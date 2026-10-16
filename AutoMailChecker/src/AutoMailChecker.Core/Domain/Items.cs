using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMailChecker.Core;

/// <summary>仕分け項目の ID。追加時に自動で振り、以後は変えない（判定履歴との対応を保つため）。</summary>
[JsonConverter(typeof(ItemIdJsonConverter))]
public readonly record struct ItemId(string Value)
{
    /// <summary>誤判定の記録で「広告」を表す予約 ID。項目には使えない。</summary>
    public const string AdvertisementReserved = "ad";

    public override string ToString() => Value;
}

/// <summary>
/// 設定の項目定義。Priority が小さいほど優先する。
/// 優先度は AI には渡さず、振り分け先を決めるときだけアプリが使う。
/// </summary>
public sealed record ItemDefinition
{
    public ItemId Id { get; init; }
    public string Label { get; init; } = "";
    public int Priority { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Examples { get; init; } = [];
    public IReadOnlyList<string> CounterExamples { get; init; } = [];

    /// <summary>アカウント ID → そのアカウント自身のフォルダー名。未設定のアカウントでは移動しない。</summary>
    public IReadOnlyDictionary<string, string> Folders { get; init; } = new Dictionary<string, string>();

    /// <summary>false なら AI の選択肢から外す。設定内容は残る。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>指定アカウントの振り分け先。未設定なら null。</summary>
    public string? FolderFor(string accountId) =>
        Folders.TryGetValue(accountId, out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder.Trim() : null;
}

public sealed class ItemIdJsonConverter : JsonConverter<ItemId>
{
    public override ItemId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("項目 ID は文字列で指定してください。");
        }

        return new ItemId(reader.GetString() ?? "");
    }

    public override void Write(Utf8JsonWriter writer, ItemId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
