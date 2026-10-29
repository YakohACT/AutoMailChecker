using System.Windows.Forms;
using AutoMailChecker.Core;
using AutoMailChecker.Windows.Ui;

namespace AutoMailChecker.Windows.Tray;

/// <summary>トレイメニューから出す指示（画面設計 SC-01 のメニュー順）。</summary>
public enum TrayCommand
{
    Pause30Minutes,
    Pause1Hour,
    PauseUntilResumed,
    Stop,
    Resume,
    OpenHistory,
    OpenTrial,
    OpenItemSettings,
    OpenPauseSettings,
    OpenGeneralSettings,
    Exit,
}

/// <summary>
/// トレイアイコンとメニュー（WinForms の NotifyIcon）。UI スレッドで作り、UI スレッドから操作する。
/// メール到着の通知は出さず、状態とエラーはアイコンとツールチップで示す。
/// </summary>
public sealed class TrayApp : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly TrayIconFactory _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _resumeItem;
    private readonly Dictionary<TrayCommand, ToolStripMenuItem> _commandItems = [];
    private readonly List<(ToolStripMenuItem Item, string Japanese, string English)> _texts = [];
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private AppStatusSnapshot _status = AppStatusSnapshot.Initial;
    private bool _disposed;

    public TrayApp(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Application.EnableVisualStyles();

        _statusItem = new ToolStripMenuItem { Enabled = false };

        var pauseItem = TextItem(new ToolStripMenuItem(), "一時停止", "Pause");
        pauseItem.DropDownItems.AddRange([
            CommandItem("30分", "30 minutes", TrayCommand.Pause30Minutes),
            CommandItem("1時間", "1 hour", TrayCommand.Pause1Hour),
            CommandItem("再開するまで", "Until resumed", TrayCommand.PauseUntilResumed),
        ]);

        _resumeItem = CommandItem("再開", "Resume", TrayCommand.Resume);
        _resumeItem.Visible = false;

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange([
            _statusItem,
            new ToolStripSeparator(),
            pauseItem,
            CommandItem("完全停止", "Full stop", TrayCommand.Stop),
            _resumeItem,
            new ToolStripSeparator(),
            CommandItem("判定履歴…", "Judgment history…", TrayCommand.OpenHistory),
            CommandItem("お試し仕分け…", "Trial sort…", TrayCommand.OpenTrial),
            new ToolStripSeparator(),
            CommandItem("項目の設定…", "Item settings…", TrayCommand.OpenItemSettings),
            CommandItem("停止対象の設定…", "Pause rule settings…", TrayCommand.OpenPauseSettings),
            CommandItem("全般設定…", "General settings…", TrayCommand.OpenGeneralSettings),
            new ToolStripSeparator(),
            CommandItem("終了", "Exit", TrayCommand.Exit),
        ]);
        _menu.Opening += (_, _) => Update(_status);

        // まだ実装していない機能のメニューは押せないようにしておく。実装したら SetCommandAvailable で有効にする
        foreach (var command in _commandItems.Keys.Where(c => c != TrayCommand.Exit).ToList())
        {
            SetCommandAvailable(command, false);
        }

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icons.Get(RunMode.Running, hasError: false),
            Text = AppInfo.Name,
        };
        _notifyIcon.MouseClick += OnIconClick;

        // 一時停止の残り時間を更新し続けるため
        _refreshTimer = new System.Windows.Forms.Timer { Interval = (int)RefreshInterval.TotalMilliseconds };
        _refreshTimer.Tick += (_, _) => Update(_status);

        // 言語を切り替えたら、メニューとツールチップを書き直す（トレイはアプリと同じだけ生きるので強い参照でよい）
        TranslateSource.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>メニューから指示が出た。Exit は確認ダイアログで「はい」が押されたときだけ出る。</summary>
    public event EventHandler<TrayCommand>? CommandInvoked;

    public void Show(AppStatusSnapshot status)
    {
        Update(status);
        _notifyIcon.Visible = true;
        _refreshTimer.Start();
    }

    public void Update(AppStatusSnapshot status)
    {
        if (_disposed)
        {
            return;
        }

        _status = status;
        var now = _time.GetUtcNow();
        _notifyIcon.Icon = _icons.Get(status.Mode, status.Errors.Count > 0);
        _notifyIcon.Text = TrayStatusFormatter.Tooltip(status, now);
        _statusItem.Text = TrayStatusFormatter.StatusLine(status, now);
        _resumeItem.Visible = status.Mode is RunMode.Held or RunMode.Stopped;
    }

    /// <summary>エラーのポップアップ（設定でオンにしたときだけ使う）。</summary>
    public void ShowNotification(string title, string message)
    {
        if (!_disposed)
        {
            _notifyIcon.ShowBalloonTip(10_000, title, message.Length <= 250 ? message : message[..249] + "…", ToolTipIcon.Warning);
        }
    }

    public void SetCommandAvailable(TrayCommand command, bool available)
    {
        if (_commandItems.TryGetValue(command, out var item))
        {
            item.Enabled = available;
            item.ToolTipText = available ? null : Translate.Text("準備中", "Not available yet");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TranslateSource.Instance.LanguageChanged -= OnLanguageChanged;
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icons.Dispose();
    }

    private ToolStripMenuItem CommandItem(string japanese, string english, TrayCommand command)
    {
        var item = TextItem(new ToolStripMenuItem(), japanese, english);
        item.Click += (_, _) => Invoke(command);
        _commandItems[command] = item;
        return item;
    }

    /// <summary>言語を切り替えたら書き直すメニュー項目。</summary>
    private ToolStripMenuItem TextItem(ToolStripMenuItem item, string japanese, string english)
    {
        item.Text = Translate.Text(japanese, english);
        _texts.Add((item, japanese, english));
        return item;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var (item, japanese, english) in _texts)
        {
            item.Text = Translate.Text(japanese, english);
            if (!item.Enabled)
            {
                item.ToolTipText = Translate.Text("準備中", "Not available yet");
            }
        }

        Update(_status);
    }

    private void Invoke(TrayCommand command)
    {
        if (command == TrayCommand.Exit && !ConfirmExit())
        {
            return;
        }

        CommandInvoked?.Invoke(this, command);
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        // エラーがあるときは、左クリックで全般設定を開いて再設定を促す
        if (e.Button == MouseButtons.Left && _status.Errors.Count > 0
            && _commandItems[TrayCommand.OpenGeneralSettings].Enabled)
        {
            Invoke(TrayCommand.OpenGeneralSettings);
        }
    }

    private static bool ConfirmExit() =>
        MessageBox.Show(
            Translate.Text(
                "AutoMailChecker を終了しますか？\n処理中のメールがあれば、その1通が終わってから終了します。",
                "Exit AutoMailChecker?\nIf a mail is being processed, the app exits after that mail is finished."),
            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
        == DialogResult.Yes;
}
