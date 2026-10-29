using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Ui;

/// <summary>
/// DatePicker の空欄に出る文字（「日付の選択」）を画面の言語に合わせる。
/// WPF は Windows の表示言語で出し、外から変える設定もないので、テンプレートの中の表示を書き換える。
/// </summary>
internal static class DatePickerWatermark
{
    private static readonly List<WeakReference<DatePicker>> Pickers = [];

    /// <summary>すべての DatePicker に効かせる（画面のスレッドで1回だけ呼ぶ）。</summary>
    public static void Register(TranslateSource source)
    {
        EventManager.RegisterClassHandler(typeof(DatePicker), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        source.LanguageChanged += (_, _) => ApplyAll();
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var picker = (DatePicker)sender;
        Pickers.RemoveAll(p => !p.TryGetTarget(out _));
        Pickers.Add(new WeakReference<DatePicker>(picker));
        Apply(picker);
    }

    private static void ApplyAll()
    {
        foreach (var reference in Pickers)
        {
            if (reference.TryGetTarget(out var picker))
            {
                Apply(picker);
            }
        }
    }

    private static void Apply(DatePicker picker)
    {
        if (picker.Template?.FindName("PART_TextBox", picker) is DatePickerTextBox textBox
            && FindWatermark(textBox) is { } watermark)
        {
            watermark.Content = Translate.Text("日付の選択", "Select a date");
        }
    }

    private static ContentControl? FindWatermark(DatePickerTextBox textBox)
    {
        textBox.ApplyTemplate();
        return textBox.Template?.FindName("PART_Watermark", textBox) as ContentControl ?? FindChild(textBox);
    }

    private static ContentControl? FindChild(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentControl { Name: "PART_Watermark" } found)
            {
                return found;
            }

            if (FindChild(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
