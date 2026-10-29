using System.Text.Json.Serialization;

namespace AutoMailChecker.Core;

/// <summary>画面に出す言語。</summary>
public enum UiLanguage
{
    [JsonStringEnumMemberName("ja")]
    Japanese,

    [JsonStringEnumMemberName("en")]
    English,
}

/// <summary>
/// 画面に出す文言の日英切り替え。文言は使う場所に日本語と英語を並べて書き、<see cref="Text"/> で今の言語のほうを取り出す。
/// XAML からは AutoMailChecker.Windows.Ui.TranslateExtension（{l:Translate '日本語', 'English'}）で使う。
/// 切り替えたら <see cref="LanguageChanged"/> で知らせ、画面は文言を読み直すだけにする（入力中の内容や選択はそのまま）。
/// ログと AI への指示文は切り替えない（日本語のまま）。
/// </summary>
public sealed class Translate
{
    private static int _language = (int)UiLanguage.Japanese;

    private Translate()
    {
    }

    public static UiLanguage Language => (UiLanguage)Volatile.Read(ref _language);

    /// <summary>言語が変わった（どのスレッドから呼ばれるかは決まっていない）。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>今の言語の文言。</summary>
    public static string Text(string japanese, string english) =>
        Language == UiLanguage.English ? english : japanese;

    public static void SetLanguage(UiLanguage language)
    {
        if (Interlocked.Exchange(ref _language, (int)language) != (int)language)
        {
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
