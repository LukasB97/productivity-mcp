namespace ProductivityMcp.Providers.Imap;

/// <summary>Sign-in checks used by the configuration app.</summary>
public static class MailAccountVerifier
{
    /// <summary>Signs in to IMAP and SMTP without changing the mailbox. Returns whether the server is Gmail.</summary>
    public static Task<bool> VerifySignInAsync(MailAccount account, string password, CancellationToken cancellationToken = default) =>
        ImapSession.VerifyAsync(account, password, cancellationToken);

    /// <summary>Signs in to IMAP with the stored password.</summary>
    public static async Task CheckStoredAsync(MailAccount account, MailAccountCatalog catalog, CancellationToken cancellationToken = default)
    {
        var password = await catalog.ReadPasswordAsync(account, cancellationToken).ConfigureAwait(false);
        await using var session = await ImapSession.OpenAsync(account, password, cancellationToken).ConfigureAwait(false);
    }
}
