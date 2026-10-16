using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;

namespace AutoMailChecker.Tests.Core;

public class ActionPolicyTests
{
    private const string Account = "gmail1";

    private static readonly ActionPolicy Policy = new();

    private static readonly IReadOnlyList<ItemDefinition> DefaultItems =
    [
        Item("important", 1, "AI/重要"),
        Item("reservation", 2, "AI/予約関連"),
        Item("payment", 3, "AI/決済履歴"),
    ];

    [Fact]
    public void 確信度が低ければ_該当項目があっても移動しない()
    {
        var decision = Policy.Decide(Judge(["important"], Confidence.Low), Context());

        Assert.Null(decision.Target);
        Assert.Equal(Outcome.KeptLowConfidence, decision.Outcome);
        Assert.False(decision.ShouldMove);
    }

    [Fact]
    public void 確信度は広告判定より先に見る()
    {
        var decision = Policy.Decide(Judge([], Confidence.Low, isAd: true), Context(adMode: AdvertisementMode.Move));

        Assert.Equal(Outcome.KeptLowConfidence, decision.Outcome);
    }

    [Fact]
    public void しきい値が高のみなら_中は移動しない()
    {
        var decision = Policy.Decide(Judge(["important"], Confidence.Medium), Context(minConfidence: Confidence.High));

        Assert.Equal(Outcome.KeptLowConfidence, decision.Outcome);
    }

    [Fact]
    public void 広告は無視の設定なら_項目に当てはまっていても受信トレイに残す()
    {
        var decision = Policy.Decide(Judge(["important"], isAd: true), Context(adMode: AdvertisementMode.Ignore));

        Assert.Null(decision.Target);
        Assert.Equal(Outcome.KeptAd, decision.Outcome);
    }

    [Fact]
    public void 広告は振り分けの設定なら_広告用フォルダーへ移動する()
    {
        var decision = Policy.Decide(Judge(["important"], isAd: true), Context(adMode: AdvertisementMode.Move));

        var target = Assert.IsType<AdvertisementTarget>(decision.Target);
        Assert.Equal("AI/広告", target.Folder);
        Assert.Equal(Outcome.MovedAd, decision.Outcome);
        Assert.True(decision.ShouldMove);
    }

    [Fact]
    public void 広告用フォルダーが未設定なら_移動しない()
    {
        var context = Context(adMode: AdvertisementMode.Move) with
        {
            Advertisement = new AdvertisementConfig { Mode = AdvertisementMode.Move },
        };

        var decision = Policy.Decide(Judge([], isAd: true), context);

        Assert.Equal(Outcome.KeptNoFolder, decision.Outcome);
    }

    [Fact]
    public void 該当項目が1件なら_その項目のフォルダーへ移動する()
    {
        var decision = Policy.Decide(Judge(["reservation"]), Context());

        var target = Assert.IsType<ItemTarget>(decision.Target);
        Assert.Equal(new ItemId("reservation"), target.Item);
        Assert.Equal("AI/予約関連", target.Folder);
        Assert.Equal(Outcome.Moved, decision.Outcome);
    }

    [Fact]
    public void 複数該当なら_優先度が最も高い項目へ移動する()
    {
        var decision = Policy.Decide(Judge(["payment", "important", "reservation"]), Context());

        var target = Assert.IsType<ItemTarget>(decision.Target);
        Assert.Equal(new ItemId("important"), target.Item);
    }

    [Fact]
    public void 最優先の項目に振り分け先がなければ_次点の項目へ移動する()
    {
        IReadOnlyList<ItemDefinition> items = [Item("important", 1, folder: null), .. DefaultItems.Skip(1)];

        var decision = Policy.Decide(Judge(["important", "payment"]), Context(items));

        var target = Assert.IsType<ItemTarget>(decision.Target);
        Assert.Equal(new ItemId("payment"), target.Item);
    }

    [Fact]
    public void 最優先の振り分け先がサーバーになければ_次点の項目へ移動する()
    {
        var context = Context(unavailableFolders: new HashSet<string> { "AI/重要" });

        var decision = Policy.Decide(Judge(["important", "reservation"]), context);

        var target = Assert.IsType<ItemTarget>(decision.Target);
        Assert.Equal(new ItemId("reservation"), target.Item);
    }

