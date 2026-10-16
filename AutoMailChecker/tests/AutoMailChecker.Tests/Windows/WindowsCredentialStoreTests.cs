using AutoMailChecker.Windows.Credentials;

namespace AutoMailChecker.Tests.Windows;

/// <summary>
/// 実際の資格情報マネージャーを使う。テスト専用の名前で書き込み、終わったら消す。
/// </summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _key = $"AutoMailChecker.Tests/{Guid.NewGuid():N}";
    private readonly WindowsCredentialStore _store = new();

    public void Dispose() => WindowsCredentialStore.Delete(_key);

    [Fact]
    public void 登録されていなければnullを返す()
    {
        Assert.Null(_store.ReadSecret(_key));
    }

    [Fact]
    public void 保存したパスワードを読み出せ_上書きもできる()
    {
        _store.WriteSecret(_key, "first@gmail.com", "abcd efgh ijkl mnop");
        Assert.Equal("abcd efgh ijkl mnop", _store.ReadSecret(_key));

        _store.WriteSecret(_key, "first@gmail.com", "新しいパスワード");
        Assert.Equal("新しいパスワード", _store.ReadSecret(_key));
    }
}
