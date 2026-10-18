using System.Windows;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Settings;

/// <summary>アプリ用パスワードや API キーの入力。入力値は ViewModel を通さず、資格情報マネージャーへ渡すだけにする。</summary>
public partial class PasswordWindow : Window
{
    public PasswordWindow(string title, string heading, string hint)
    {
        InitializeComponent();
        Title = $"{title} - {AppInfo.Name}";
        Heading.Text = heading;
        HintText.Text = hint;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public string Password => PasswordInput.Password;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length > 0)
        {
            DialogResult = true;
        }
    }
}
