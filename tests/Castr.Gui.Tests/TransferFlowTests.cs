using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NSec.Cryptography;
using Castr.Core.Manifest;
using Castr.Core.Security;
using Castr.Core.Transport.InMemory;
using Castr.Core.Trust;
using Castr.Gui.Services;
using Castr.Gui.Trust;
using Castr.Gui.ViewModels;

namespace Castr.Gui.Tests;

/// <summary>
/// End-to-end through the real view-models: a <see cref="SendViewModel"/> and <see cref="ReceiveViewModel"/>
/// wired onto one in-process <see cref="InMemoryNetwork"/>, driving real Sender/Receiver sessions, with
/// progress marshaled onto the (headless) UI thread — the same code the desktop head runs, minus UDP.
/// </summary>
public class TransferFlowTests
{
    [AvaloniaFact]
    public async Task RealTransfer_OverInMemory_CompletesAndProgressReflectsIt()
    {
        var network = new InMemoryNetwork();
        var factory = new InMemoryTransportFactory(network);

        using var signingKey = ManifestSigner.CreateSigningKey();
        var send = new SendViewModel(factory, signingKey);

        // Receiver already trusts this sender, so no prompt is needed for the happy path.
        var trustStore = new InMemoryTrustStore();
        var rawPublic = signingKey.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        trustStore.Upsert(new TrustEntry(
            PublicKeyId.FromRawEd25519(rawPublic), "sender", TrustStatus.Trusted,
            DateTimeOffset.UnixEpoch, TrustEntrySource.Manual));
        var receive = new ReceiveViewModel(factory, trustStore, new AutoTrustPrompt(false));

        var payload = new byte[12_000];
        new Random(99).NextBytes(payload);

        var tempRoot = Path.Combine(Path.GetTempPath(), "castr-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var sourcePath = Path.Combine(tempRoot, "payload.bin");
        await File.WriteAllBytesAsync(sourcePath, payload);
        var destDir = Path.Combine(tempRoot, "incoming");

        send.FilePath = sourcePath;
        send.ChunkSize = 1024;
        receive.DestinationDirectory = destDir;
        receive.SelectedPolicyIndex = 0; // Deny unknown (sender is trusted anyway)

        try
        {
            // Fire both flows; they run cooperatively on the dispatcher.
            _ = receive.StartCommand.ExecuteAsync(null);
            _ = send.StartCommand.ExecuteAsync(null);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!receive.Progress.IsComplete && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            Dispatcher.UIThread.RunJobs();

            Assert.True(receive.Progress.IsComplete, "Receiver progress never reached completion.");
            Assert.Equal(100.0, receive.Progress.Percent, 3);
            Assert.Equal(payload.Length, receive.Progress.TotalBytes);

            var receivedPath = Path.Combine(destDir, "payload.bin");
            Assert.True(File.Exists(receivedPath), "Received file was not written to disk.");
            Assert.Equal(payload, await File.ReadAllBytesAsync(receivedPath));
        }
        finally
        {
            send.StopCommand.Execute(null);
            receive.StopCommand.Execute(null);

            // Let both flows unwind.
            for (int i = 0; i < 20 && (send.IsRunning || receive.IsRunning); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
