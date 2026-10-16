using System.Diagnostics.CodeAnalysis;
using AutoMailChecker.Core;
using AutoMailChecker.Core.Configuration;
using AutoMailChecker.Mail.Imap;
using Microsoft.Extensions.Logging;

namespace AutoMailChecker.Mail;

/// <summary>
/// アカウントごとの読み取り用接続と IMailReader。アカウントをまたいで接続やフォルダーを共有しない。
/// </summary>
public sealed class ImapAccountRegistry : IMailAccountRegistry, IDisposable
{
    private readonly Dictionary<string, (ImapAccountSession Session, ImapMailReader Reader)> _accounts = [];

    public ImapAccountRegistry(
        IEnumerable<AccountConfig> accounts, ImapConnector connector, BodyPreprocessor preprocessor,
        TimeProvider time, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<ImapAccountSession>();
        foreach (var account in accounts)
        {
            var session = new ImapAccountSession(account, connector, time, logger);
            _accounts[account.Id] = (session, new ImapMailReader(session, preprocessor, time));
        }
    }

    public IReadOnlyCollection<string> AccountIds => _accounts.Keys;

    public bool TryGetReader(string accountId, [NotNullWhen(true)] out IMailReader? reader)
    {
        reader = _accounts.TryGetValue(accountId, out var entry) ? entry.Reader : null;
        return reader is not null;
    }

    public void Dispose()
    {
        foreach (var (session, _) in _accounts.Values)
        {
            session.Dispose();
        }
    }
}
