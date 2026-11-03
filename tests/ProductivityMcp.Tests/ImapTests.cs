using System.Globalization;
using System.Net;
using System.Text;
using MailKit;
using MailKit.Search;
using MailKit.Security;
using ProductivityMcp.App.Services;
using ProductivityMcp.Core;
using ProductivityMcp.Email;
using ProductivityMcp.Providers.Google;
using ProductivityMcp.Providers.Imap;
using ProductivityMcp.Security;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class ImapTests
{
    [TestMethod]
    public void GenericSearch_CombinesRecipientsWithOrAndConditionsWithAnd()
    {
        var (query, scope) = ImapSearch.Build(new EmailFilter
        {
            From = ["a@example.com", "b@example.com"],
            Subject = "Invoice",
            Unread = true,
        });

        var and = Assert.IsInstanceOfType<BinarySearchQuery>(query);
        Assert.AreEqual(SearchTerm.And, and.Term);
        var terms = Flatten(query).ToArray();
        Assert.AreEqual(2, terms.OfType<TextSearchQuery>().Count(term => term.Term == SearchTerm.FromContains));
        Assert.IsTrue(terms.OfType<TextSearchQuery>().Any(term => term.Term == SearchTerm.SubjectContains && term.Text == "Invoice"));
        Assert.IsTrue(terms.Any(term => term.Term == SearchTerm.NotSeen));
        Assert.IsTrue(Flatten(query, includeBinary: true).OfType<BinarySearchQuery>().Any(term => term.Term == SearchTerm.Or));
        Assert.IsNull(scope.Mailbox);
    }

    [TestMethod]
    public void GenericSearch_ChecksTopLevelTimesExactlyAfterWideningTheServerSearch()
    {
        var after = DateTimeOffset.Parse("2026-10-01T10:00:00+02:00", CultureInfo.InvariantCulture);
        var (query, scope) = ImapSearch.Build(new EmailFilter { After = after, Mailbox = EmailMailbox.Inbox });

        var date = Flatten(query).OfType<DateSearchQuery>().Single();
        Assert.AreEqual(SearchTerm.DeliveredAfter, date.Term);
        Assert.IsLessThan(after.UtcDateTime.Date, date.Date);
        Assert.AreEqual(EmailMailbox.Inbox, scope.Mailbox);
        Assert.IsFalse(scope.Matches(Summary(after.AddMinutes(-1))));
        Assert.IsTrue(scope.Matches(Summary(after.AddMinutes(1))));
    }

    [TestMethod]
    public void GenericSearch_RejectsConditionsStandardImapCannotExpress()
    {
        Assert.ThrowsExactly<UnsupportedFeatureException>(() => ImapSearch.Build(new EmailFilter { Important = true }));
        Assert.ThrowsExactly<UnsupportedFeatureException>(() => ImapSearch.Build(new EmailFilter { Filename = "a.pdf" }));
        Assert.ThrowsExactly<UnsupportedFeatureException>(() => ImapSearch.Build(new EmailFilter
        {
            Or = [new EmailFilter { Mailbox = EmailMailbox.Sent }, new EmailFilter { Unread = true }],
        }));
        Assert.ThrowsExactly<UnsupportedFeatureException>(() => ImapSearch.Build(new EmailFilter
        {
            Not = new EmailFilter { After = DateTimeOffset.UtcNow },
        }));
    }

    [TestMethod]
    public void MailIds_RoundTripAndRejectForeignIds()
    {
        var id = MailIds.Generic("Ordner/Unterordner:mit:Doppelpunkt", 42, new UniqueId(42, 7));
        var (folder, validity, uid) = MailIds.ParseGeneric(id);
        Assert.AreEqual("Ordner/Unterordner:mit:Doppelpunkt", folder);
        Assert.AreEqual(42u, validity);
        Assert.AreEqual(7u, uid.Id);

        Assert.AreEqual(0x18f2a3b4c5d6e7f8UL, MailIds.ParseGmail("18f2a3b4c5d6e7f8"));
        Assert.AreEqual("18f2a3b4c5d6e7f8", MailIds.Gmail(0x18f2a3b4c5d6e7f8UL));
        Assert.ThrowsExactly<KeyNotFoundException>(() => MailIds.ParseGmail(id));
        Assert.ThrowsExactly<KeyNotFoundException>(() => MailIds.ParseGeneric("18f2a3b4c5d6e7f8"));
        Assert.AreEqual("<root@example.com>", MailIds.ParseGenericThread(MailIds.GenericThread("<root@example.com>")));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Discovery_UsesKnownProvidersWithoutNetwork()
    {
        var discovery = new MailServerDiscovery(new HttpClient(new FailingHandler()));

        var gmail = await discovery.DiscoverAsync("person@gmail.com");
        var webDe = await discovery.DiscoverAsync("person@web.de");

        Assert.AreEqual(new MailServer("imap.gmail.com", 993, MailSecurity.SslOnConnect), gmail!.Imap);
        Assert.AreEqual(new MailServer("smtp.gmail.com", 465, MailSecurity.SslOnConnect), gmail.Smtp);
        Assert.AreEqual(MailSecurity.StartTls, webDe!.Smtp.Security);
        await Assert.ThrowsExactlyAsync<UnsupportedFeatureException>(() => discovery.DiscoverAsync("person@outlook.com"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Discovery_FallsBackToAutoconfigAndReturnsNullWhenUnknown()
    {
        var discovery = new MailServerDiscovery(new HttpClient(new StaticHandler(HttpStatusCode.NotFound, "")));

        Assert.IsNull(await discovery.DiscoverAsync("person@unknown.example"));
    }

    [TestMethod]
    public void Autoconfig_ParsesSecureServersAndUsernamePlaceholders()
    {
        const string xml = """
            <clientConfig version="1.1"><emailProvider id="example.org">
              <incomingServer type="pop3"><hostname>pop.example.org</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
              <incomingServer type="imap"><hostname>imap.example.org</hostname><port>143</port><socketType>plain</socketType></incomingServer>
              <incomingServer type="imap"><hostname>imap.example.org</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILLOCALPART%</username></incomingServer>
              <outgoingServer type="smtp"><hostname>smtp.example.org</hostname><port>587</port><socketType>STARTTLS</socketType></outgoingServer>
            </emailProvider></clientConfig>
            """;

        var settings = MailServerDiscovery.ParseAutoconfig(xml, "alex@example.org");

        Assert.IsNotNull(settings);
        Assert.AreEqual(new MailServer("imap.example.org", 993, MailSecurity.SslOnConnect), settings.Imap);
        Assert.AreEqual(new MailServer("smtp.example.org", 587, MailSecurity.StartTls), settings.Smtp);
        Assert.AreEqual("alex", settings.Username);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Catalog_StoresPasswordEncryptedAndRemovesItWithTheAccount()
    {
        using var files = TemporaryMail.Create();
        var account = Account("alex@example.org");

        await files.Catalog.SaveAsync(account, "app-password-1234");

        var secret = Directory.GetFiles(files.Options.SecretsDirectory).Single();
        Assert.DoesNotContain("app-password-1234", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(secret)), StringComparison.Ordinal);
        Assert.DoesNotContain("app-password-1234", await File.ReadAllTextAsync(files.Options.AccountsPath), StringComparison.Ordinal);
        Assert.AreEqual("app-password-1234", await files.Catalog.ReadPasswordAsync(account));
        Assert.AreEqual(account, files.Catalog.Find("ALEX@example.org"));

        files.Catalog.Remove(account.Key);

        Assert.IsEmpty(files.Catalog.List());
        Assert.IsFalse(File.Exists(secret));
        await Assert.ThrowsExactlyAsync<AuthenticationRequiredException>(() => files.Catalog.ReadPasswordAsync(account));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Provider_ReportsUnknownAccountsAndForeignCursors()
    {
        using var files = TemporaryMail.Create();
        await files.Catalog.SaveAsync(Account("alex@gmail.com") with { Imap = new("imap.gmail.com", 993, MailSecurity.SslOnConnect) }, "secret");
        var provider = new MultiImapEmailProvider(files.Catalog, new EmailContentService());

        var accounts = ImapScenario.Success(await provider.ListAccountsAsync());
        var missing = await provider.GetMessagesAsync("nobody@example.org", ["1"], EmailContentFormat.Plain, false);
        var cursor = await provider.QueryAsync(new EmailQuery { Cursor = "not-a-cursor" });

        Assert.AreEqual(new EmailAccountInfo("alex@gmail.com", "gmail"), accounts.Single());
        Assert.AreEqual(OperationErrorCode.NotFound, ((OperationResult<IReadOnlyList<EmailMessageReadResult>>.Failure)missing).Error.Code);
        Assert.AreEqual(OperationErrorCode.Validation, ((OperationResult<EmailQueryResult>.Failure)cursor).Error.Code);
    }

    [TestMethod]
    public void Translate_MapsMailServerFailuresToStableErrors()
    {
        Assert.AreEqual(OperationErrorCode.Authentication, MailOperation.Translate(new AuthenticationException())!.Code);
        Assert.IsTrue(MailOperation.Translate(new IOException())!.Retryable);
        Assert.AreEqual(OperationErrorCode.NotFound, MailOperation.Translate(new KeyNotFoundException("x"))!.Code);
        Assert.IsNull(MailOperation.Translate(new InvalidOperationException()));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Setup_ConnectEmailVerifiesBeforeSavingAndStripsGmailAppPasswordSpaces()
    {
        using var files = TemporaryMail.Create();
        var google = files.GoogleOptions;
        MailAccount? verified = null;
        string? verifiedPassword = null;
        var service = new GoogleSetupService(google, files.Catalog, new MailServerDiscovery(new HttpClient(new FailingHandler())),
            (account, password, _) => { verified = account; verifiedPassword = password; return System.Threading.Tasks.Task.FromResult(true); },
            (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var result = await service.ConnectEmailAsync(new MailConnectionRequest("alex@gmail.com", "abcd efgh ijkl mnop", null));

        var summary = ImapScenario.Success(result);
        Assert.AreEqual("abcdefghijklmnop", verifiedPassword);
        Assert.AreEqual("imap.gmail.com", verified!.Imap.Host);
        Assert.AreEqual("alex@gmail.com", verified.Username);
        Assert.AreEqual("abcdefghijklmnop", await files.Catalog.ReadPasswordAsync(files.Catalog.Find("alex@gmail.com")!));
        var account = summary.Accounts.Single();
        Assert.IsTrue(account.EmailEnabled);
        Assert.IsTrue(account.EmailConnected);
        Assert.IsFalse(account.CalendarTasksEnabled);
        Assert.StartsWith(GoogleSetupService.MailKeyPrefix, account.Key);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Setup_RejectedPasswordIsNotSavedAndExplainsAppPasswords()
    {
        using var files = TemporaryMail.Create();
        var service = new GoogleSetupService(files.GoogleOptions, files.Catalog, new MailServerDiscovery(new HttpClient(new FailingHandler())),
            (_, _, _) => throw new AuthenticationException("rejected"),
            (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var result = await service.ConnectEmailAsync(new MailConnectionRequest("alex@gmail.com", "normal-password", null));

        var failure = (OperationResult<ConnectionSummary>.Failure)result;
        Assert.AreEqual(OperationErrorCode.Authentication, failure.Error.Code);
        Assert.AreEqual("password", failure.Error.Field);
        StringAssert.Contains(failure.Error.Message, "App-Passwort");
        Assert.IsEmpty(files.Catalog.List());
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Setup_UnknownServerAsksForManualSettingsAndMicrosoftIsExplained()
    {
        using var files = TemporaryMail.Create();
        var service = new GoogleSetupService(files.GoogleOptions, files.Catalog,
            new MailServerDiscovery(new HttpClient(new StaticHandler(HttpStatusCode.NotFound, ""))),
            (_, _, _) => System.Threading.Tasks.Task.FromResult(false), (_, _) => System.Threading.Tasks.Task.CompletedTask);

        var unknown = (OperationResult<ConnectionSummary>.Failure)await service.ConnectEmailAsync(new("alex@unknown.example", "secret", null));
        var microsoft = (OperationResult<ConnectionSummary>.Failure)await service.ConnectEmailAsync(new("alex@hotmail.com", "secret", null));

        Assert.AreEqual("settings", unknown.Error.Field);
        Assert.AreEqual(OperationErrorCode.Unsupported, microsoft.Error.Code);
        StringAssert.Contains(microsoft.Error.Message, "Microsoft");
    }

    [TestMethod]
    public async System.Threading.Tasks.Task Setup_MailboxJoinsTheGoogleAccountWithTheSameAddressAndDisconnectRemovesBoth()
    {
        using var files = TemporaryMail.Create();
        new GoogleAccountCatalog(files.GoogleOptions).Upsert(new GoogleAccountRegistration("google-key", "alex@gmail.com"));
        await files.Catalog.SaveAsync(Account("alex@gmail.com"), "secret");
        var service = new GoogleSetupService(files.GoogleOptions, files.Catalog, null,
            (_, _, _) => System.Threading.Tasks.Task.FromResult(true),
            (_, _) => throw new AuthenticationException("expired"));

        var summary = ImapScenario.Success(await service.ConnectAsync());

        var account = summary.Accounts.Single();
        Assert.AreEqual("google-key", account.Key);
        Assert.IsTrue(account.CalendarTasksEnabled);
        Assert.IsTrue(account.EmailEnabled);
        Assert.IsFalse(account.EmailConnected);
        Assert.AreEqual(OperationErrorCode.Authentication, account.EmailError!.Code);

        service.Disconnect("google-key");

        Assert.IsEmpty(files.Catalog.List());
        Assert.IsEmpty(new GoogleAccountCatalog(files.GoogleOptions).List());
    }

    private static MailAccount Account(string email) => new(
        MailAccountCatalog.CreateAccountKey(), email, email,
        new MailServer("imap.example.org", 993, MailSecurity.SslOnConnect),
        new MailServer("smtp.example.org", 465, MailSecurity.SslOnConnect));

    private static MessageSummary Summary(DateTimeOffset date) => new(1) { InternalDate = date };

    private static IEnumerable<SearchQuery> Flatten(SearchQuery query, bool includeBinary = false)
    {
        switch (query)
        {
            case BinarySearchQuery binary:
                if (includeBinary) yield return binary;
                foreach (var item in Flatten(binary.Left, includeBinary).Concat(Flatten(binary.Right, includeBinary))) yield return item;
                break;
            case UnarySearchQuery unary:
                foreach (var item in Flatten(unary.Operand, includeBinary)) yield return item;
                break;
            default:
                yield return query;
                break;
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException($"Unexpected network request to {request.RequestUri}.");
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

internal sealed class TestMasterKeyStore : IMasterKeyStore
{
    public System.Threading.Tasks.Task<byte[]> GetOrCreateAsync(CancellationToken cancellationToken) =>
        System.Threading.Tasks.Task.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
}

internal sealed class TemporaryMail : IDisposable
{
    private readonly DirectoryInfo _root;

    private TemporaryMail(DirectoryInfo root)
    {
        _root = root;
        Options = new MailOptions
        {
            DataDirectory = Path.Combine(root.FullName, "mail"),
            DownloadsDirectory = Path.Combine(root.FullName, "downloads"),
        };
        Catalog = new MailAccountCatalog(Options, new ProtectedFileStore(new TestMasterKeyStore()));
        GoogleOptions = new GoogleOptions
        {
            CredentialsPath = Path.Combine(root.FullName, "credentials.json"),
            TokenStorePath = Path.Combine(root.FullName, "tokens"),
            AccountsPath = Path.Combine(root.FullName, "accounts.json"),
        };
    }

    public MailOptions Options { get; }
    public MailAccountCatalog Catalog { get; }
    public GoogleOptions GoogleOptions { get; }

    public static TemporaryMail Create() => new(Directory.CreateTempSubdirectory("ProductivityMcp.Mail-"));

    public void Dispose() => _root.Delete(recursive: true);
}

internal static class ImapScenario
{
    public static T Success<T>(OperationResult<T> result) => MailboxScenario.Success(result);
}
