namespace ProductivityMcp.Providers.Google;

public sealed record GoogleOptions
{
    public required string CredentialsPath { get; init; }
    public string? BundledCredentialsPath { get; init; }
    public required string TokenStorePath { get; init; }
    public required string AccountsPath { get; init; }
    public string ApplicationName { get; init; } = "Productivity MCP";

    public string EffectiveCredentialsPath =>
        File.Exists(CredentialsPath) || string.IsNullOrWhiteSpace(BundledCredentialsPath)
            ? CredentialsPath
            : BundledCredentialsPath;

    public static GoogleOptions FromEnvironment()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataDirectory = Path.Combine(appData, "ProductivityMcp");

        return new GoogleOptions
        {
            CredentialsPath = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS")
                ?? Path.Combine(dataDirectory, "credentials.json"),
            BundledCredentialsPath = Path.Combine(AppContext.BaseDirectory, "google-oauth-client.json"),
            TokenStorePath = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_GOOGLE_TOKENS")
                ?? Path.Combine(dataDirectory, "tokens"),
            AccountsPath = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_GOOGLE_ACCOUNTS")
                ?? Path.Combine(dataDirectory, "accounts.json"),
        };
    }
}
