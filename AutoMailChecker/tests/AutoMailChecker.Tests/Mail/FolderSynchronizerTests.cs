using AutoMailChecker.Core;
using AutoMailChecker.Core.State;
using AutoMailChecker.Mail.Watching;
using AutoMailChecker.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoMailChecker.Tests.Mail;

public sealed class FolderSynchronizerTests : IDisposable
{
    private const string Account = "icloud";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(Now);
    private readonly SqliteStateStore _store;
    private readonly FolderSynchronizer _synchronizer;

    public FolderSynchronizerTests()
    {
        _store = new SqliteStateStore(_temp.Combine("state.db"), _time);
        _synchronizer = new FolderSynchronizer(_store, _time, NullLogger.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task 初回は最大UIDを起点として記録し_過去のメールは積まない()
    {
        await _store.InitializeAsync(Ct);
        var folder = new FakeFolder(uidValidity: 100, uids: [118, 119, 120]);

        var added = await _synchronizer.SyncAsync(Account, folder, Ct);

        Assert.Equal(0, added);
        var state = await _store.Folders.GetAsync(Account, "INBOX", Ct);
        Assert.Equal(new FolderState(Account, "INBOX", 100, 120, Now, 120, Now), state);
        Assert.Equal(0, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task 空のフォルダーなら起点は0で_次に届いたメールから積む()
    {
        await _store.InitializeAsync(Ct);
        var folder = new FakeFolder(uidValidity: 100, uids: []);
        await _synchronizer.SyncAsync(Account, folder, Ct);

        folder.Uids.Add(1);
        var added = await _synchronizer.SyncAsync(Account, folder, Ct);

        Assert.Equal(1, added);
    }

    [Fact]
    public async Task 起点より後の新着だけを積み_最後のUIDを進める()
    {
        await _store.InitializeAsync(Ct);
        var folder = new FakeFolder(uidValidity: 100, uids: [119, 120]);
        await _synchronizer.SyncAsync(Account, folder, Ct);

        folder.Uids.AddRange([121, 123]);
        _time.Advance(TimeSpan.FromMinutes(3));
        var added = await _synchronizer.SyncAsync(Account, folder, Ct);

        Assert.Equal(2, added);
        Assert.Equal(123u, (await _store.Folders.GetAsync(Account, "INBOX", Ct))?.LastProcessedUid);
        Assert.Equal([121u, 123u], await TakeAllUidsAsync());
    }

    [Fact]
    public async Task 検索が前回以下のUIDを返しても積まない()
    {
        await _store.InitializeAsync(Ct);
        var folder = new FakeFolder(uidValidity: 100, uids: [120]);
        await _synchronizer.SyncAsync(Account, folder, Ct);

        // 「UID SEARCH UID 121:*」に、サーバーは最後の1通（120）を返すことがある
        folder.ReturnLastOnEmptySearch = true;
        var added = await _synchronizer.SyncAsync(Account, folder, Ct);

        Assert.Equal(0, added);
        Assert.Equal(120u, (await _store.Folders.GetAsync(Account, "INBOX", Ct))?.LastProcessedUid);
    }

    [Fact]
    public async Task 同じ新着を二度積まない()
    {
        await _store.InitializeAsync(Ct);
        var folder = new FakeFolder(uidValidity: 100, uids: [10]);
        await _synchronizer.SyncAsync(Account, folder, Ct);
        folder.Uids.Add(11);

        await _synchronizer.SyncAsync(Account, folder, Ct);
        var second = await _synchronizer.SyncAsync(Account, folder, Ct);

        Assert.Equal(0, second);
        Assert.Equal(1, await _store.Queue.CountPendingAsync(Ct));
    }

    [Fact]
    public async Task UIDVALIDITYが変わったら_起点より後で直近3日分を積み直す()
    {
        await _store.InitializeAsync(Ct);
        _time.Now = Now.AddDays(-10);
        await _synchronizer.SyncAsync(Account, new FakeFolder(uidValidity: 100, uids: [50]), Ct);
        _time.Now = Now;

        var renumbered = new FakeFolder(uidValidity: 200, uids: [1, 2, 3, 4])
        {
            Received =
            {
                [1] = Now.AddDays(-5),   // 3日より前
                [2] = Now.AddDays(-2),
                [3] = Now.AddHours(-1),
                [4] = Now.AddMinutes(-1),
            },
        };
        var added = await _synchronizer.SyncAsync(Account, renumbered, Ct);

        Assert.Equal(3, added);
        Assert.Equal([2u, 3u, 4u], await TakeAllUidsAsync());
        var state = await _store.Folders.GetAsync(Account, "INBOX", Ct);
        Assert.Equal(200u, state?.UidValidity);
        Assert.Equal(4u, state?.LastProcessedUid);
        Assert.Equal(50u, state?.BaselineUid);
    }

    [Fact]
    public async Task UIDVALIDITYが変わっても_起点より前に届いたメールは積まない()
    {
        await _store.InitializeAsync(Ct);
        _time.Now = Now.AddHours(-6);
        await _synchronizer.SyncAsync(Account, new FakeFolder(uidValidity: 100, uids: [50]), Ct);
        _time.Now = Now;

        var renumbered = new FakeFolder(uidValidity: 200, uids: [1, 2])
        {
            Received = { [1] = Now.AddDays(-1), [2] = Now.AddHours(-1) },
        };
        var added = await _synchronizer.SyncAsync(Account, renumbered, Ct);

        Assert.Equal(1, added);
        Assert.Equal([2u], await TakeAllUidsAsync());
    }

    private async Task<List<uint>> TakeAllUidsAsync()
    {
        var uids = new List<uint>();
        while (await _store.Queue.TakeNextAsync(Ct) is { } item)
        {
            uids.Add(item.Mail.Uid);
        }

        return uids;
    }

    private sealed class FakeFolder(uint uidValidity, List<uint> uids) : IWatchedFolder
    {
        public List<uint> Uids { get; } = uids;
        public Dictionary<uint, DateTimeOffset> Received { get; } = [];
        public bool ReturnLastOnEmptySearch { get; set; }

        public string Name => "INBOX";
        public uint UidValidity => uidValidity;

        public Task<uint> GetMaxUidAsync(CancellationToken ct) => Task.FromResult(Uids.Count == 0 ? 0 : Uids.Max());

        public Task<IReadOnlyList<uint>> SearchUidsAboveAsync(uint uid, CancellationToken ct)
        {
            IReadOnlyList<uint> found = Uids.Where(u => u > uid).ToList();
            if (found.Count == 0 && ReturnLastOnEmptySearch && Uids.Count > 0)
            {
                found = [Uids.Max()];
            }

            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<ReceivedUid>> SearchReceivedSinceAsync(DateOnly since, CancellationToken ct)
        {
            IReadOnlyList<ReceivedUid> found = Uids
                .Select(u => new ReceivedUid(u, Received.GetValueOrDefault(u, DateTimeOffset.MinValue)))
                .Where(r => DateOnly.FromDateTime(r.Received.UtcDateTime) >= since)
                .ToList();
            return Task.FromResult(found);
        }
    }
}
