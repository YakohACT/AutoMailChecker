using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Core.State;
using AutoMailChecker.Llm;
using AutoMailChecker.Logging;
using AutoMailChecker.Mail;
using AutoMailChecker.Mail.Imap;
using AutoMailChecker.Persistence;
using AutoMailChecker.Services;
using AutoMailChecker.Windows.Credentials;
using AutoMailChecker.Windows.Pause;
using AutoMailChecker.Windows.Startup;
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
        services.AddSingleton<IStatusReporter>(sp => sp.GetRequiredService<AppStatusService>());
        services.AddSingleton<QueueSignal>();

        // IMAP。アカウントは起動時の設定で決まる（追加・削除は再起動で反映）
        services.AddSingleton<ImapConnector>();
        services.AddSingleton<IMailAccountRegistry>(sp => new ImapAccountRegistry(
            sp.GetRequiredService<IConfigProvider>(),
            sp.GetRequiredService<ImapConnector>(),
            sp.GetRequiredService<BodyPreprocessor>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // AI（llama-server）。モデル・ポート・文脈長は起動時の設定で決まる（変更は再起動で反映）
        services.AddSingleton(sp => new LlamaServerManager(
            sp.GetRequiredService<IConfigProvider>(),
            paths.RunDirectory,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LlamaServerManager>>()));
        services.AddSingleton<ILlmRuntime>(sp => sp.GetRequiredService<LlamaServerManager>());
        services.AddSingleton<ILlamaEndpoint>(sp => sp.GetRequiredService<LlamaServerManager>());
        services.AddSingleton<IMailClassifier>(sp => new LlamaClassifier(
            sp.GetRequiredService<LlamaServerManager>(), sp.GetRequiredService<ILogger<LlamaClassifier>>()));

        // 停止・再開（手動操作、ゲーム・全画面の検知）
        services.AddSingleton<ISystemSnapshotSource, WindowsSystemSnapshot>();
        services.AddSingleton<IGameLibraryLocator, GameLibraryLocator>();
        services.AddSingleton<PauseController>();
        services.AddSingleton<IRunModeController>(sp => sp.GetRequiredService<PauseController>());
        services.AddSingleton<IRunModeSource>(sp => sp.GetRequiredService<PauseController>());
        services.AddSingleton<ProcessingGate>();

        // 振り分け（元に戻す、お試し仕分け）
        services.AddSingleton<IUndoService, UndoService>();
        services.AddSingleton<ITrialService, TrialRunner>();

        // 設定画面（振り分け先の候補、接続テスト、試し判定、ログオン時の自動起動）
        services.AddSingleton<IMailFolderCatalog, MailFolderCatalog>();
        services.AddSingleton<IMailConnectionTester, ImapConnectionTester>();
        services.AddSingleton<IRejudgeService, RejudgeService>();
        services.AddSingleton<IAutoStartRegistrar>(_ => TaskSchedulerAutoStart.ForCurrentUser());

        // 終了時は処理中の1通を最大2分待ち、そのあと llama-server を止める
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(3));

        // 起動順: 状態 DB の初期化 → 設定ファイルの監視 → 定期処理 → 運転モード → AI → メールの監視と処理
        // 終了は逆順（処理 → 監視 → AI の停止）
        services.AddHostedService<StateStoreInitializer>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigService>());
        services.AddHostedService<HistoryRetentionService>();
        services.AddHostedService<RunModeService>();
        services.AddHostedService<LlmService>();
        services.AddHostedService<MailWatchService>();
        services.AddHostedService<MailProcessingService>();

        return builder.Build();
    }
}
