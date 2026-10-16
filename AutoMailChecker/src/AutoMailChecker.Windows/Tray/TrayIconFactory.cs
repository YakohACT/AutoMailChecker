using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Tray;

/// <summary>
/// 運転モードとエラーの有無に応じたトレイアイコンを描く（画像ファイルは持たない）。
/// 稼働中: 緑の封筒 / 低負荷: ＋青い点 / 判定保留: 灰色 / 完全停止: 灰色＋停止マーク / エラー: ＋赤い点
/// </summary>
internal sealed class TrayIconFactory : IDisposable
{
    private static readonly Color RunningBody = Color.FromArgb(46, 125, 50);
    private static readonly Color PausedBody = Color.FromArgb(138, 138, 138);
    private static readonly Color LowLoadDot = Color.FromArgb(30, 136, 229);
    private static readonly Color ErrorDot = Color.FromArgb(229, 57, 53);
    private static readonly Color StopMark = Color.FromArgb(66, 66, 66);

    private readonly Dictionary<(RunMode Mode, bool HasError), Icon> _cache = [];
    private readonly int _size = Math.Max(16, SystemInformation.SmallIconSize.Width);

    public Icon Get(RunMode mode, bool hasError)
    {
        if (!_cache.TryGetValue((mode, hasError), out var icon))
        {
            icon = Create(mode, hasError);
            _cache[(mode, hasError)] = icon;
        }

        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
    }

    private Icon Create(RunMode mode, bool hasError)
    {
        using var bitmap = new Bitmap(_size, _size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var unit = _size / 16f; // 16px 基準の座標で描く

            var paused = mode is RunMode.Held or RunMode.Stopped;
            DrawEnvelope(g, unit, paused ? PausedBody : RunningBody);

            if (mode == RunMode.LowLoad)
            {
                DrawDot(g, unit, 9f, 9f, LowLoadDot);
            }
            else if (mode == RunMode.Stopped)
            {
                DrawStopMark(g, unit);
            }

            if (hasError)
            {
                DrawDot(g, unit, 9f, 0f, ErrorDot);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawEnvelope(Graphics g, float u, Color body)
    {
        var rect = new RectangleF(1f * u, 3f * u, 14f * u, 10f * u);
        using (var fill = new SolidBrush(body))
        {
            g.FillRectangle(fill, rect);
        }

        using var flap = new Pen(Color.White, Math.Max(1f, 1.3f * u)) { LineJoin = LineJoin.Round };
        g.DrawLines(flap, [
            new PointF(rect.Left + 0.8f * u, rect.Top + 0.8f * u),
            new PointF(8f * u, 8.6f * u),
            new PointF(rect.Right - 0.8f * u, rect.Top + 0.8f * u),
        ]);
    }

    private static void DrawDot(Graphics g, float u, float x, float y, Color color)
    {
        var rect = new RectangleF(x * u, y * u, 7f * u, 7f * u);
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.White, Math.Max(1f, 1f * u));
        g.FillEllipse(fill, rect);
        g.DrawEllipse(outline, rect);
    }

    private static void DrawStopMark(Graphics g, float u)
    {
        var circle = new RectangleF(8f * u, 8f * u, 8f * u, 8f * u);
        using var fill = new SolidBrush(StopMark);
        using var outline = new Pen(Color.White, Math.Max(1f, 1f * u));
        g.FillEllipse(fill, circle);
        g.DrawEllipse(outline, circle);
        using var square = new SolidBrush(Color.White);
        g.FillRectangle(square, 10.5f * u, 10.5f * u, 3f * u, 3f * u);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
