using System.Windows;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Hosting;
using AutoMailChecker.Windows;
using AutoMailChecker.Windows.Pause;
using AutoMailChecker.Windows.Settings;
using AutoMailChecker.Windows.Startup;
using AutoMailChecker.Windows.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker;

/// <summary>
/// 起動と終了。Generic Host を起動してからトレイアイコンを出す。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\AutoMailChecker.SingleInstance";

    private Mutex? _singleInstance;
    private IHost? _host;
    private TrayApp? _tray;
    private AppWindows? _windows;
    private IRunModeController? _runModes;
    private IConfigProvider _config = null!;
    private ILogger<App>? _logger;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("AutoMailChecker はすでに起動しています。タスクトレイを確認してください。",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _host = AppHost.Build(AppPaths.ForCurrentUser());
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            RegisterGlobalExceptionHandlers();

            // 各サービスが UI スレッドの上で動き続けないよう、スレッドプールで開始する
            var host = _host;
            await Task.Run(() => host.StartAsync());

            // await の後は UI スレッドに戻るので、ここでトレイとウィンドウの窓口を作る
            var services = _host.Services;
            var status = services.GetRequiredService<IAppStatus>();
            _config = services.GetRequiredService<IConfigProvider>();
            _windows = new AppWindows(
                new SettingsServices(
                    _config,
                    services.GetRequiredService<IMailFolderCatalog>(),
                    services.GetRequiredService<IRejudgeService>(),
                    services.GetRequiredService<IStateStore>(),
                    services.GetRequiredService<IRunModeController>(),
                    services.GetRequiredService<ISystemSnapshotSource>(),
                    services.GetRequiredService<ICredentialStore>(),
                    services.GetRequiredService<IMailConnectionTester>(),
                    services.GetRequiredService<ILlmConnectionTester>(),
                    services.GetRequiredService<IAutoStartRegistrar>(),
                    services.GetRequiredService<AppPaths>().LogDirectory),
                services.GetRequiredService<IUndoService>(),
                services.GetRequiredService<ITrialService>(),
                status);
            _runModes = services.GetRequiredService<IRunModeController>();
            _tray = new TrayApp();
            _tray.CommandInvoked += OnTrayCommand;
            foreach (var command in Enum.GetValues<TrayCommand>())
            {
                _tray.SetCommandAvailable(command, true);
            }

            _tray.Show(status.Current);
            var shownErrors = status.Current.Errors;
            status.Changed += (_, snapshot) => Dispatcher.InvokeAsync(() =>
            {
                _tray?.Update(snapshot);
                // 新しく出たエラーは、設定でオンにしていればポップアップでも知らせる（既定はトレイアイコンだけ）
                var newErrors = snapshot.Errors.Except(shownErrors).ToList();
                shownErrors = snapshot.Errors;
                if (newErrors.Count > 0 && _config.Current.Policy.ErrorPopup)
                {
                    _tray?.ShowNotification(AppInfo.Name, string.Join("\n", newErrors.Select(e => $"{e.Source}: {e.Message}")));
                }
            });

            _logger.LogInformation("起動しました。");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "起動に失敗しました。");
            MessageBox.Show($"起動に失敗しました。\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Windows のサインアウト時などは ExitAsync を通らずにここへ来る。SQLite は WAL なので途中で終わっても壊れない
        _tray?.Dispose();
        _host?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        base.OnExit(e);
    }

    private async void OnTrayCommand(object? sender, TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Pause30Minutes:
                _runModes?.PauseFor(TimeSpan.FromMinutes(30));
                break;
            case TrayCommand.Pause1Hour:
                _runModes?.PauseFor(TimeSpan.FromHours(1));
                break;
            case TrayCommand.PauseUntilResumed:
                _runModes?.PauseFor(null);
                break;
            case TrayCommand.Stop:
                _runModes?.Stop();
                break;
            case TrayCommand.Resume:
                _runModes?.Resume();
                break;
            case TrayCommand.OpenHistory:
                _windows?.ShowHistory();
                break;
            case TrayCommand.OpenTrial:
                _windows?.ShowTrial();
                break;
            case TrayCommand.OpenItemSettings:
                _windows?.ShowSettings(SettingsTab.Items);
                break;
            case TrayCommand.OpenPauseSettings:
                _windows?.ShowSettings(SettingsTab.Pause);
                break;
            case TrayCommand.OpenGeneralSettings:
                _windows?.ShowSettings(SettingsTab.General);
                break;
            case TrayCommand.Exit:
                await ExitAsync();
                break;
            default:
                _logger?.LogWarning("未対応のメニュー操作です: {Command}", command);
                break;
        }
    }

    /// <summary>トレイの「終了」。各サービスを止めてから終える（処理中の1件はサービス側で終わらせる）。</summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger?.LogInformation("終了します。");
        _windows?.CloseAll();
        _tray?.Dispose();
        _tray = null;

        if (_host is { } host)
        {
            try
            {
                await Task.Run(() => host.StopAsync());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "終了処理中にエラーが起きました。");
            }
        }

        Shutdown();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // 常駐アプリなので、UI スレッドの想定外の例外は記録して動き続ける
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "UI スレッドで想定外の例外が起きました。");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "想定外の例外で終了します。");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "待たれていないタスクで例外が起きました。");
            args.SetObserved();
        };
    }
}
