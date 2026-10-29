using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Security;
using System.Text;
using AutoMailChecker.Core;

namespace AutoMailChecker.Windows.Startup;

/// <summary>Windows のログオン時に自動で起動する設定。</summary>
public interface IAutoStartRegistrar
{
    bool IsRegistered();

    void Register();

    void Unregister();
}

/// <summary>
/// タスクスケジューラに「ログオン時に起動する」タスクを登録する（仕様書13章、外部仕様タブ）。
/// 管理者権限は使わない: 自分のログオンだけをきっかけにし、自分の権限（最小限）で動かすタスクとして登録する。
/// 常駐アプリなので実行時間の上限はなくし、二重に起動しないようにする。
/// </summary>
public sealed class TaskSchedulerAutoStart(string executablePath, string userId) : IAutoStartRegistrar
{
    public const string TaskName = "AutoMailChecker";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    static TaskSchedulerAutoStart()
    {
        // schtasks の出力は OEM コードページ（日本語環境では CP932）なので、その文字コードを使えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static TaskSchedulerAutoStart ForCurrentUser() =>
        new(Environment.ProcessPath ?? throw new InvalidOperationException(Translate.Text(
            "実行ファイルの場所が分かりません。",
            "Cannot find where the program is located.")),
            WindowsIdentity.GetCurrent().Name);

    public bool IsRegistered() => Run("/Query", "/TN", TaskName).ExitCode == 0;

    public void Register()
    {
        // schtasks /XML は UTF-16 のファイルを読む
        var xmlPath = Path.Combine(Path.GetTempPath(), $"{TaskName}-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xmlPath, BuildTaskXml(executablePath, userId), Encoding.Unicode);
        try
        {
            var (exitCode, output) = Run("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (exitCode != 0)
            {
                throw new InvalidOperationException(Translate.Text(
                    $"タスクスケジューラに登録できませんでした: {output.Trim()}",
                    $"Could not register in Task Scheduler: {output.Trim()}"));
            }
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public void Unregister()
    {
        var (exitCode, output) = Run("/Delete", "/TN", TaskName, "/F");
        if (exitCode != 0 && IsRegistered())
        {
            throw new InvalidOperationException(Translate.Text(
                $"タスクスケジューラから削除できませんでした: {output.Trim()}",
                $"Could not remove from Task Scheduler: {output.Trim()}"));
        }
    }

    /// <summary>タスクの定義（Task Scheduler スキーマ 1.2）。</summary>
    internal static string BuildTaskXml(string executablePath, string userId)
    {
        var command = SecurityElement.Escape(executablePath);
        var workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(executablePath) ?? "");
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>AutoMailChecker を Windows のログオン時に起動します。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int ExitCode, string Output) Run(params string[] arguments)
    {
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(Translate.Text(
            "schtasks.exe を起動できませんでした。",
            "Could not start schtasks.exe."));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            process.Kill();
            throw new TimeoutException(Translate.Text("schtasks.exe が応答しませんでした。", "schtasks.exe did not respond."));
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
