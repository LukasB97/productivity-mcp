using System.Text.Json;

namespace ProductivityMcp.Providers.Google;

public sealed record GoogleAccountRegistration(string Key, string Email);

public sealed class GoogleAccountCatalog(GoogleOptions baseOptions)
{
    public const string PrimaryAccountKey = "primary";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public IReadOnlyList<GoogleAccountRegistration> List()
    {
        var registrations = ReadStored().ToList();
        if (HasToken(PrimaryAccountKey) && registrations.All(item => item.Key != PrimaryAccountKey))
        {
            registrations.Insert(0, new GoogleAccountRegistration(PrimaryAccountKey, "Google-Konto"));
        }

        return registrations.Where(item => HasToken(item.Key)).ToArray();
    }

    public GoogleOptions OptionsFor(string accountKey) => baseOptions with
    {
        TokenStorePath = TokenDirectory(accountKey),
    };

    public static string CreateAccountKey() => Guid.NewGuid().ToString("N");

    public void Upsert(GoogleAccountRegistration registration)
    {
        var registrations = ReadStored()
            .Where(item => !string.Equals(item.Key, registration.Key, StringComparison.Ordinal))
            .Append(registration)
            .ToArray();
        WriteStored(registrations);
    }

    public void Remove(string accountKey)
    {
        var registrations = ReadStored()
            .Where(item => !string.Equals(item.Key, accountKey, StringComparison.Ordinal))
            .ToArray();
        WriteStored(registrations);
    }

    public bool HasToken(string accountKey) =>
        File.Exists(Path.Combine(TokenDirectory(accountKey), GoogleServiceFactory.TokenFileName));

    public string TokenDirectory(string accountKey) =>
        string.Equals(accountKey, PrimaryAccountKey, StringComparison.Ordinal)
            ? Path.GetFullPath(baseOptions.TokenStorePath)
            : Path.Combine(Path.GetFullPath(baseOptions.TokenStorePath), "accounts", accountKey);

    private GoogleAccountRegistration[] ReadStored()
    {
        if (!File.Exists(baseOptions.AccountsPath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<GoogleAccountRegistration[]>(
                       File.ReadAllText(baseOptions.AccountsPath)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteStored(IReadOnlyList<GoogleAccountRegistration> registrations)
    {
        var path = Path.GetFullPath(baseOptions.AccountsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(registrations, SerializerOptions));
    }
}
