using System.Text.Json.Nodes;
using AutoMailChecker.Core;

namespace AutoMailChecker.Llm;

/// <summary>
/// 出力形式（JSON スキーマ）。llama-server の response_format で渡し、生成そのものをこの形に制約する。
/// matched_items の選択肢は有効な項目の ID から作る。AI が返せるのは判定値と設定済みの項目 ID だけになる。
/// </summary>
public static class SchemaBuilder
{
    public static readonly IReadOnlyList<string> RequiredProperties =
        ["matched_items", "confidence", "summary", "reason"];

    public static JsonObject Build(IReadOnlyList<ItemDefinition> items)
    {
        var selectable = PromptBuilder.SelectableItems(items);

        // 有効な項目が1つもなければ、matched_items は空の配列に限る
        var matchedItems = selectable.Count == 0
            ? new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["maxItems"] = 0,
            }
            : new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["enum"] = new JsonArray(selectable.Select(i => (JsonNode?)i.Id.Value).ToArray()),
                },
            };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["matched_items"] = matchedItems,
                ["confidence"] = new JsonObject { ["enum"] = new JsonArray("high", "medium", "low") },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["reason"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(RequiredProperties.Select(p => (JsonNode?)p).ToArray()),
        };
    }
}
