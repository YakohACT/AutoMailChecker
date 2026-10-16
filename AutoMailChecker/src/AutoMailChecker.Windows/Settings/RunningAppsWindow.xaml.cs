using System.Windows;
using AutoMailChecker.Windows.Pause;

namespace AutoMailChecker.Windows.Settings;

public partial class RunningAppsWindow : Window
{
    public RunningAppsWindow(IReadOnlyList<ProcessInfo> processes)
    {
        InitializeComponent();
        ProcessList.ItemsSource = processes;
    }

    public ProcessInfo? Selected => ProcessList.SelectedItem as ProcessInfo;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not null)
        {
            DialogResult = true;
        }
    }
}
