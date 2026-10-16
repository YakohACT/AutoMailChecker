using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using AutoMailChecker.Windows.History;
using AutoMailChecker.Windows.Trial;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Windows;

/// <summary>
/// 判定履歴とお試し仕分けの画面を、見本のデータで画像（PNG）にする。見た目の確認用。
/// 画面を描くので、環境変数 AMC_RENDER_DIR（画像の保存先）を指定したときだけ動かす。
/// </summary>
public sealed class WindowRenderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void 判定履歴とお試し仕分けの画面を画像にする()
    {
        var outputDirectory = Environment.GetEnvironmentVariable("AMC_RENDER_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(outputDirectory), "環境変数 AMC_RENDER_DIR を指定したときだけ画面を画像にします。");
        Directory.CreateDirectory(outputDirectory!);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RenderAll(outputDirectory!);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("画面を描けませんでした。", failure);
        }
    }

    private void RenderAll(string outputDirectory)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var store = new SqliteStateStore(_temp.Combine("state.db"), TimeProvider.System);
        Wait(store.InitializeAsync(CancellationToken.None));
        var trialRunId = Wait(SeedAsync(store));

        var config = new FixedConfig(DefaultConfig.Instance);
        var status = new AppStatusService(config);
        var accounts = new FakeAccounts();
        var undo = new UndoService(store, accounts, NullLogger<UndoService>.Instance);
        var trials = new TrialRunner(store, accounts, undo, config, new QueueSignal(), status, NullLogger<TrialRunner>.Instance);

        var history = new HistoryViewModel(store, config, undo);
        var historyWindow = Offscreen(new HistoryWindow(history));
        Wait(history.InitializeAsync());
        history.SelectedRow = history.Rows[0];
        Save(historyWindow, Path.Combine(outputDirectory, "history.png"));
        historyWindow.Close();

        var trial = new TrialViewModel(trials, store, config, status, _ => { }, _ => true);
        var trialWindow = Offscreen(new TrialWindow(trial));
        Wait(trial.InitializeAsync());
        Save(trialWindow, Path.Combine(outputDirectory, "trial-setup.png"));
        Wait(trial.ShowReportAsync(trialRunId));
        Save(trialWindow, Path.Combine(outputDirectory, "trial-done.png"));
        trialWindow.Close();

        RenderSettings(outputDirectory, store);
    }

    private static void RenderSettings(string outputDirectory, SqliteStateStore store)
    {
        var config = new ValidatingConfig(DefaultConfig.Instance);
        var credentials = new RenderCredentials();
        credentials.WriteSecret("AutoMailChecker/gmail1", "first@gmail.com", "x");
        var services = new AutoMailChecker.Windows.Settings.SettingsServices(
            config, new RenderCatalog(), new RenderRejudge(), store, new RenderRunModes(), new RenderSystem(), credentials,
            new RenderTester(), new RenderAutoStart(), Path.GetTempPath());
        var settings = new AutoMailChecker.Windows.Settings.SettingsViewModel(
            services, new RenderDialogs(), AutoMailChecker.Windows.Settings.SettingsTab.Items);
        var window = Offscreen(new AutoMailChecker.Windows.Settings.SettingsWindow(settings));
        Wait(settings.InitializeAsync());

        Save(window, Path.Combine(outputDirectory, "settings-items.png"));
        settings.SelectedTabIndex = 1;
        settings.PauseTab.SelectedRule = settings.PauseTab.Rules.Single(r => r.Name == "Minecraft（Java版）");
        Save(window, Path.Combine(outputDirectory, "settings-pause.png"));
        settings.SelectedTabIndex = 2;
        settings.GeneralTab.SelectedAccount = settings.GeneralTab.Accounts[1];
        Save(window, Path.Combine(outputDirectory, "settings-general.png"));

        // 保存できない内容にして、赤枠と理由の出方を見る
        settings.SelectedTabIndex = 0;
        settings.ItemsTab.SelectedItem = settings.ItemsTab.Items[1];
        settings.ItemsTab.Items[1].Label = "";
        settings.ItemsTab.Items[1].Folders.Single(f => f.AccountId == "gmail1").Folder = "AI/重要";
        settings.SaveCommand.Execute(null);
        Save(window, Path.Combine(outputDirectory, "settings-errors.png"));
        window.Close();
    }

    private sealed class RenderCatalog : IMailFolderCatalog
    {
        public Task<IReadOnlyList<string>> GetSortingFoldersAsync(string accountId, bool refresh, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(accountId == "icloud"
                ? ["重要", "予約関連", "決済履歴", "給与明細", "広告", "Archive"]
                : ["AI/重要", "AI/予約関連", "AI/決済履歴", "AI/給与明細", "AI/広告", "仕事"]);
    }

    private sealed class RenderRejudge : IRejudgeService
    {
        public bool IsAvailable => true;
        public Task<RejudgeResult> RejudgeAsync(long judgementId, AppConfig draft, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RenderRunModes : IRunModeController
    {
        public RunModeState Current => new(RunMode.LowLoad, "Visual Studio起動中", null);
        public IReadOnlyList<PauseDetection> CurrentDetections => [new("app:Visual Studio", PauseAction.LowLoad, "Visual Studio起動中")];

        public event EventHandler<RunModeState>? Changed
        {
            add { }
            remove { }
        }

        public void PauseFor(TimeSpan? duration) { }
        public void Stop() { }
        public void Resume() { }
    }

    private sealed class RenderSystem : AutoMailChecker.Windows.Pause.ISystemSnapshotSource
    {
        public IReadOnlyList<AutoMailChecker.Windows.Pause.ProcessInfo> GetProcesses() => [];
        public IReadOnlyList<AutoMailChecker.Windows.Pause.WindowInfo> GetWindows() => [];
        public bool IsFullscreen() => false;
    }

    private sealed class RenderCredentials : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public string? ReadSecret(string key) => _secrets.GetValueOrDefault(key);
        public void WriteSecret(string key, string userName, string secret) => _secrets[key] = secret;
    }

    private sealed class RenderTester : IMailConnectionTester
    {
        public Task<string> TestAsync(AccountConfig account, CancellationToken ct) => Task.FromResult("接続できました。");
    }

    private sealed class RenderAutoStart : AutoMailChecker.Windows.Startup.IAutoStartRegistrar
    {
        public bool IsRegistered() => true;
        public void Register() { }
        public void Unregister() { }
    }

    private sealed class RenderDialogs : AutoMailChecker.Windows.Settings.ISettingsDialogs
    {
        public bool Confirm(string message) => true;
        public string? AskPassword(string accountId, string user) => null;
        public string? PickExecutable() => null;
        public string? PickFolder(string? initialFolder) => null;
        public string? PickModelFile(string? current) => null;
        public AutoMailChecker.Windows.Pause.ProcessInfo? PickRunningApp(IReadOnlyList<AutoMailChecker.Windows.Pause.ProcessInfo> processes) => null;
        public void ShowRejudge(AutoMailChecker.Windows.Settings.RejudgeViewModel viewModel) { }
        public void OpenFolder(string path) { }
    }

    private static async Task<long> SeedAsync(SqliteStateStore store)
    {
        var samples = new (string Account, string From, string Subject, Outcome Outcome, string? Item, Confidence? Confidence, bool Ad, string Summary)[]
        {
            ("gmail1", "〇〇ホテル <info@hotel.example.com>", "【〇〇ホテル】ご予約確定のお知らせ", Outcome.Moved, "reservation", Confidence.High, false, "10月20日宿泊の予約確定。事前の情報登録が必要"),
            ("icloud", "△△カード <no-reply@card.example.jp>", "カードご利用のお知らせ", Outcome.Moved, "payment", Confidence.High, false, "10月15日 3,980円のカード利用"),
            ("gmail2", "□□市役所 <info@city.example.lg.jp>", "【要提出】住民税の申告書のご提出について", Outcome.Planned, "important", Confidence.Medium, false, "10月31日までに申告書の提出が必要"),
            ("gmail1", "ショップ通信 <news@shop.example.com>", "週末限定セール開催中！", Outcome.KeptAd, null, Confidence.High, true, "週末セールの案内"),
            ("gmail3", "友人 <friend@example.com>", "Re: 来週の件", Outcome.KeptNoMatch, null, Confidence.Medium, false, "来週の予定についての返信"),
            ("icloud", "不明な差出人 <x@unknown.example>", "ご確認ください", Outcome.KeptLowConfidence, "important", Confidence.Low, false, "内容が曖昧な確認依頼"),
            ("gmail1", "給与システム <payroll@corp.example.co.jp>", "10月分 給与明細のお知らせ", Outcome.Failed, null, null, false, ""),
        };

        var labels = DefaultConfig.Instance.Items.ToDictionary(i => i.Id.Value);
        uint uid = 100;
        foreach (var s in samples)
        {
            var mail = new MailRef(s.Account, "INBOX", 1, uid++);
            var folder = s.Item is null ? null : s.Account == "icloud" ? labels[s.Item].Label : $"AI/{labels[s.Item].Label}";
            var record = new JudgementRecord
            {
                Mail = mail,
                MessageId = $"<sample{uid}@example.com>",
                ReceivedAt = Now.AddMinutes(-uid * 17),
                From = s.From,
                Subject = s.Subject,
                IsAdvertisement = s.Ad,
                Confidence = s.Confidence,
                Summary = s.Summary,
                Reason = s.Outcome == Outcome.Failed ? "AI の出力が不正なため判定できませんでした: JSON として読めません" : "通知の内容から判断したため",
                Outcome = s.Outcome,
                TargetItemId = s.Outcome is Outcome.Moved or Outcome.Planned ? s.Item : null,
                TargetFolder = s.Outcome is Outcome.Moved or Outcome.Planned ? folder : null,
                JudgedAt = Now,
                Items = s.Item is null ? [] : [new JudgementItemRecord(new ItemId(s.Item), labels[s.Item].Label, labels[s.Item].Priority)],
            };
            var move = s.Outcome == Outcome.Moved
                ? new MoveRecord { Moved = new MovedMailRef(s.Account, "INBOX", folder!, 9, uid), MovedAt = Now }
                : null;
            await store.Judgements.RecordAsync(record, move, null, CancellationToken.None);
        }

        var run = await store.TrialRuns.CreateAsync(new TrialRun
        {
            PeriodFrom = DateOnly.FromDateTime(Now.Date.AddDays(-30)),
            PeriodTo = DateOnly.FromDateTime(Now.Date),
            Accounts = ["icloud", "gmail1"],
            Folders = ["INBOX"],
            Mode = TrialMode.Move,
            MaxMessages = 200,
        }, [], CancellationToken.None);
        var trialOutcomes = new[]
        {
            ("important", Outcome.Moved), ("important", Outcome.Moved), ("reservation", Outcome.Moved),
            ("payment", Outcome.Moved), ("payment", Outcome.Moved), ("payment", Outcome.Moved),
            ((string?)null, Outcome.KeptLowConfidence), (null, Outcome.KeptNoMatch), (null, Outcome.KeptAd),
        };
        foreach (var (item, outcome) in trialOutcomes)
        {
            var mail = new MailRef("gmail1", "INBOX", 1, uid++);
            await store.Judgements.RecordAsync(new JudgementRecord
            {
                Mail = mail,
                Outcome = outcome,
                IsAdvertisement = outcome == Outcome.KeptAd,
                Confidence = outcome == Outcome.KeptLowConfidence ? Confidence.Low : Confidence.High,
                TargetItemId = item,
                TargetFolder = item is null ? null : $"AI/{labels[item].Label}",
                TrialRunId = run.Id,
                JudgedAt = Now,
            }, item is null ? null : new MoveRecord { Moved = new MovedMailRef("gmail1", "INBOX", $"AI/{labels[item].Label}", 9, uid), MovedAt = Now },
            null, CancellationToken.None);
        }

        await store.TrialRuns.FinishAsync(run.Id, TrialRunStatus.Done, CancellationToken.None);
        return run.Id;
    }

    private static T Offscreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        return window;
    }

    private static void Save(Window window, string path)
    {
        for (var i = 0; i < 5; i++)
        {
            DoEvents();
        }

        // ウィンドウの枠を除いた中身（背景と余白を含む）をそのまま描く
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>UI スレッドのメッセージを回しながら、非同期の処理が終わるのを待つ。</summary>
    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            DoEvents();
            Thread.Sleep(5);
        }

        task.GetAwaiter().GetResult();
    }

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }), null);
        Dispatcher.PushFrame(frame);
    }
}
