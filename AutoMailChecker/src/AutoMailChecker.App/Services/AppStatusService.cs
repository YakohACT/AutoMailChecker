using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Services;

/// <summary>
/// トレイに出す状態（運転モード、未処理件数、お試しの進み具合、エラー）をまとめる。
/// 各サービスがここへ報告し、トレイは Changed を受けて表示を変える。
/// </summary>
public sealed class AppStatusService : IAppStatus, IStatusReporter
{
    public const string ConfigErrorSource = "設定";

    private readonly object _gate = new();
    private AppStatusSnapshot _current = AppStatusSnapshot.Initial;

    public AppStatusService(IConfigProvider config)
    {
        SetConfigIssues(config.Issues);
        config.Changed += (_, e) => SetConfigIssues(e.Issues);
    }

    public AppStatusSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AppStatusSnapshot>? Changed;

    public void SetMode(RunMode mode, string? reason, DateTimeOffset? heldUntil) =>
        Update(s => s with { Mode = mode, ModeReason = reason, HeldUntil = heldUntil });

    public void SetPendingCount(int count) => Update(s => s with { PendingCount = count });

    public void SetTrial(TrialProgress? trial) => Update(s => s with { Trial = trial });

    /// <summary>同じ Source のエラーは置き換える。内容が同じなら何もしない（変更を知らせない）。</summary>
    public void ReportError(string source, string message) => Update(s =>
    {
        var error = new StatusError(source, message);
        if (s.Errors.Contains(error))
        {
            return s;
        }

        var errors = s.Errors.Where(e => e.Source != source).Append(error).ToList();
        return s with { Errors = errors };
    });

    public void ClearError(string source) => Update(s =>
        s.Errors.Any(e => e.Source == source)
            ? s with { Errors = s.Errors.Where(e => e.Source != source).ToList() }
            : s);

    private void SetConfigIssues(IReadOnlyList<ConfigIssue> issues)
    {
        if (issues.Count == 0)
        {
            ClearError(ConfigErrorSource);
            return;
        }

        var message = issues.Count == 1 ? issues[0].ToString() : Translate.Text(
            $"{issues[0]}（ほか{issues.Count - 1}件）",
            $"{issues[0]} (+{issues.Count - 1} more)");
        ReportError(ConfigErrorSource, message);
    }

    private void Update(Func<AppStatusSnapshot, AppStatusSnapshot> change)
    {
        AppStatusSnapshot next;
        lock (_gate)
        {
            next = change(_current);
            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(this, next);
    }
}
