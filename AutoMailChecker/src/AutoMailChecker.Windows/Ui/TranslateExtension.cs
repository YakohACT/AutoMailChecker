using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Ui;

/// <summary>
/// XAML から使う <see cref="Translate"/>。<c>Text="{l:Translate '保存', 'Save'}"</c> のように日本語と英語を並べて書く。
/// 言語の切り替えはバインディングで表示だけを差し替えるので、入力中の内容や選択はそのまま残る。
/// 値に「,」を含むときは '…' で囲む（このアプリでは常に囲む）。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension()
    {
    }

    public TranslateExtension(string japanese, string english)
    {
        Japanese = japanese;
        English = english;
    }

    [ConstructorArgument("japanese")]
    public string Japanese { get; set; } = "";

    [ConstructorArgument("english")]
    public string English { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding(nameof(TranslateSource.Language))
        {
            Source = TranslateSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new PickText(Japanese, English),
        };
        return binding.ProvideValue(serviceProvider);
    }

    /// <summary>言語に合うほうの文言を返す。</summary>
    private sealed class PickText(string japanese, string english) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is UiLanguage.English ? english : japanese;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// 言語の切り替えを画面のスレッドで知らせる窓口。XAML の文言はこれの Language にバインドし、
/// ViewModel（<see cref="ObservableObject"/>）とトレイは <see cref="LanguageChanged"/> で文言を読み直す。
/// </summary>
public sealed class TranslateSource : INotifyPropertyChanged
{
    private TranslateSource()
    {
        Translate.LanguageChanged += (_, _) => RaiseOnUiThread();
    }

    public static TranslateSource Instance { get; } = Create();

    private static TranslateSource Create()
    {
        var source = new TranslateSource();
        // XAML の文言が初めて読まれるときに、日付欄の空欄の文字も言語に合わせるようにする
        Application.Current?.Dispatcher.Invoke(() => DatePickerWatermark.Register(source));
        return source;
    }

    public UiLanguage Language => Translate.Language;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>言語が変わった（画面のスレッドで呼ばれる）。</summary>
    public event EventHandler? LanguageChanged;

    private void RaiseOnUiThread()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Raise();
        }
        else
        {
            dispatcher.BeginInvoke(Raise);
        }
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
