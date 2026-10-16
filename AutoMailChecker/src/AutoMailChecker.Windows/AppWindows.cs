using System.Windows;
using AutoMailChecker.Core;
using AutoMailChecker.Windows.History;
using AutoMailChecker.Windows.Settings;
using AutoMailChecker.Windows.Trial;

namespace AutoMailChecker.Windows;

/// <summary>
/// トレイから開くウィンドウ。開いている間だけ作って読み込み、閉じたら手放す（同じ画面は1つだけ）。
/// UI スレッドから呼ぶ。
/// </summary>
public sealed class AppWindows(SettingsServices services, IUndoService undo, ITrialService trials, IAppStatus status)
{
    private HistoryWindow? _history;
    private TrialWindow? _trial;
    private SettingsWindow? _settings;

    /// <summary>判定履歴を開く。お試しの回を指定すると、その回だけを表示する。</summary>
    public void ShowHistory(long? trialRunId = null)
    {
        if (_history is { } open)
        {
            Activate(open);
            if (trialRunId is not null)
            {
                _ = open.ViewModel.ShowTrialAsync(trialRunId);
            }

            return;
        }

        var viewModel = new HistoryViewModel(services.Store, services.Config, undo);
        var window = new HistoryWindow(viewModel);
        window.Closed += (_, _) => _history = null;
        _history = window;
        window.Show();
        _ = viewModel.InitializeAsync(trialRunId);
    }

    public void ShowTrial()
    {
        if (_trial is { } open)
        {
            Activate(open);
            return;
        }

        TrialWindow? window = null;
        var viewModel = new TrialViewModel(trials, services.Store, services.Config, status,
            openHistory: id => ShowHistory(id),
            confirm: message => MessageBox.Show(window!, message, AppInfo.Name, MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        window = new TrialWindow(viewModel);
        window.Closed += (_, _) => _trial = null;
        _trial = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    /// <summary>設定ウィンドウ（項目・停止対象・全般）を、指定したタブで開く。</summary>
    public void ShowSettings(SettingsTab tab)
    {
        if (_settings is { } open)
        {
            Activate(open);
            open.SelectTab(tab);
            return;
        }

        SettingsWindow? window = null;
        var viewModel = new SettingsViewModel(services, new SettingsDialogs(() => window!), tab);
        window = new SettingsWindow(viewModel);
        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    public void CloseAll()
    {
        _history?.Close();
        _trial?.Close();
        _settings?.Close();
    }

    private static void Activate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}
