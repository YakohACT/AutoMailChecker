using System.Windows;

namespace AutoMailChecker.Windows.Settings;

/// <summary>アプリ用パスワードの入力。入力値は ViewModel を通さず、資格情報マネージャーへ渡すだけにする。</summary>
public partial class PasswordWindow : Window
{
    public PasswordWindow(string accountId, string user)
    {
        InitializeComponent();
        Heading.Text = $"{accountId}（{(string.IsNullOrWhiteSpace(user) ? "ユーザー名未入力" : user)}）のパスワード";
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
