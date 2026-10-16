namespace AutoMailChecker.Core;

/// <summary>
/// 除外リストとの照合。「a@b」は完全一致、「@b」はドメイン一致で、大文字小文字は区別しない。
/// 一致したメールは本文を取得せず、AI にも渡さない。設定が変わったら作り直す。
/// </summary>
public sealed class SenderFilter
{
    private readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);

    public SenderFilter(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim() ?? "";
            if (pattern.Length > 1 && pattern[0] == '@')
            {
                _domains.Add(pattern[1..]);
            }
            else if (pattern.Contains('@'))
            {
                _addresses.Add(pattern);
            }
        }
    }

    /// <param name="from">From ヘッダー。「表示名 &lt;a@example.com&gt;」の形でもよい。</param>
    public bool IsExcluded(string from)
    {
        var address = ExtractAddress(from);
        if (address is null)
        {
            return false;
        }

        if (_addresses.Contains(address))
        {
            return true;
        }

        var at = address.LastIndexOf('@');
        return at >= 0 && _domains.Contains(address[(at + 1)..]);
    }

    internal static string? ExtractAddress(string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        var value = from.Trim();
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            value = value[(open + 1)..close].Trim();
        }

        return value.Contains('@') ? value : null;
    }
}
