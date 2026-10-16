using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Logging;
using AutoMailChecker.Mail;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using AutoMailChecker.Windows.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Hosting;

/// <summary>
/// 依存性注入の組み立て（Generic Host）。具体クラスを知っているのはここだけにする。
/// </summary>
public static class AppHost
{
    public static IHost Build(AppPaths paths)
    {
        paths.EnsureDirectories();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = AppInfo.Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddDebug();

        var services = builder.Services;
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        // コンテナーが作ったものはコンテナーが破棄する（終了時にログファイルを閉じる）
        services.AddSingleton<ILoggerProvider>(sp =>
            new FileLoggerProvider(paths.LogDirectory, sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ConfigService>();
        services.AddSingleton<IConfigProvider>(sp => sp.GetRequiredService<ConfigService>());
        services.AddSingleton<IStateStore>(sp =>
            new SqliteStateStore(paths.StateDatabase, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IHtmlTextExtractor, AngleSharpHtmlTextExtractor>();
        services.AddSingleton<BodyPreprocessor>();
        services.AddSingleton<ActionPolicy>();
        services.AddSingleton<AppStatusService>();
        services.AddSingleton<IAppStatus>(sp => sp.GetRequiredService<AppStatusService>());

        // 起動順: 状態 DB の初期化 → 設定ファイルの監視 → 定期処理
        services.AddHostedService<StateStoreInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigService>());
        services.AddHostedService<HistoryRetentionService>();

        return builder.Build();
    }
}
