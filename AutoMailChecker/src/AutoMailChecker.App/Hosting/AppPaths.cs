using AutoMailChecker.Core;

namespace AutoMailChecker.Hosting;

/// <summary>
/// ファイルの配置（外部仕様タブ）。設定は %APPDATA%、状態 DB とログは OneDrive 同期の対象外の %LOCALAPPDATA% に置く。
/// </summary>
public sealed class AppPaths(string configDirectory, string dataDirectory)
{
    public static AppPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name));

    public string ConfigDirectory { get; } = configDirectory;
    public string DataDirectory { get; } = dataDirectory;

    public string ConfigFile => Path.Combine(ConfigDirectory, "config.jsonc");

    /// <summary>画面から保存する前の内容の退避先。</summary>
    public string ConfigBackupFile => ConfigFile + ".bak";

    public string StateDatabase => Path.Combine(DataDirectory, "state.db");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>llama-server の API キーなど、実行中だけ使うファイル。</summary>
    public string RunDirectory => Path.Combine(DataDirectory, "run");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RunDirectory);
    }
}
