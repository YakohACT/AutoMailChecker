using System.Windows;

namespace AutoMailChecker.Windows.Trial;

public partial class TrialWindow : Window
{
    public TrialWindow(TrialViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    public TrialViewModel ViewModel { get; }
}
