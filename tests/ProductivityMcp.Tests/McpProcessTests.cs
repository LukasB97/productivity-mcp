using System.Text.Json;
using ModelContextProtocol.Client;
using ProductivityMcp.Providers.Google;

namespace ProductivityMcp.Tests;

[TestClass]
public sealed class McpProcessTests
{
    [TestMethod]
    public async Task StdioServer_ListsSchemasValidatesInputsAndReloadsAccountsWithoutOpeningBrowser()
    {
        var directory = Directory.CreateTempSubdirectory("ProductivityMcp.McpTests-");
        try
        {
            var options = new GoogleOptions
            {
                CredentialsPath = Path.Combine(directory.FullName, "credentials.json"),
                AccountsPath = Path.Combine(directory.FullName, "accounts.json"),
                TokenStorePath = Path.Combine(directory.FullName, "tokens"),
            };
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["PRODUCTIVITY_MCP_GOOGLE_CREDENTIALS"] = options.CredentialsPath;
            environment["PRODUCTIVITY_MCP_GOOGLE_ACCOUNTS"] = options.AccountsPath;
            environment["PRODUCTIVITY_MCP_GOOGLE_TOKENS"] = options.TokenStorePath;
            var packagedServer = Environment.GetEnvironmentVariable("PRODUCTIVITY_MCP_TEST_SERVER");
            var serverAssembly = Path.Combine(AppContext.BaseDirectory, "ProductivityMcp.Server.dll");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Name = "isolated-release-smoke",
                Command = packagedServer ?? "dotnet",
                Arguments = packagedServer is null ? [serverAssembly] : [],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
            }), cancellationToken: timeout.Token);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.HasCount(27, tools);
            var update = tools.Single(tool => tool.Name == "calendar.events.update").JsonSchema;
            Assert.IsTrue(update.GetProperty("properties").TryGetProperty("calendarId", out _));
            Assert.IsFalse(update.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "calendarId"));
            var send = tools.Single(tool => tool.Name == "email.messages.send").JsonSchema;
            var messageSchema = send.GetProperty("properties").GetProperty("message");
            if (messageSchema.TryGetProperty("required", out var required))
                Assert.IsFalse(required.EnumerateArray().Any(field => field.GetString() is "cc" or "bcc" or "attachments"),
                    "Defaulted collections must remain optional in the public schema.");

            var accounts = await client.CallToolAsync("email.accounts.list", cancellationToken: timeout.Token);
            Assert.IsFalse(accounts.IsError == true);
            Assert.AreEqual(0, accounts.StructuredContent!.Value.GetProperty("result").GetArrayLength());

            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
                {"account":"test@example.com","message":{"to":null,"body":{"format":"html","content":"Test"}}}
                """);
            var invalid = await client.CallToolAsync("email.messages.send", arguments, cancellationToken: timeout.Token);
            Assert.IsTrue(invalid.IsError);
            Assert.AreEqual("validation_error", invalid.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());

            // A catalog change is picked up by the already-running server. A missing
            // token must return promptly, not start an interactive authorization flow.
            await File.WriteAllTextAsync(options.CredentialsPath,
                """{"installed":{"client_id":"test.apps.googleusercontent.com","client_secret":"synthetic-test-value"}}""", timeout.Token);
            new GoogleAccountCatalog(options).Upsert(new("synthetic", "test@example.com"));
            var missingToken = await client.CallToolAsync("calendar.list", cancellationToken: timeout.Token);
            Assert.IsTrue(missingToken.IsError);
            Assert.AreEqual("authentication_error", missingToken.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        }
        finally { directory.Delete(recursive: true); }
    }
}
