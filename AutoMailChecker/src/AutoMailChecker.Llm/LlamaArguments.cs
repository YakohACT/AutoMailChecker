using System.Globalization;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Llm;

/// <summary>llama-server の起動引数（外部仕様タブ「llama-serverとのやり取り」）。</summary>
internal static class LlamaArguments
{
    public const string ListenHost = "127.0.0.1";

    /// <param name="startup">起動時の設定。モデル・ポート・文脈長は再起動で反映する</param>
    /// <param name="current">いまの設定。スレッド数などは次に llama-server を起動したときに反映する</param>
    public static IReadOnlyList<string> Build(LlmProfile profile, LlmConfig startup, LlmConfig current, string apiKeyFile)
    {
        var args = new List<string>
        {
            "-m", startup.Model,
            "-c", Number(startup.CtxSize),
            "-np", "1",
        };

        if (profile == LlmProfile.Normal)
        {
            // -ngl は指定しない（自動）。MoE モデルのときだけ専門家の重みを CPU 側に置く
            if (current.NCpuMoe is { } nCpuMoe)
            {
                args.AddRange(["--n-cpu-moe", Number(nCpuMoe)]);
            }

            args.AddRange(["-t", Number(current.Threads)]);
        }
        else
        {
            // 低負荷で継続: GPU を使わず、スレッドも絞る
            args.AddRange(["-ngl", Number(current.LowLoad.GpuLayers), "-t", Number(current.LowLoad.Threads)]);
        }

        // 追加引数（安全に関わるものは設定の検証で弾いてある）
        args.AddRange(current.ExtraArgs.Where(a => !ConfigValidator.IsForbiddenLlmArg(a)));

        // 待ち受け先・API キー・Web UI 無効は最後に付け、ほかの引数で上書きされないようにする
        args.AddRange(
        [
            "--host", ListenHost,
            "--port", Number(startup.Port),
            "--api-key-file", apiKeyFile,
            "--no-webui",
        ]);
        return args;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
