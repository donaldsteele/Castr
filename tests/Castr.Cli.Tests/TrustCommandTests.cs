using System.Security.Cryptography;
using Castr.Cli;
using Castr.Core.Security;
using Castr.Core.Trust;
using Spectre.Console.Testing;

namespace Castr.Cli.Tests;

/// <summary>Behavioral tests for the `castr trust` subcommands driving a real FileTrustStore on disk.</summary>
public class TrustCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "castr-cli-tests", Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "trust.json");

    public TrustCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string SampleKeyId() =>
        PublicKeyId.FromRawEd25519(RandomNumberGenerator.GetBytes(32)).Value;

    [Fact]
    public void Add_ThenList_ShowsTrustedEntry()
    {
        var console = new TestConsole();
        var id = SampleKeyId();

        Assert.Equal(ExitCodes.Success, TrustRunner.Add(StorePath, id, "build-server", TrustStatus.Trusted, console));

        var entries = new FileTrustStore(StorePath).All();
        var entry = Assert.Single(entries);
        Assert.Equal(id, entry.PublicKeyId.Value);
        Assert.Equal(TrustStatus.Trusted, entry.Status);
        Assert.Equal("build-server", entry.DisplayName);

        Assert.Equal(ExitCodes.Success, TrustRunner.List(StorePath, console));
        Assert.Contains("build-server", console.Output);
    }

    [Fact]
    public void Block_PersistsBlockedStatus()
    {
        var id = SampleKeyId();
        Assert.Equal(ExitCodes.Success, TrustRunner.Add(StorePath, id, null, TrustStatus.Blocked, new TestConsole()));

        var entry = Assert.Single(new FileTrustStore(StorePath).All());
        Assert.Equal(TrustStatus.Blocked, entry.Status);
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var id = SampleKeyId();
        TrustRunner.Add(StorePath, id, null, TrustStatus.Trusted, new TestConsole());

        Assert.Equal(ExitCodes.Success, TrustRunner.Remove(StorePath, id, new TestConsole()));
        Assert.Empty(new FileTrustStore(StorePath).All());
    }

    [Fact]
    public void Remove_NonexistentEntry_ReturnsInvalidInput()
    {
        Assert.Equal(ExitCodes.InvalidInput, TrustRunner.Remove(StorePath, SampleKeyId(), new TestConsole()));
    }

    [Fact]
    public void Add_MalformedKeyId_ReturnsInvalidInput()
    {
        Assert.Equal(ExitCodes.InvalidInput,
            TrustRunner.Add(StorePath, "not-a-valid-id", null, TrustStatus.Trusted, new TestConsole()));
    }

    [Fact]
    public void List_EmptyStore_Succeeds()
    {
        var console = new TestConsole();
        Assert.Equal(ExitCodes.Success, TrustRunner.List(StorePath, console));
        Assert.Contains("empty", console.Output);
    }
}
