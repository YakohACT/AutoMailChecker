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

    public string? AskPassword(string accountId, string user)
    {
        var dialog = new PasswordWindow(accountId, user) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public string? PickExecutable()
    {
        var dialog = new OpenFileDialog { Filter = "実行ファイル (*.exe)|*.exe", Title = "停止対象のアプリを選ぶ" };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = "フォルダーを選ぶ" };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public string? PickModelFile(string? current)
    {
        var dialog = new OpenFileDialog { Filter = "モデル (*.gguf)|*.gguf", Title = "モデルファイルを選ぶ" };
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
