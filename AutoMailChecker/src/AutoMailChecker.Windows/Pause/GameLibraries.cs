using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AutoMailChecker.Windows.Pause;

/// <summary>固定ルール（Steam・Epic）の対象になるゲームのインストール先。</summary>
/// <param name="SteamGameRoots">各 Steam ライブラリの steamapps\common</param>
/// <param name="EpicGameRoots">Epic のゲームごとのインストール先</param>
public sealed record GameLibraries(IReadOnlyList<string> SteamGameRoots, IReadOnlyList<string> EpicGameRoots)
{
    public static GameLibraries Empty { get; } = new([], []);
}

public interface IGameLibraryLocator
{
    GameLibraries Locate();
}

/// <summary>
/// Steam と Epic のゲームのインストール先を読む（外部仕様タブ）。
/// - Steam: レジストリ HKCU\Software\Valve\Steam の SteamPath → steamapps\libraryfolders.vdf の各ライブラリ
/// - Epic: C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests の各ファイルの InstallLocation
/// </summary>
public sealed partial class GameLibraryLocator(ILogger<GameLibraryLocator> logger) : IGameLibraryLocator
{
    public GameLibraries Locate() => new(LocateSteam(), LocateEpic());

    /// <summary>libraryfolders.vdf からライブラリの場所を読む（"path" の値。\\ は \ に戻す）。</summary>
    public static IReadOnlyList<string> ParseSteamLibraryFolders(string vdf) =>
        SteamPathEntry().Matches(vdf)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal))
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>Epic のマニフェスト（JSON）の InstallLocation。</summary>
    public static string? ParseEpicInstallLocation(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            return document.RootElement.TryGetProperty("InstallLocation", out var location)
                   && location.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(location.GetString())
                ? location.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private List<string> LocateSteam()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steamPath || string.IsNullOrWhiteSpace(steamPath))
            {
                return [];
            }

            steamPath = Path.GetFullPath(steamPath.Replace('/', '\\'));
            var libraries = new List<string> { steamPath };
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                libraries.AddRange(ParseSteamLibraryFolders(File.ReadAllText(vdf)));
            }

            return libraries
                .Select(library => Path.Combine(library, "steamapps", "common"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            logger.LogWarning("Steam のライブラリの場所を読めませんでした: {Message}", ex.Message);
            return [];
        }
    }

    private List<string> LocateEpic()
    {
        try
        {
            var manifests = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifests))
            {
                return [];
            }

            return Directory.EnumerateFiles(manifests, "*.item")
                .Select(file => ParseEpicInstallLocation(File.ReadAllText(file)))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Epic のゲームのインストール先を読めませんでした: {Message}", ex.Message);
            return [];
        }
    }

    [GeneratedRegex("\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathEntry();
}
