namespace AutoMailChecker.Services;

/// <summary>
/// 処理中の1通と、llama-server の起動・終了・切り替えを調停する。
/// 切り替える側は HoldAsync で新しい1通を取らせないようにし、処理中の1通が終わるのを待ってから切り替える
/// （仕様書10章: 完全停止は処理中の1件が終わってから llama-server を終了する）。
/// </summary>
public sealed class ProcessingGate
{
    private readonly object _gate = new();
    private bool _busy;
    private int _holds;
    private TaskCompletionSource? _idle;

    /// <summary>1通の処理を始める。切り替え中、または別の1通を処理中なら false。</summary>
    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_busy || _holds > 0)
            {
                return false;
            }

            _busy = true;
            return true;
        }
    }

    /// <summary>1通の処理が終わった。</summary>
    public void Exit()
    {
        TaskCompletionSource? idle;
        lock (_gate)
        {
            _busy = false;
            idle = _idle;
            _idle = null;
        }

        idle?.TrySetResult();
    }

    /// <summary>新しい1通を取らせないようにし、処理中の1通が終わるのを待つ。戻り値を Dispose すると処理を再開できる。</summary>
    public async Task<IDisposable> HoldAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            _holds++;
            if (!_busy)
            {
                return new Hold(this);
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _idle.Task;
        }

        try
        {
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }

        return new Hold(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            _holds--;
        }
    }

    private sealed class Hold(ProcessingGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
