using System.Windows;

namespace AutoMailChecker.Windows.Settings;

public partial class RejudgeWindow : Window
{
    public RejudgeWindow(RejudgeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
}