    [Fact]
    public void どの該当項目にも振り分け先がなければ_移動しない()
    {
        var context = Context(unavailableFolders: new HashSet<string> { "AI/重要", "AI/予約関連" });

        var decision = Policy.Decide(Judge(["important", "reservation"]), context);

        Assert.Null(decision.Target);
        Assert.Equal(Outcome.KeptNoFolder, decision.Outcome);
    }

    [Fact]
    public void 他アカウントの振り分け先は使わない()
    {
        IReadOnlyList<ItemDefinition> items =
        [
            Item("important", 1, "AI/重要") with { Folders = new Dictionary<string, string> { ["icloud"] = "重要" } },
        ];

        var decision = Policy.Decide(Judge(["important"]), Context(items));

        Assert.Equal(Outcome.KeptNoFolder, decision.Outcome);
    }

    [Fact]
    public void 無効な項目は無視する()
    {
        IReadOnlyList<ItemDefinition> items = [DefaultItems[0] with { Enabled = false }, .. DefaultItems.Skip(1)];

        var withOther = Policy.Decide(Judge(["important", "payment"]), Context(items));
        var onlyDisabled = Policy.Decide(Judge(["important"]), Context(items));

        Assert.Equal(new ItemId("payment"), Assert.IsType<ItemTarget>(withOther.Target).Item);
        Assert.Equal(Outcome.KeptNoMatch, onlyDisabled.Outcome);
    }

    [Fact]
    public void 該当項目がなければ_受信トレイに残す()
    {
        Assert.Equal(Outcome.KeptNoMatch, Policy.Decide(Judge([]), Context()).Outcome);
    }

    [Fact]
    public void 設定にない項目IDは無視する()
    {
        Assert.Equal(Outcome.KeptNoMatch, Policy.Decide(Judge(["unknown"]), Context()).Outcome);
    }

    [Fact]
    public void 移動できないときは_移動予定先だけ決める()
    {
        var decision = Policy.Decide(Judge(["important"]), Context(moveAllowed: false));

        Assert.Equal(Outcome.Planned, decision.Outcome);
        Assert.Equal("AI/重要", decision.Target?.Folder);
        Assert.False(decision.ShouldMove);
    }

    [Fact]
    public void 移動できないときも_残す判定はそのまま()
    {
        var decision = Policy.Decide(Judge(["important"], Confidence.Low), Context(moveAllowed: false));

        Assert.Equal(Outcome.KeptLowConfidence, decision.Outcome);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void allowMoveとお試しの設定から移動の可否を決める(bool allowMove, bool previewOnly, bool expected)
    {
        var config = DefaultConfig.Instance with
        {
            Accounts = [.. DefaultConfig.Instance.Accounts.Select(a => a.Id == Account ? a with { AllowMove = allowMove } : a)],
        };

        var context = PolicyContext.From(config, Account, previewOnly);

        Assert.Equal(expected, context.MoveAllowed);
    }

    private static ItemDefinition Item(string id, int priority, string? folder) => new()
    {
        Id = new ItemId(id),
        Label = id,
        Priority = priority,
        Description = id,
        Folders = folder is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [Account] = folder },
    };

    private static Judgement Judge(string[] items, Confidence confidence = Confidence.High, bool isAd = false) =>
        new(isAd, items.Select(i => new ItemId(i)).ToList(), confidence, "要約", "理由");

    private static PolicyContext Context(
        IReadOnlyList<ItemDefinition>? items = null,
        AdvertisementMode adMode = AdvertisementMode.Ignore,
        Confidence minConfidence = Confidence.Medium,
        bool moveAllowed = true,
        IReadOnlySet<string>? unavailableFolders = null) =>
        new(Account,
            items ?? DefaultItems,
            new AdvertisementConfig { Mode = adMode, Folders = new Dictionary<string, string> { [Account] = "AI/広告" } },
            minConfidence,
            moveAllowed,
            unavailableFolders);
}
