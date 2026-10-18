namespace AutoMailChecker.Core;

/// <summary>AI が自己申告する確信度。値が小さいほど確信が強い。</summary>
public enum Confidence
{
    High,
    Medium,
    Low,
}

public static class ConfidenceExtensions
{
    /// <summary>このしきい値以上の確信度か（High は Medium 以上を満たす）。</summary>
    public static bool IsAtLeast(this Confidence value, Confidence threshold) => value <= threshold;

    public static string ToDbValue(this Confidence value) => value switch
    {
        Confidence.High => "high",
        Confidence.Medium => "medium",
        Confidence.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static Confidence ParseConfidence(string value) => value switch
    {
        "high" => Confidence.High,
        "medium" => Confidence.Medium,
        "low" => Confidence.Low,
        _ => throw new FormatException($"不明な確信度です: {value}"),
    };
}

/// <summary>AI が1通について返す結果。移動先やフォルダー名は含まない。</summary>
public sealed record Judgement(
    IReadOnlyList<ItemId> MatchedItems,
    Confidence Confidence,
    string Summary,
    string Reason);
