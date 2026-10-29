using System.Text.RegularExpressions;

namespace AutoMailChecker.Core.Configuration;

/// <summary>
/// サーバーに問い合わせずに確かめられる検証（外部仕様タブ「保存時の検証」）。
/// フォルダーの実在と迷惑メールフォルダーかどうかは、IMAP に接続してから別に確かめる。
/// </summary>
public static partial class ConfigValidator
{
    public const int LabelMaxLength = 20;
    public const int DescriptionMaxLength = 200;
    public const int ExamplesMaxCount = 3;
    public const int ExampleMaxLength = 50;

    public static IReadOnlyList<ConfigIssue> Validate(AppConfig config)
    {
        var issues = new List<ConfigIssue>();
        var accountIds = ValidateAccounts(config.Accounts, issues);
        ValidateIgnoreSenders(config.IgnoreSenders, issues);
        ValidateItems(config.Items, accountIds, issues);
        ValidateFolderConflicts(config, issues);
        ValidatePolicy(config.Policy, issues);
        if (config.Trial.MaxMessages is < 1)
        {
            Add(issues, ConfigSection.Trial, "trial.maxMessages", Translate.Text(
                $"1 以上の数字か、null（上限なし）を指定してください（現在 {config.Trial.MaxMessages}）。",
                $"Specify a number of 1 or more, or null for no limit (currently {config.Trial.MaxMessages})."));
        }

        ValidateRange(issues, ConfigSection.History, "history.retentionDays", config.History.RetentionDays, 7, 365);
        ValidateLlm(config.Llm, issues);
        ValidatePause(config.Pause, issues);
        if (!Enum.IsDefined(config.Ui.Language))
        {
            Add(issues, ConfigSection.Ui, "ui.language", Translate.Text("ja か en を指定してください。", "Specify ja or en."));
        }
        return issues;
    }

    private static HashSet<string> ValidateAccounts(IReadOnlyList<AccountConfig> accounts, List<ConfigIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var idsIgnoreCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            var path = $"accounts[{i}]";
            if (!AccountIdPattern().IsMatch(a.Id))
            {
                Add(issues, ConfigSection.Accounts, $"{path}.id", Translate.Text(
                    "アカウント ID は英数字とハイフンだけで指定してください。",
                    "Use only letters, digits and hyphens for the account ID."));
            }
            else if (!idsIgnoreCase.Add(a.Id))
            {
                Add(issues, ConfigSection.Accounts, $"{path}.id", Translate.Text(
                    $"アカウント ID「{a.Id}」が重複しています。",
                    $"The account ID \"{a.Id}\" is used more than once."));
            }
            else
            {
                ids.Add(a.Id);
            }

            if (string.IsNullOrWhiteSpace(a.Host))
            {
                Add(issues, ConfigSection.Accounts, $"{path}.host", Translate.Text("IMAP サーバーを指定してください。", "Specify the IMAP server."));
            }

            ValidateRange(issues, ConfigSection.Accounts, $"{path}.port", a.Port, 1, 65535);

            if (string.IsNullOrWhiteSpace(a.User))
            {
                Add(issues, ConfigSection.Accounts, $"{path}.user", Translate.Text("ログイン名を指定してください。", "Specify the user name."));
            }

            if (a.Auth != AuthMethod.AppPassword)
            {
                Add(issues, ConfigSection.Accounts, $"{path}.auth", Translate.Text(
                    "現在はアプリ用パスワード（appPassword）だけに対応しています。",
                    "Only app passwords (appPassword) are supported at the moment."));
            }

            if (a.WatchFolders.Count == 0 || a.WatchFolders.Any(string.IsNullOrWhiteSpace))
            {
                Add(issues, ConfigSection.Accounts, $"{path}.watchFolders", Translate.Text(
                    "監視するフォルダーを1つ以上指定してください。",
                    "Specify at least one folder to watch."));
            }
        }

