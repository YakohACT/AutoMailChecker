using System.Diagnostics.CodeAnalysis;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Mail.Imap;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Mail;

/// <summary>
/// アカウントごとの読み取り口と移動口。どちらも同じアカウントの「必要なときだけ開く接続」を使い、
/// アカウントをまたいで接続やフォルダーを共有しない。
/// </summary>
public sealed class ImapAccountRegistry : IMailAccountRegistry, IDisposable
{
    private readonly Dictionary<string, Account> _accounts = [];

    public ImapAccountRegistry(
        IConfigProvider config, ImapConnector connector, BodyPreprocessor preprocessor,
        TimeProvider time, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<ImapAccountSession>();
        foreach (var account in config.Current.Accounts)
        {
            var session = new ImapAccountSession(account, connector, time, logger);
            _accounts[account.Id] = new Account(
                session, new ImapMailReader(session, preprocessor, time), new ImapItemFolderMover(session, config));
        }
    }

    public IReadOnlyCollection<string> AccountIds => _accounts.Keys;

    public bool TryGetReader(string accountId, [NotNullWhen(true)] out IMailReader? reader)
    {
        reader = _accounts.TryGetValue(accountId, out var account) ? account.Reader : null;
        return reader is not null;
    }

    public bool TryGetMover(string accountId, [NotNullWhen(true)] out IItemFolderMover? mover)
    {
        mover = _accounts.TryGetValue(accountId, out var account) ? account.Mover : null;
        return mover is not null;
    }

    public void Dispose()
    {
        foreach (var account in _accounts.Values)
        {
            account.Session.Dispose();
        }
    }

    private sealed record Account(ImapAccountSession Session, ImapMailReader Reader, ImapItemFolderMover Mover);
}
