namespace AutoMailChecker.Core;

/// <summary>
/// 処理待ちにメールを積んだことを処理側へ知らせる。何度 Notify しても、待っている側は1回だけ起きる。
/// </summary>
public sealed class QueueSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // すでに知らせてあり、まだ受け取られていない
        }
    }

    /// <summary>知らせが来たら true、時間切れなら false。</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);
}
