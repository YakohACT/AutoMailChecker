using AutoMailChecker.Core;
using AutoMailChecker.Mail.Watching;

namespace AutoMailChecker.Tests.Mail;

public class WatchingSupportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(50, 300)]
    public void 繋ぎ直す間隔は5秒から倍々で最大5分(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AccountWatcher.Backoff(failures));
    }

    [Fact]
    public async Task 何度知らせても待っている側は1回だけ起きる()
    {
        var signal = new QueueSignal();
        signal.Notify();
        signal.Notify();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), Ct));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), Ct));
    }
}
