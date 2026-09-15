using ProductivityMcp.App.Services;
using ProductivityMcp.App.ViewModels;
using ProductivityMcp.Core;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class ConfigurationAppTests
{
    [TestMethod]
    public void ImportCredentials_AcceptsDesktopOAuthFile()
    {
        using var files = TestFiles.Create();
        var source = files.Write("download.json", """
            {
              "installed": {
                "client_id": "test.apps.googleusercontent.com",
                "client_secret": "test-secret"
              }
            }
            """);
        var service = new GoogleSetupService(files.Options);

        var result = service.ImportCredentials(source);

        var success = result as OperationResult<SetupSnapshot>.Success;
        Assert.IsNotNull(success);
        Assert.IsTrue(success.Value.CredentialsPresent);
        Assert.IsTrue(File.Exists(files.Options.CredentialsPath));
    }

    [TestMethod]
    public void ImportCredentials_RejectsNonDesktopOAuthFile()
    {
        using var files = TestFiles.Create();
        var source = files.Write("wrong.json", """{"web":{"client_id":"test"}}""");
        var service = new GoogleSetupService(files.Options);

        var result = service.ImportCredentials(source);

        var failure = result as OperationResult<SetupSnapshot>.Failure;
        Assert.IsNotNull(failure);
        Assert.AreEqual(OperationErrorCode.Configuration, failure.Error.Code);
        Assert.AreEqual("credentialsPath", failure.Error.Field);
        Assert.IsFalse(File.Exists(files.Options.CredentialsPath));
    }

    [TestMethod]
    public void Inspect_AcceptsBundledCredentialsWithoutCopyingThem()
    {
        using var files = TestFiles.Create();
        var bundled = files.Write("release/google-oauth-client.json", "{}");
        var service = new GoogleSetupService(files.Options with { BundledCredentialsPath = bundled });

        var snapshot = service.Inspect();

        Assert.IsTrue(snapshot.CredentialsPresent);
        Assert.IsFalse(File.Exists(files.Options.CredentialsPath));
        Assert.AreEqual(bundled, (files.Options with { BundledCredentialsPath = bundled }).EffectiveCredentialsPath);
    }

    [TestMethod]
    public void Disconnect_RemovesOnlyGoogleToken()
    {
        using var files = TestFiles.Create();
        Directory.CreateDirectory(files.Options.TokenStorePath);
        var token = files.WriteInTokenStore(
            "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user",
            "temporary-token");
        var unrelated = files.WriteInTokenStore("keep-me.txt", "keep");
        var service = new GoogleSetupService(files.Options);

        var result = service.Disconnect(GoogleAccountCatalog.PrimaryAccountKey);

        Assert.IsInstanceOfType<OperationResult<SetupSnapshot>.Success>(result);
        Assert.IsFalse(File.Exists(token));
        Assert.IsTrue(File.Exists(unrelated));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ViewModel_ConnectSuccessUpdatesReadyState()
    {
        var service = new FakeSetupService();
        var viewModel = new MainWindowViewModel(service);

        await viewModel.ConnectCommand.ExecuteAsync(null);

        Assert.IsTrue(viewModel.IsConnected);
        Assert.HasCount(1, viewModel.Accounts);
        Assert.AreEqual("person@example.com", viewModel.Accounts[0].Email);
        Assert.AreEqual("Work", viewModel.Accounts[0].Calendars[1].Name);
        Assert.AreEqual("Groceries", viewModel.Accounts[0].TaskLists[1].Name);
        Assert.AreEqual("1 Google-Konto konfiguriert", viewModel.StatusTitle);
        Assert.IsFalse(viewModel.IsFeedbackError);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ViewModel_StartupVerifiesStoredConnection()
    {
        var service = new FakeSetupService(connected: true);
        var viewModel = new MainWindowViewModel(service);

        await viewModel.InitializeAsync();

        Assert.IsTrue(viewModel.IsConnected);
        Assert.AreEqual(1, service.ConnectCalls);
        Assert.AreEqual("person@example.com", viewModel.Accounts[0].Email);
        Assert.HasCount(3, viewModel.Accounts[0].Calendars);
        Assert.HasCount(2, viewModel.Accounts[0].TaskLists);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ViewModel_AddAccountKeepsExistingAccount()
    {
        var service = new FakeSetupService(connected: true);
        var viewModel = new MainWindowViewModel(service);
        await viewModel.InitializeAsync();

        await viewModel.AddAccountCommand.ExecuteAsync(null);

        Assert.HasCount(2, viewModel.Accounts);
        Assert.AreEqual("person@example.com", viewModel.Accounts[0].Email);
        Assert.AreEqual("second@example.com", viewModel.Accounts[1].Email);
        Assert.AreEqual("2 Google-Konten konfiguriert", viewModel.StatusTitle);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ViewModel_ImportCredentialsDoesNotChooseServicesForUser()
    {
        var service = new FakeSetupService();
        var viewModel = new MainWindowViewModel(service);

        await viewModel.ImportCredentialsAsync("credentials.json");

        Assert.AreEqual(0, service.ConnectCalls);
        Assert.IsTrue(viewModel.ShowConnectionAction);
        Assert.IsFalse(viewModel.HasAccounts);
    }

    [TestMethod]
    public void MultiProvider_ExcludesEmailOnlyAccountsFromCalendarAndTasks()
    {
        using var files = TestFiles.Create();
        var catalog = new GoogleAccountCatalog(files.Options);
        catalog.Upsert(new GoogleAccountRegistration("calendar", "calendar@example.com", true, false));
        catalog.Upsert(new GoogleAccountRegistration("email", "email@example.com", false, true));
        var provider = new MultiGoogleProvider(files.Options);

        var calendarProviders = (Array)typeof(MultiGoogleProvider)
            .GetMethod("CalendarProviders", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(provider, null)!;
        var taskProviders = (Array)typeof(MultiGoogleProvider)
            .GetMethod("TaskProviders", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(provider, null)!;

        Assert.AreEqual(1, calendarProviders.Length);
        Assert.AreEqual(1, taskProviders.Length);
    }

    [TestMethod]
    public void ViewModel_AutostartSettingUpdatesOperatingSystemService()
    {
        var autostart = new FakeAutostartService(enabled: false);
        var viewModel = new MainWindowViewModel(new FakeSetupService(), autostart);

        Assert.IsFalse(viewModel.StartWithSystem);

        viewModel.StartWithSystem = true;

        Assert.IsTrue(autostart.Enabled);
        Assert.AreEqual(1, autostart.SetCalls);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task ViewModel_CancelAddAccountRestoresConnectedState()
    {
        var service = new FakeSetupService(connected: true, blockAddAccount: true);
        var viewModel = new MainWindowViewModel(service);
        await viewModel.InitializeAsync();

        var addAccount = viewModel.AddAccountCommand.ExecuteAsync(null);
        await service.AddAccountStarted;

        Assert.IsTrue(viewModel.IsBusy);
        Assert.IsTrue(viewModel.CanCancelConnection);
        viewModel.CancelConnectionCommand.Execute(null);
        await addAccount;

        Assert.IsFalse(viewModel.IsBusy);
        Assert.IsFalse(viewModel.CanCancelConnection);
        Assert.IsTrue(viewModel.IsConnected);
        Assert.HasCount(1, viewModel.Accounts);
        Assert.AreEqual("person@example.com", viewModel.Accounts[0].Email);
        Assert.AreEqual("Google-Anmeldung wurde abgebrochen.", viewModel.Feedback);
        Assert.IsFalse(viewModel.IsFeedbackError);
    }

    private sealed class FakeAutostartService(bool enabled) : IAutostartService
    {
        public bool Enabled { get; private set; } = enabled;

        public int SetCalls { get; private set; }

        public bool IsSupported => true;

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabledValue)
        {
            Enabled = enabledValue;
            SetCalls++;
        }
    }

    private sealed class FakeSetupService : IGoogleSetupService
    {
        private bool _connected;
        private readonly bool _blockAddAccount;
        private readonly TaskCompletionSource _addAccountStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSetupService(bool connected = false, bool blockAddAccount = false)
        {
            _connected = connected;
            _blockAddAccount = blockAddAccount;
        }

        public int ConnectCalls { get; private set; }

        public System.Threading.Tasks.Task AddAccountStarted => _addAccountStarted.Task;

        public SetupSnapshot Inspect() => new(
            "C:\\test",
            "C:\\test\\credentials.json",
            true,
            "C:\\test\\tokens",
            _connected);

        public OperationResult<SetupSnapshot> ImportCredentials(string sourcePath) =>
            OperationResult.Ok(Inspect());

        public System.Threading.Tasks.Task<OperationResult<ConnectionSummary>> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            _connected = true;
            return System.Threading.Tasks.Task.FromResult<OperationResult<ConnectionSummary>>(
                OperationResult.Ok(Summary()));
        }

        public async System.Threading.Tasks.Task<OperationResult<ConnectionSummary>> AddAccountAsync(
            CancellationToken cancellationToken = default)
        {
            if (_blockAddAccount)
            {
                _addAccountStarted.TrySetResult();
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            _connected = true;
            return OperationResult.Ok(Summary(includeSecondAccount: true));
        }

        public System.Threading.Tasks.Task<OperationResult<ConnectionSummary>> AddEmailAccountAsync(
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<OperationResult<ConnectionSummary>>(OperationResult.Ok(Summary()));

        public System.Threading.Tasks.Task<OperationResult<ConnectionSummary>> EnableEmailAsync(
            string accountKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<OperationResult<ConnectionSummary>>(OperationResult.Ok(Summary()));

        public System.Threading.Tasks.Task<OperationResult<ConnectionSummary>> DisableEmailAsync(
            string accountKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<OperationResult<ConnectionSummary>>(OperationResult.Ok(Summary()));

        public OperationResult<SetupSnapshot> Disconnect(string accountKey)
        {
            _connected = false;
            return OperationResult.Ok(Inspect());
        }

        private static ConnectionSummary Summary(bool includeSecondAccount = false)
        {
            var accounts = new List<AccountConnectionSummary>
            {
                new(
                GoogleAccountCatalog.PrimaryAccountKey,
                "person@example.com",
                [
                    new CalendarInfo("person@example.com", "Personal", "google", true, "Europe/Berlin"),
                    new CalendarInfo("work", "Work", "google", false, "Europe/Berlin"),
                    new CalendarInfo("birthdays", "Birthdays", "google", false, "Europe/Berlin"),
                ],
                [
                    new TaskListInfo("default", "My Tasks", "google", true),
                    new TaskListInfo("groceries", "Groceries", "google", false),
                ]),
            };

            if (includeSecondAccount)
            {
                accounts.Add(new AccountConnectionSummary(
                    "second",
                    "second@example.com",
                    [new CalendarInfo("second@example.com", "Second", "google", true, "UTC")],
                    [new TaskListInfo("second-tasks", "Second Tasks", "google", true)]));
            }

            return new ConnectionSummary(accounts);
        }
    }

    private sealed class TestFiles : IDisposable
    {
        private TestFiles(string root)
        {
            Root = root;
            Options = new GoogleOptions
            {
                CredentialsPath = Path.Combine(root, "config", "credentials.json"),
                TokenStorePath = Path.Combine(root, "tokens"),
                AccountsPath = Path.Combine(root, "accounts.json"),
            };
        }

        public string Root { get; }

        public GoogleOptions Options { get; }

        public static TestFiles Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ProductivityMcp.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestFiles(root);
        }

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteInTokenStore(string name, string content)
        {
            var path = Path.Combine(Options.TokenStorePath, name);
            Directory.CreateDirectory(Options.TokenStorePath);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            var resolvedRoot = Path.GetFullPath(Root);
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ProductivityMcp.Tests"));
            if (resolvedRoot.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
