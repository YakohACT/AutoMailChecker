using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace AutoMailChecker.Windows.Settings;

public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _detectionTimer;
    private Point _dragStart;
    private ItemEditViewModel? _dragItem;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        // 開いている間だけ、停止対象タブの「現在の検知」を更新する
        _detectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _detectionTimer.Tick += (_, _) => viewModel.PauseTab.RefreshDetection();
        _detectionTimer.Start();
        Closed += (_, _) => _detectionTimer.Stop();
    }

    public SettingsViewModel ViewModel { get; }

    public void SelectTab(SettingsTab tab) => ViewModel.SelectedTabIndex = (int)tab;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // 項目の一覧はドラッグで並べ替える（上にあるほど優先）
    private void OnItemsMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as ItemEditViewModel;
    }

    private void OnItemsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null)
        {
            return;
        }

        var moved = e.GetPosition(null) - _dragStart;
        if (Math.Abs(moved.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop(ItemsList, item, DragDropEffects.Move);
        }
    }

    private void OnItemsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ItemEditViewModel)) is ItemEditViewModel dragged
            && (e.OriginalSource as FrameworkElement)?.DataContext is ItemEditViewModel target)
        {
            ViewModel.ItemsTab.Move(dragged, ViewModel.ItemsTab.Items.IndexOf(target));
        }
    }
}
