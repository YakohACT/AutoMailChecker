using System.Diagnostics;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Llm;

/// <summary>
/// 本物の llama-server とモデルで、起動 → 判定 → 終了を確かめる結合テスト（テスト設計の「本物と偽サーバーの両方」の本物側）。
/// モデルの読み込みに時間とメモリを使うので、環境変数を指定したときだけ動かす。
///   AMC_LLAMA_SERVER  llama-server.exe の場所
///   AMC_LLAMA_MODEL   モデル（.gguf）の場所
///   AMC_LLAMA_NCPUMOE MoE モデルで専門家の重みを CPU に置く層の数（省略可）
/// </summary>
public sealed class RealLlamaServerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 本物のllama_serverを起動して1通を判定し_終了できる()
    {
        var serverExe = Environment.GetEnvironmentVariable("AMC_LLAMA_SERVER");
        var model = Environment.GetEnvironmentVariable("AMC_LLAMA_MODEL");
        Assert.SkipWhen(string.IsNullOrEmpty(serverExe) || string.IsNullOrEmpty(model),
            "環境変数 AMC_LLAMA_SERVER と AMC_LLAMA_MODEL を指定したときだけ動かします。");
        int? nCpuMoe = int.TryParse(Environment.GetEnvironmentVariable("AMC_LLAMA_NCPUMOE"), out var n) ? n : null;

        var ct = TestContext.Current.CancellationToken;
        var config = DefaultConfig.Instance with
        {
            Llm = DefaultConfig.Instance.Llm with { ServerExe = serverExe!, Model = model!, Port = 18089, NCpuMoe = nCpuMoe },
        };
        var runDirectory = _temp.Combine("run");
        using var server = new LlamaServerManager(new FixedConfig(config), runDirectory, TimeProvider.System,
            NullLogger<LlamaServerManager>.Instance);
        using var classifier = new LlamaClassifier(server, NullLogger<LlamaClassifier>.Instance);

        var stopwatch = Stopwatch.StartNew();
        await server.StartAsync(LlmProfile.Normal, ct);
        var loadSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.Equal(LlmProfile.Normal, server.Running);
        Assert.True(File.Exists(Path.Combine(runDirectory, "llama.key")));

        stopwatch.Restart();
        var judgement = await classifier.ClassifyAsync(ReservationMail(), config.Items, ct);
        var judgeSeconds = stopwatch.Elapsed.TotalSeconds;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"読み込み {loadSeconds:0} 秒 / 判定 {judgeSeconds:0.0} 秒 / 広告 {judgement.IsAdvertisement} / " +
            $"該当 {string.Join(",", judgement.MatchedItems)} / 確信度 {judgement.Confidence} / 要約 {judgement.Summary} / 理由 {judgement.Reason}");

        Assert.False(judgement.IsAdvertisement);
        Assert.Contains(new ItemId("reservation"), judgement.MatchedItems);

        await server.StopAsync(ct);
        Assert.Null(server.Running);
        Assert.False(File.Exists(Path.Combine(runDirectory, "llama.key")));
    }

    private static MailSnapshot ReservationMail() => new(
        new MailHeader(
            new MailRef("gmail1", "INBOX", 1, 10),
            "<r1@hotel.example.com>",
            "〇〇ホテル <info@hotel.example.com>",
            "【〇〇ホテル】ご予約確定のお知らせ",
            new DateTimeOffset(2026, 10, 15, 10, 4, 0, TimeSpan.Zero)),
        """
        山田 太郎 様

        このたびは〇〇ホテルをご予約いただき、誠にありがとうございます。
        以下の内容でご予約が確定しました。

        ご宿泊日: 2026年10月20日（火）〜10月21日（水） 1泊
        お部屋: スタンダードツイン 1室 2名
        お支払い: 現地にてお支払い

        ご来館前日までに、[リンク: hotel.example.com] よりオンラインチェックインの情報登録をお願いいたします。
        """,
        []);
}
