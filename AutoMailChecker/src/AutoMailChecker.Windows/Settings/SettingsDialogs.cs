using System.Diagnostics;
using System.Windows;
using AutoMailChecker.Core;
using AutoMailChecker.Windows.Pause;
using Microsoft.Win32;

namespace AutoMailChecker.Windows.Settings;

/// <summary>設定画面から開く確認やファイル選択。ViewModel をテストできるよう、画面の部品はここに閉じ込める。</summary>
public interface ISettingsDialogs
{
    bool Confirm(string message);

    /// <summary>パスワードの入力。取り消したら null。</summary>
    string? AskPassword(string accountId, string user);

    /// <summary>AI サーバーの API キーの入力。取り消したら null。</summary>
    string? AskApiKey();

    string? PickExecutable();

    string? PickFolder(string? initialFolder);

    string? PickModelFile(string? current);

    ProcessInfo? PickRunningApp(IReadOnlyList<ProcessInfo> processes);

    void ShowRejudge(RejudgeViewModel viewModel);

    void OpenFolder(string path);
}

/// <summary>ISettingsDialogs の WPF 実装。</summary>
/// <param name="owner">ダイアログの親。設定ウィンドウを作る前に ViewModel を作るので、呼ぶときに取り出す</param>
public sealed class SettingsDialogs(Func<Window> owner) : ISettingsDialogs
{
    public bool Confirm(string message) =>
        MessageBox.Show(owner(), message, AppInfo.Name, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
        == MessageBoxResult.OK;

    public string? AskPassword(string accountId, string user) => AskSecret(
        Translate.Text("パスワードの登録", "Register password"),
        Translate.Text(
            $"{accountId}（{(string.IsNullOrWhiteSpace(user) ? "ユーザー名未入力" : user)}）のパスワード",
            $"Password for {accountId} ({(string.IsNullOrWhiteSpace(user) ? "no user name yet" : user)})"),
        Translate.Text(
            "iCloud は「App用パスワード」、Gmail は「アプリ パスワード」を入力してください。Windows 資格情報マネージャーにだけ保存し、画面にも設定ファイルにも出しません。",
            "Enter an app-specific password for iCloud or an app password for Gmail. It is stored only in Windows Credential Manager and is never shown on screen or written to the settings file."));

    public string? AskApiKey() => AskSecret(
        Translate.Text("API キーの登録", "Register API key"),
        Translate.Text("AI サーバーの API キー", "API key of the AI server"),
        Translate.Text(
            "llama-server を --api-key 付きで起動したときに、そこで指定したキーを入力してください。Windows 資格情報マネージャーにだけ保存し、画面にも設定ファイルにも出しません。",
            "Enter the key you passed with --api-key when you started llama-server. It is stored only in Windows Credential Manager and is never shown on screen or written to the settings file."));

    private string? AskSecret(string title, string heading, string hint)
    {
        var dialog = new PasswordWindow(title, heading, hint) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog { Filter = Translate.Text(
            "実行ファイル (*.exe)|*.exe",
            "Programs (*.exe)|*.exe"), Title = Translate.Text("停止対象のアプリを選ぶ", "Choose an app to pause for") };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = Translate.Text("フォルダーを選ぶ", "Choose a folder") };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public string? PickModelFile(string? current)
    {
        var dialog = new OpenFileDialog { Filter = Translate.Text(
            "モデル (*.gguf)|*.gguf",
            "Models (*.gguf)|*.gguf"), Title = Translate.Text("モデルファイルを選ぶ", "Choose a model file") };
        if (!string.IsNullOrWhiteSpace(current) && Path.GetDirectoryName(current) is { } directory && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public ProcessInfo? PickRunningApp(IReadOnlyList<ProcessInfo> processes)
    {
        var dialog = new RunningAppsWindow(processes) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    public void ShowRejudge(RejudgeViewModel viewModel) => new RejudgeWindow(viewModel) { Owner = owner() }.ShowDialog();

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }
}
