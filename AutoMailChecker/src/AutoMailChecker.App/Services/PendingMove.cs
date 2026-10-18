using System.Text.Json;
using AutoMailChecker.Core;
using AutoMailChecker.Core.State;

namespace AutoMailChecker.Services;

/// <summary>
/// 移動する直前に queue_item に残す控え。判定結果（記録する予定の判定履歴）と移動先を持つ。
/// 移動の後、記録の前にアプリが落ちたら、再起動後にこれを使って移動し直すか、移動先を探して履歴を補う。
/// </summary>
/// <param name="ItemId">移す先の項目 ID（以前の版で広告用フォルダーへ移そうとしていたものは null）</param>
internal sealed record PendingMove(JudgementRecord Record, string? ItemId, string Folder)
{
    public static PendingMove Create(JudgementRecord record, MoveTarget target) =>
        new(record, target is ItemTarget item ? item.Item.Value : null, target.Folder);

    public static PendingMove FromJson(string json) =>
        JsonSerializer.Deserialize<PendingMove>(json)
        ?? throw new JsonException("移動の控えを読めません。");

    public string ToJson() => JsonSerializer.Serialize(this);
}
