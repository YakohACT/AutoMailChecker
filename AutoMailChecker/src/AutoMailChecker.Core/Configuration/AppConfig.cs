using System.Text.Json.Serialization;

namespace AutoMailChecker.Core.Configuration;

/// <summary>
/// config.jsonc の内容。不変で、変更するときは with で新しいインスタンスを作る。
/// 既定値は外部仕様タブの「設定ファイルの項目」に合わせる。
/// </summary>
public sealed record AppConfig
{
    public IReadOnlyList<AccountConfig> Accounts { get; init; } = [];

    /// <summary>a@b は完全一致、@b はドメイン一致。大文字小文字は区別しない。</summary>
    public IReadOnlyList<string> IgnoreSenders { get; init; } = [];

    public IReadOnlyList<ItemDefinition> Items { get; init; } = [];
    public PolicyConfig Policy { get; init; } = new();
    public TrialConfig Trial { get; init; } = new();
    public HistoryConfig History { get; init; } = new();
    public LlmConfig Llm { get; init; } = new();
    public PauseConfig Pause { get; init; } = new();
    public UiConfig Ui { get; init; } = new();

    public AccountConfig? FindAccount(string accountId) =>
        Accounts.FirstOrDefault(a => a.Id == accountId);
}

public enum AuthMethod
{
    [JsonStringEnumMemberName("appPassword")]
    AppPassword,

    /// <summary>将来の移行先。現時点では未実装。</summary>
    [JsonStringEnumMemberName("oauth2")]
    OAuth2,
}

public sealed record AccountConfig
{
    /// <summary>英数字とハイフンのみ。項目の folders のキーになる。</summary>
    public string Id { get; init; } = "";

    public string Host { get; init; } = "";
    public int Port { get; init; } = 993;
    public string User { get; init; } = "";
    public AuthMethod Auth { get; init; } = AuthMethod.AppPassword;

    /// <summary>資格情報マネージャーの名前。省略時は「AutoMailChecker/＜id＞」。</summary>
    public string? CredentialKey { get; init; }

    /// <summary>監視するフォルダー。迷惑メールフォルダーは指定できない。</summary>
    public IReadOnlyList<string> WatchFolders { get; init; } = [AppInfo.InboxFolder];

    /// <summary>false なら判定だけ行い、移動しない。</summary>
    public bool AllowMove { get; init; } = true;

    [JsonIgnore]
    public string EffectiveCredentialKey =>
        string.IsNullOrWhiteSpace(CredentialKey) ? $"{AppInfo.Name}/{Id}" : CredentialKey;
}

public sealed record PolicyConfig
{
    /// <summary>エラー時のポップアップ。既定ではトレイアイコンだけで示す。</summary>
    public bool ErrorPopup { get; init; }

    /// <summary>これ未満の確信度なら移動しない。High か Medium のみ。</summary>
    public Confidence MinConfidence { get; init; } = Confidence.Medium;

    /// <summary>新着の自動仕分け。お試し仕分けで確かめてから true にする。</summary>
    public bool AutoSort { get; init; }
}

public sealed record TrialConfig
{
    /// <summary>お試し仕分けの件数上限（1以上）。null なら上限なし（既定）。</summary>
    public int? MaxMessages { get; init; }
}

public sealed record HistoryConfig
{
    /// <summary>判定履歴の保持日数（7〜365）。</summary>
    public int RetentionDays { get; init; } = 90;
}

public sealed record LlmConfig
{
    /// <summary>
    /// 起動済みの AI サーバー（llama-server など）の接続先 URL。この PC のアドレスだけ指定できる。
    /// 空にすると、アプリが <see cref="ServerExe"/> と <see cref="Model"/> で llama-server を起動する。
    /// </summary>
    public string Endpoint { get; init; } = LlmEndpoint.DefaultUrl;

    /// <summary>接続先 URL の起動済みサーバーを使うか（false ならアプリが llama-server を起動する）。</summary>
    [JsonIgnore]
    public bool UsesEndpoint => !string.IsNullOrWhiteSpace(Endpoint);

    public string ServerExe { get; init; } = "";
    public string Model { get; init; } = "";
    public int Port { get; init; } = 8089;
    public int CtxSize { get; init; } = 8192;

    /// <summary>MoE モデル（35B-A3B など）のときだけ指定する。</summary>
    public int? NCpuMoe { get; init; }

    public int Threads { get; init; } = 4;
    public int MaxBodyChars { get; init; } = 4000;

    /// <summary>
    /// 追加の起動引数。--host、--api-key-file、--no-webui などの安全に関わる引数は
    /// アプリ側で必ず付けるので、ここには書かなくてよい。
    /// </summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = ["--no-mmproj", "--reasoning", "off", "--prio", "-1"];

    public LowLoadConfig LowLoad { get; init; } = new();
}

/// <summary>「低負荷で継続」中の llama-server の起動設定。</summary>
public sealed record LowLoadConfig
{
    public int GpuLayers { get; init; }
    public int Threads { get; init; } = 2;
}

public enum PauseAction
{
    /// <summary>完全停止</summary>
    Stop,
    /// <summary>低負荷で継続</summary>
    LowLoad,
}

/// <summary>画面の表示。</summary>
public sealed record UiConfig
{
    /// <summary>画面の言語（ja / en）。全般タブで選ぶとすぐ切り替わる。</summary>
    public UiLanguage Language { get; init; } = UiLanguage.Japanese;
}

public sealed record PauseConfig
{
    /// <summary>DMM GAME PLAYER のゲームのインストール先。</summary>
    public string? DmmGameFolder { get; init; }

    /// <summary>設定画面から増減する停止対象。Steam・Epic・DMM の固定ルールはここに書かない。</summary>
    public IReadOnlyList<PauseAppRule> Apps { get; init; } = [];

    public bool FullscreenDetect { get; init; } = true;

    /// <summary>検知の間隔（2〜60 秒）。</summary>
    public int PollSeconds { get; init; } = 5;

    /// <summary>検知の解除から再開までの待ち時間（0〜600 秒）。</summary>
    public int ResumeDelaySeconds { get; init; } = 60;
}

public sealed record PauseAppRule
{
    public string Name { get; init; } = "";

    /// <summary>実行ファイル名。完全一致で、大文字小文字は区別しない。</summary>
    public IReadOnlyList<string> Exe { get; init; } = [];

    /// <summary>ウィンドウタイトルの部分一致。Exe と両方あるときは両方に一致したものだけが対象。</summary>
    public string? TitleContains { get; init; }

    public PauseAction Mode { get; init; } = PauseAction.Stop;

    /// <summary>一時的に無効にするときは false。</summary>
    public bool Enabled { get; init; } = true;
}
