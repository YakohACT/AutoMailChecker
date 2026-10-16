using System.Windows;
using AutoMailChecker.Core;
using AutoMailChecker.Hosting;
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

            await _host.StartAsync();

            // await の後も UI スレッドで続くので、ここでトレイを作る
            var status = _host.Services.GetRequiredService<IAppStatus>();
            _tray = new TrayApp();
            _tray.CommandInvoked += OnTrayCommand;
            _tray.Show(status.Current);
            status.Changed += (_, snapshot) => Dispatcher.InvokeAsync(() => _tray?.Update(snapshot));

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
        _tray?.Dispose();
        _tray = null;

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync();
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
