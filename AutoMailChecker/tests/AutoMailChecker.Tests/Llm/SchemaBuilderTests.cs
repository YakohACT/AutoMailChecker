using System.Text.Json.Nodes;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Llm;

namespace AutoMailChecker.Tests.Llm;

public class SchemaBuilderTests
{
    private static readonly IReadOnlyList<ItemDefinition> DefaultItems = DefaultConfig.Instance.Items;

    [Fact]
    public void 選択肢は有効な項目のIDだけを優先度順に入れる()
    {
        IReadOnlyList<ItemDefinition> items = [.. DefaultItems.Take(3), DefaultItems[3] with { Enabled = false }];

        var schema = SchemaBuilder.Build(items);

        var choices = schema["properties"]!["matched_items"]!["items"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>());
        Assert.Equal(["important", "reservation", "payment"], choices);
    }

    [Fact]
    public void 有効な項目がなければ空の配列に限る()
    {
        var schema = SchemaBuilder.Build([DefaultItems[0] with { Enabled = false }]);

        var matched = schema["properties"]!["matched_items"]!;
        Assert.Equal(0, matched["maxItems"]!.GetValue<int>());
    }

    [Fact]
    public void 出力の形は仕様書のとおり()
    {
        var schema = SchemaBuilder.Build(DefaultItems);

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal(["is_advertisement", "matched_items", "confidence", "summary", "reason"],
            schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["high", "medium", "low"],
            schema["properties"]!["confidence"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("boolean", schema["properties"]!["is_advertisement"]!["type"]!.GetValue<string>());
        Assert.IsType<JsonObject>(schema["properties"]!["summary"]);
    }
}
