using System.Security.AccessControl;
using System.Security.Principal;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Llm;

namespace AutoMailChecker.Tests.Llm;

public sealed class LlamaArgumentsTests : IDisposable
{
    private static readonly LlmConfig Llm = DefaultConfig.Instance.Llm with
    {
        ServerExe = @"C:\llama\llama-server.exe",
        Model = @"D:\models\Qwen3.6-35B-A3B.gguf",
    };

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void 通常はGPU層を自動にし_設定のスレッド数で起動する()
    {
        var args = LlamaArguments.Build(LlmProfile.Normal, Llm, Llm, @"C:\run\llama.key");

        Assert.Equal(
        [
            "-m", @"D:\models\Qwen3.6-35B-A3B.gguf", "-c", "8192", "-np", "1", "-t", "4",
            "--no-mmproj", "--reasoning", "off", "--prio", "-1",
            "--host", "127.0.0.1", "--port", "8089", "--api-key-file", @"C:\run\llama.key", "--no-webui",
        ], args);
    }

    [Fact]
    public void MoEモデルのときだけ専門家の重みをCPUに置く()
    {
        var args = LlamaArguments.Build(LlmProfile.Normal, Llm, Llm with { NCpuMoe = 30 }, "key");

        Assert.Equal("30", args[args.ToList().IndexOf("--n-cpu-moe") + 1]);
    }

    [Fact]
    public void 低負荷ではGPUを使わず_スレッドを絞る()
    {
        var args = LlamaArguments.Build(LlmProfile.LowLoad, Llm, Llm with { NCpuMoe = 30 }, "key").ToList();

        Assert.Equal("0", args[args.IndexOf("-ngl") + 1]);
        Assert.Equal("2", args[args.IndexOf("-t") + 1]);
        Assert.DoesNotContain("--n-cpu-moe", args);
    }

    [Fact]
    public void モデル_ポート_文脈長は起動時の設定を使い_スレッド数はいまの設定を使う()
    {
        var current = Llm with { Model = "changed.gguf", Port = 9999, CtxSize = 4096, Threads = 8 };

        var args = LlamaArguments.Build(LlmProfile.Normal, Llm, current, "key").ToList();

        Assert.Equal(Llm.Model, args[args.IndexOf("-m") + 1]);
        Assert.Equal("8089", args[args.IndexOf("--port") + 1]);
        Assert.Equal("8192", args[args.IndexOf("-c") + 1]);
        Assert.Equal("8", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void 安全に関わる引数は追加引数に混ざっていても使わず_最後に必ず付ける()
    {
        var current = Llm with { ExtraArgs = ["--host=0.0.0.0", "--tools", "--no-mmproj", "--webui"] };

        var args = LlamaArguments.Build(LlmProfile.Normal, Llm, current, "key");

        Assert.DoesNotContain("--host=0.0.0.0", args);
        Assert.DoesNotContain("--tools", args);
        Assert.DoesNotContain("--webui", args);
        Assert.Contains("--no-mmproj", args);
        Assert.Equal(["--host", "127.0.0.1", "--port", "8089", "--api-key-file", "key", "--no-webui"], args.TakeLast(7));
    }

    [Theory]
    [InlineData("--host", true)]
    [InlineData("--host=0.0.0.0", true)]
    [InlineData("--api-key", true)]
    [InlineData("--tools", true)]
    [InlineData("--tools-runtime", true)]
    [InlineData("--webui", true)]
    [InlineData("--ui-mcp-proxy", true)]
    [InlineData("-m", true)]
    [InlineData("--no-mmproj", false)]
    [InlineData("--no-webui", false)]
    [InlineData("--reasoning", false)]
    [InlineData("-1", false)]
    public void 設定の検証でも安全に関わる引数を弾く(string arg, bool forbidden)
    {
        var config = DefaultConfig.Instance with { Llm = DefaultConfig.Instance.Llm with { ExtraArgs = [arg] } };

        var issues = ConfigValidator.Validate(config);

        Assert.Equal(forbidden, issues.Any(i => i.Path == "llm.extraArgs[0]"));
    }

    [Fact]
    public void APIキーのファイルは本人だけが読める権限で作る()
    {
        var path = _temp.Combine("run", "llama.key");

        LlamaServerManager.WriteKeyFile(path, "abc123");

        Assert.Equal("abc123\n", File.ReadAllText(path));
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }
}