        return ids;
    }

    private static void ValidateIgnoreSenders(IReadOnlyList<string> senders, List<ConfigIssue> issues)
    {
        for (var i = 0; i < senders.Count; i++)
        {
            var s = senders[i]?.Trim() ?? "";
            var at = s.IndexOf('@');
            if (at < 0 || at == s.Length - 1 || s.IndexOf('@', at + 1) >= 0 || s.Any(char.IsWhiteSpace))
            {
                Add(issues, ConfigSection.IgnoreSenders, $"ignoreSenders[{i}]",
                    Translate.Text(
                        "「a@example.com」（完全一致）か「@example.com」（ドメイン一致）の形で指定してください。",
                        "Use the form \"a@example.com\" (exact match) or \"@example.com\" (domain match)."));
            }
        }
    }

    private static void ValidateItems(IReadOnlyList<ItemDefinition> items, HashSet<string> accountIds, List<ConfigIssue> issues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var priorities = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var path = $"items[{i}]";
            var id = item.Id.Value ?? "";

            if (!ItemIdPattern().IsMatch(id))
            {
                Add(issues, ConfigSection.Items, $"{path}.id", Translate.Text(
                    "項目 ID は英数字・ハイフン・アンダースコアだけで指定してください。",
                    "Use only letters, digits, hyphens and underscores for the item ID."));
            }
            else if (string.Equals(id, ItemId.AdvertisementReserved, StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, ConfigSection.Items, $"{path}.id", Translate.Text(
                    $"「{ItemId.AdvertisementReserved}」は予約 ID なので使えません。",
                    $"\"{ItemId.AdvertisementReserved}\" is a reserved ID and cannot be used."));
            }
            else if (!ids.Add(id))
            {
                Add(issues, ConfigSection.Items, $"{path}.id", Translate.Text(
                    $"項目 ID「{id}」が重複しています。",
                    $"The item ID \"{id}\" is used more than once."));
            }

            var label = item.Label?.Trim() ?? "";
            if (label.Length is 0 or > LabelMaxLength)
            {
                Add(issues, ConfigSection.Items, $"{path}.label", Translate.Text(
                    $"表示名は1〜{LabelMaxLength}文字で指定してください。",
                    $"The display name must be 1 to {LabelMaxLength} characters."));
            }
            else if (!labels.Add(label))
            {
                Add(issues, ConfigSection.Items, $"{path}.label", Translate.Text(
                    $"表示名「{label}」が重複しています。",
                    $"The display name \"{label}\" is used more than once."));
            }

            var description = item.Description?.Trim() ?? "";
            if (description.Length is 0 or > DescriptionMaxLength)
            {
                Add(issues, ConfigSection.Items, $"{path}.description", Translate.Text(
                    $"AI への説明は1〜{DescriptionMaxLength}文字で指定してください。",
                    $"The description for the AI must be 1 to {DescriptionMaxLength} characters."));
            }

            if (!priorities.Add(item.Priority))
            {
                Add(issues, ConfigSection.Items, $"{path}.priority", Translate.Text(
                    $"優先度 {item.Priority} が重複しています。",
                    $"Priority {item.Priority} is used more than once."));
            }

            ValidateExamples(issues, $"{path}.examples", item.Examples);
            ValidateExamples(issues, $"{path}.counterExamples", item.CounterExamples);
            ValidateFolderMap(issues, ConfigSection.Items, $"{path}.folders", item.Folders, accountIds);
        }
    }

    private static void ValidateExamples(List<ConfigIssue> issues, string path, IReadOnlyList<string> examples)
    {
        if (examples.Count > ExamplesMaxCount)
        {
            Add(issues, ConfigSection.Items, path, Translate.Text($"例は{ExamplesMaxCount}件までです。", $"Up to {ExamplesMaxCount} examples are allowed."));
        }

        for (var i = 0; i < examples.Count; i++)
        {
            var e = examples[i]?.Trim() ?? "";
            if (e.Length is 0 or > ExampleMaxLength)
            {
                Add(issues, ConfigSection.Items, $"{path}[{i}]", Translate.Text(
                    $"例は1件あたり1〜{ExampleMaxLength}文字で指定してください。",
                    $"Each example must be 1 to {ExampleMaxLength} characters."));
            }
        }
    }

    private static void ValidateFolderMap(
        List<ConfigIssue> issues, ConfigSection section, string path,
        IReadOnlyDictionary<string, string> folders, HashSet<string> accountIds)
    {
        foreach (var (accountId, folder) in folders)
        {
            if (!accountIds.Contains(accountId))
            {
                Add(issues, section, $"{path}.{accountId}", Translate.Text(
                    $"アカウント「{accountId}」は accounts にありません。振り分け先にはそのアカウント自身のフォルダーだけを指定できます。",
                    $"The account \"{accountId}\" is not in accounts. A destination can only be a folder of that account itself."));
            }
            else if (!string.IsNullOrWhiteSpace(folder) && AppInfo.IsInbox(folder.Trim()))
            {
                Add(issues, section, $"{path}.{accountId}", Translate.Text("受信トレイは振り分け先に指定できません。", "The inbox cannot be a destination."));
            }
        }
    }

    /// <summary>同じアカウントの中で、振り分け先フォルダーが項目間で重複しないこと。</summary>
    private static void ValidateFolderConflicts(AppConfig config, List<ConfigIssue> issues)
    {
        // アカウント ID → (フォルダー名 → 最初に使った場所)
        var used = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        void Check(ConfigSection section, string path, string owner, IReadOnlyDictionary<string, string> folders)
        {
            foreach (var (accountId, rawFolder) in folders)
            {
                if (string.IsNullOrWhiteSpace(rawFolder))
                {
                    continue;
                }

                var folder = rawFolder.Trim();
                if (!used.TryGetValue(accountId, out var byFolder))
                {
                    used[accountId] = byFolder = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                if (byFolder.TryGetValue(folder, out var firstOwner))
                {
                    Add(issues, section, $"{path}.{accountId}", Translate.Text(
                        $"フォルダー「{folder}」は {firstOwner} でも使われています。",
                        $"The folder \"{folder}\" is also used by {firstOwner}."));
                }
                else
                {
                    byFolder[folder] = owner;
                }
            }
        }

        for (var i = 0; i < config.Items.Count; i++)
        {
            var item = config.Items[i];
            Check(ConfigSection.Items, $"items[{i}].folders", Translate.Text($"項目「{item.Label}」", $"the item \"{item.Label}\""), item.Folders);
        }
    }

    private static void ValidatePolicy(PolicyConfig policy, List<ConfigIssue> issues)
    {
        if (policy.MinConfidence is not (Confidence.High or Confidence.Medium))
        {
            Add(issues, ConfigSection.Policy, "policy.minConfidence", Translate.Text("high か medium を指定してください。", "Specify high or medium."));
        }
    }

    private static void ValidateLlm(LlmConfig llm, List<ConfigIssue> issues)
    {
        if (llm.UsesEndpoint)
        {
            // 起動済みのサーバーに繋ぐ。llama-server.exe とモデルファイルは使わない
            if (LlmEndpoint.TryParse(llm.Endpoint, out var error) is null)
            {
                Add(issues, ConfigSection.Llm, "llm.endpoint", error!);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(llm.ServerExe))
            {
                Add(issues, ConfigSection.Llm, "llm.serverExe", Translate.Text(
                    "接続先 URL を使わないときは、llama-server.exe の場所を指定してください。",
                    "When no server URL is set, specify the location of llama-server.exe."));
            }

            if (string.IsNullOrWhiteSpace(llm.Model))
            {
                Add(issues, ConfigSection.Llm, "llm.model", Translate.Text(
                    "接続先 URL を使わないときは、モデルファイルを指定してください。",
                    "When no server URL is set, specify the model file."));
            }
        }

        ValidateRange(issues, ConfigSection.Llm, "llm.port", llm.Port, 1, 65535);
        ValidateRange(issues, ConfigSection.Llm, "llm.ctxSize", llm.CtxSize, 2048, 262144);
        ValidateRange(issues, ConfigSection.Llm, "llm.threads", llm.Threads, 1, 256);
        ValidateRange(issues, ConfigSection.Llm, "llm.maxBodyChars", llm.MaxBodyChars, 500, 20000);
        if (llm.NCpuMoe is { } moe)
        {
            ValidateRange(issues, ConfigSection.Llm, "llm.nCpuMoe", moe, 0, 1000);
        }

        ValidateRange(issues, ConfigSection.Llm, "llm.lowLoad.gpuLayers", llm.LowLoad.GpuLayers, 0, 1000);
        ValidateRange(issues, ConfigSection.Llm, "llm.lowLoad.threads", llm.LowLoad.Threads, 1, 256);

        for (var i = 0; i < llm.ExtraArgs.Count; i++)
        {
            if (IsForbiddenLlmArg(llm.ExtraArgs[i]))
            {
                Add(issues, ConfigSection.Llm, $"llm.extraArgs[{i}]",
                    Translate.Text(
                        $"「{llm.ExtraArgs[i]}」は指定できません（待ち受け先・API キー・Web UI・ツール実行・モデルはアプリが決めます）。",
                        $"\"{llm.ExtraArgs[i]}\" cannot be specified (the app decides the listen address, API key, Web UI, tool execution and model)."));
            }
        }
    }

    /// <summary>
    /// llama-server の追加引数として渡せないもの（仕様書11章「AIの権限を閉じ込める5層」の1層目）。
    /// 待ち受け先と API キーと Web UI 無効はアプリが必ず付け、ツール実行・MCP・エージェント機能は起動させない。
    /// </summary>
    public static bool IsForbiddenLlmArg(string arg)
    {
        var name = (arg ?? "").Trim();
        var equals = name.IndexOf('=');
        if (equals > 0)
        {
            name = name[..equals];
        }

        return ForbiddenLlmArgs.Contains(name)
            || ForbiddenLlmArgPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> ForbiddenLlmArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--host", "--port", "--api-key", "--api-key-file", "--ui", "--webui", "--path",
        "-m", "--model", "-mu", "--model-url", "-hf", "-hfr", "--hf-repo", "-hff", "--hf-file",
    };

    private static readonly string[] ForbiddenLlmArgPrefixes =
        ["--tools", "--mcp", "--agent", "--ui-mcp", "--webui-mcp", "--ui-config", "--webui-config"];

    private static void ValidatePause(PauseConfig pause, List<ConfigIssue> issues)
    {
        ValidateRange(issues, ConfigSection.Pause, "pause.pollSeconds", pause.PollSeconds, 2, 60);
        ValidateRange(issues, ConfigSection.Pause, "pause.resumeDelaySeconds", pause.ResumeDelaySeconds, 0, 600);

        for (var i = 0; i < pause.Apps.Count; i++)
        {
            var rule = pause.Apps[i];
            var path = $"pause.apps[{i}]";
            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                Add(issues, ConfigSection.Pause, $"{path}.name", Translate.Text("名前を指定してください。", "Specify a name."));
            }

            var hasExe = rule.Exe.Count > 0;
            if (hasExe && rule.Exe.Any(string.IsNullOrWhiteSpace))
            {
                Add(issues, ConfigSection.Pause, $"{path}.exe", Translate.Text("実行ファイル名に空の値があります。", "One of the executable names is empty."));
            }

            if (!hasExe && string.IsNullOrWhiteSpace(rule.TitleContains))
            {
                Add(issues, ConfigSection.Pause, path, Translate.Text(
                    "実行ファイル名かウィンドウタイトルのどちらかを指定してください。",
                    "Specify an executable name or a window title."));
            }

            if (!Enum.IsDefined(rule.Mode))
            {
                Add(issues, ConfigSection.Pause, $"{path}.mode", Translate.Text("stop か lowLoad を指定してください。", "Specify stop or lowLoad."));
            }
        }
    }

    private static void ValidateRange(List<ConfigIssue> issues, ConfigSection section, string path, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            Add(issues, section, path, Translate.Text(
                $"{min}〜{max} の範囲で指定してください（現在 {value}）。",
                $"Specify a value from {min} to {max} (currently {value})."));
        }
    }

    private static void Add(List<ConfigIssue> issues, ConfigSection section, string path, string message) =>
        issues.Add(new ConfigIssue(section, path, message));

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex AccountIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ItemIdPattern();
}
