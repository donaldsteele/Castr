using Spectre.Console;
using Castr.Core.Security;
using Castr.Core.Trust;

namespace Castr.Cli;

/// <summary>
/// Direct management of the local <see cref="FileTrustStore"/> — list/add/block/remove — so senders can be
/// pre-approved (or blocked) headlessly, without ever needing the interactive TOFU prompt.
/// </summary>
internal static class TrustRunner
{
    public static int List(string trustStorePath, IAnsiConsole console)
    {
        var store = new FileTrustStore(trustStorePath);
        var entries = store.All();
        if (entries.Count == 0)
        {
            console.WriteLine("(trust store is empty)");
            return ExitCodes.Success;
        }

        var table = new Table().AddColumns("Status", "Display name", "Source", "Added", "Public key id");
        foreach (var e in entries)
            table.AddRow(
                e.Status.ToString(),
                Markup.Escape(e.DisplayName),
                e.Source.ToString(),
                e.AddedAt.ToString("u"),
                Markup.Escape(e.PublicKeyId.Value));
        console.Write(table);
        return ExitCodes.Success;
    }

    public static int Add(string trustStorePath, string publicKeyId, string? displayName, TrustStatus status, IAnsiConsole console)
    {
        var id = new PublicKeyId(publicKeyId);
        if (!TryValidate(id, console))
            return ExitCodes.InvalidInput;

        var store = new FileTrustStore(trustStorePath);
        store.Upsert(new TrustEntry(id, displayName ?? publicKeyId, status, DateTimeOffset.UtcNow, TrustEntrySource.Manual));
        console.MarkupLineInterpolated($"[green]{status}[/] {publicKeyId}");
        return ExitCodes.Success;
    }

    public static int Remove(string trustStorePath, string publicKeyId, IAnsiConsole console)
    {
        var store = new FileTrustStore(trustStorePath);
        var id = new PublicKeyId(publicKeyId);
        if (store.Find(id) is null)
        {
            console.MarkupLineInterpolated($"[yellow]No such entry:[/] {publicKeyId}");
            return ExitCodes.InvalidInput;
        }

        store.Remove(id);
        console.MarkupLineInterpolated($"[green]removed[/] {publicKeyId}");
        return ExitCodes.Success;
    }

    private static bool TryValidate(PublicKeyId id, IAnsiConsole console)
    {
        try
        {
            var raw = id.ToRawEd25519();
            if (raw.Length != 32)
                throw new FormatException("Ed25519 public keys must be exactly 32 bytes.");
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            console.MarkupLineInterpolated($"[red]Not a valid ed25519 public key id:[/] {ex.Message}");
            console.WriteLine("Expected form: ed25519:<base64 of 32-byte public key>");
            return false;
        }
    }
}
