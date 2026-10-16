using System.Windows;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Windows.History;
using AutoMailChecker.Windows.Trial;

namespace AutoMailChecker.Windows;

/// <summary>
/// トレイから開くウィンドウ。開いている間だけ作って読み込み、閉じたら手放す（同じ画面は1つだけ）。
/// UI スレッドから呼ぶ。
/// </summary>
public sealed class AppWindows(
    IStateStore store, IConfigProvider config, IUndoService undo, ITrialService trials, IAppStatus status)
{
    private HistoryWindow? _history;
    private TrialWindow? _trial;

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

        var viewModel = new HistoryViewModel(store, config, undo);
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
        var viewModel = new TrialViewModel(trials, store, config, status,
            openHistory: id => ShowHistory(id),
            confirm: message => MessageBox.Show(window!, message, AppInfo.Name, MessageBoxButton.OKCancel,
                MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
        window = new TrialWindow(viewModel);
        window.Closed += (_, _) => _trial = null;
        _trial = window;
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    public void CloseAll()
    {
        _history?.Close();
        _trial?.Close();
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
