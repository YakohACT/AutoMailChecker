using System.Windows;
using System.Windows.Controls;

namespace AutoMailChecker.Windows.History;

public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public HistoryViewModel ViewModel { get; }

    /// <summary>一覧の末尾近くまでスクロールしたら、続きの 200 件を読む。</summary>
    private async void OnRowsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 5)
        {
            await ViewModel.LoadMoreAsync();
        }
    }
}
