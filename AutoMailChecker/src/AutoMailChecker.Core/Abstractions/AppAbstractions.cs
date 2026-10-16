namespace AutoMailChecker.Core;

/// <summary>現在の運転モード。手動操作とゲーム・全画面の検知から決まる。</summary>
public interface IRunModeSource
{
    RunModeState Current { get; }

    event EventHandler<RunModeState>? Changed;
}

/// <summary>トレイからの手動操作を受ける運転モードの窓口。</summary>
public interface IRunModeController : IRunModeSource
{
    /// <summary>判定保留（一時停止）。duration が null なら再開するまで。</summary>
    void PauseFor(TimeSpan? duration);

    /// <summary>完全停止（llama-server を終了してメモリを空ける）。</summary>
    void Stop();

    /// <summary>手動の一時停止・完全停止を解き、いま検知している完全停止の原因も、それが終わるまでは無視する。</summary>
    void Resume();

    /// <summary>いま検知している停止対象（設定画面の「現在の検知」に出す）。</summary>
    IReadOnlyList<PauseDetection> CurrentDetections { get; }
}

/// <summary>
/// Windows 資格情報マネージャー（汎用資格情報）。パスワードは画面にも設定ファイルにも出さない。
/// </summary>
public interface ICredentialStore
{
    /// <summary>登録されていなければ null。</summary>
    string? ReadSecret(string key);

    void WriteSecret(string key, string userName, string secret);
}

/// <summary>トレイアイコンに出すエラーの報告口。Source（アカウント ID など）ごとに1件を持つ。</summary>
public interface IStatusReporter
{
    /// <summary>同じ Source のエラーは置き換える。</summary>
    void ReportError(string source, string message);

    void ClearError(string source);
}

/// <summary>トレイアイコンに出す状態。</summary>
public interface IAppStatus
{
    AppStatusSnapshot Current { get; }

    event EventHandler<AppStatusSnapshot>? Changed;
}

/// <summary>トレイのアイコンとツールチップに出す内容。</summary>
public sealed record AppStatusSnapshot(
    RunMode Mode,
    string? ModeReason,
    DateTimeOffset? HeldUntil,
    int PendingCount,
    TrialProgress? Trial,
    IReadOnlyList<StatusError> Errors)
{
    public static AppStatusSnapshot Initial { get; } = new(RunMode.Running, null, null, 0, null, []);
}

/// <summary>お試し仕分けの進み具合。</summary>
public sealed record TrialProgress(long TrialRunId, int Processed, int Total);

/// <summary>トレイに出すエラー。Source はアカウント ID や「設定」など、どこで起きたかを示す。</summary>
public sealed record StatusError(string Source, string Message);
