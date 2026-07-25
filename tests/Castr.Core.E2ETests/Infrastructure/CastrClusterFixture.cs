using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;

namespace Castr.Core.E2ETests.Infrastructure;

/// <summary>
/// Shared, once-per-run setup for the container fan-out tests: it publishes the shipped <c>Castr.Cli</c>
/// as a self-contained linux-x64 binary, bakes it into a small Debian image (with iproute2 for
/// <c>tc netem</c> and coreutils for <c>sha256sum</c>), and creates one user-defined bridge network that
/// carries real IP multicast between containers.
///
/// The Docker default user-defined <b>bridge</b> network passes multicast cleanly with no special
/// configuration (verified empirically for this milestone), so no macvlan / explicit multicast tuning is
/// needed — all containers share the bridge's L2 segment and IGMP/flooding delivers the group traffic.
///
/// Setup is skipped entirely when the tier is not opted in or Docker is unreachable, so nothing is built
/// or downloaded on machines that will only skip the tests.
/// </summary>
public sealed class CastrClusterFixture : IAsyncLifetime
{
    private IFutureDockerImage? _image;

    public INetwork Network { get; private set; } = null!;

    public IFutureDockerImage Image => _image!;

    public bool Ready { get; private set; }

    public async Task InitializeAsync()
    {
        // Match the E2EFact gating: don't build/publish anything if the tests will just skip.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASTR_E2E")) || !DockerAvailability.IsAvailable)
            return;

        ConfigureDockerEndpoint();

        var publishDir = PublishCli();
        StageDockerfile(publishDir);

        _image = new ImageFromDockerfileBuilder()
            .WithName("castr-e2e-tests:latest")
            .WithDockerfileDirectory(publishDir)
            .WithDockerfile("Dockerfile")
            .WithCleanUp(false) // keep the image cached between runs; the publish step is the slow part
            .Build();
        await _image.CreateAsync();

        Network = new NetworkBuilder()
            .WithName($"castr-e2e-{Guid.NewGuid():N}")
            .WithDriver(DotNet.Testcontainers.Configurations.NetworkDriver.Bridge)
            .Build();
        await Network.CreateAsync();

        Ready = true;
    }

    public async Task DisposeAsync()
    {
        if (Network is not null)
            await Network.DeleteAsync();
        // The image is intentionally left cached (WithCleanUp(false)) to keep reruns fast.
    }

    /// <summary>
    /// Points Testcontainers at the Docker Desktop <c>desktop-linux</c> named pipe when nothing else has chosen
    /// an endpoint.
    ///
    /// <para>Without this, Testcontainers probes the legacy <c>docker_engine</c> pipe, which does not exist when
    /// the active context is <c>desktop-linux</c>, and <b>hangs indefinitely rather than failing</b> — observed as
    /// ~20 minute runs with zero Docker activity.</para>
    ///
    /// <para>It has to be set here, in-process, rather than by exporting <c>DOCKER_HOST</c>: the docker CLI and
    /// Docker.DotNet disagree on how many slashes an npipe URI takes (the CLI accepts only
    /// <c>npipe:////./pipe/...</c>, Docker.DotNet only <c>npipe://./pipe/...</c>), and
    /// <see cref="DockerAvailability"/> shells out to the CLI to decide whether to skip — so any single exported
    /// value breaks one of the two. Setting it after that probe has its own value scopes the workaround to this
    /// test process. The earlier fix for this was a machine-global <c>~/.testcontainers.properties</c>, which
    /// changed Docker resolution for every Testcontainers project on the host and was invisible from the repo.</para>
    ///
    /// <para>Deliberately does not override an endpoint the environment already specifies, so CI and Linux hosts
    /// (where the default unix socket is correct) are unaffected.</para>
    /// </summary>
    private static void ConfigureDockerEndpoint()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            return;

        const string desktopLinuxPipe = "npipe://./pipe/dockerDesktopLinuxEngine";
        if (File.Exists(@"\\.\pipe\dockerDesktopLinuxEngine"))
            Environment.SetEnvironmentVariable("DOCKER_HOST", desktopLinuxPipe);
    }

    /// <summary>Publishes Castr.Cli as a self-contained single-file linux-x64 binary and returns the output directory.</summary>
    private static string PublishCli()
    {
        var repoRoot = FindRepoRoot();
        var csproj = Path.Combine(repoRoot, "src", "Castr.Cli", "Castr.Cli.csproj");

        // Per-repo-root output directory. This was a single fixed %TEMP%/castr-e2e-publish shared by every
        // worktree and branch on the machine, and `dotnet publish -o` does not clean its target — so files from
        // an unrelated branch (stale .pdbs were observed) accumulated in the Docker build context. The `castr`
        // binary itself is always republished, so this was not a correctness hazard, but a per-root directory
        // removes the class rather than the instance.
        var outDir = Path.Combine(
            Path.GetTempPath(), "castr-e2e-publish", StableDirectoryToken(repoRoot));
        Directory.CreateDirectory(outDir);

        var args =
            $"publish \"{csproj}\" -c Release -r linux-x64 --self-contained true " +
            $"-p:PublishSingleFile=true -o \"{outDir}\"";

        using var process = Process.Start(new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start 'dotnet publish'.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'dotnet publish' failed (exit {process.ExitCode}).\n{stdout}\n{stderr}");

        var binary = Path.Combine(outDir, "castr");
        if (!File.Exists(binary))
            throw new InvalidOperationException($"Publish did not produce the expected 'castr' binary at {binary}.");

        return outDir;
    }

    /// <summary>
    /// A short, filesystem-safe, stable token for an absolute path, so each worktree/branch gets its own publish
    /// directory. Content-addressed rather than a sanitised path so the name stays short and collision-free.
    /// </summary>
    private static string StableDirectoryToken(string path)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>Copies the checked-in Dockerfile into the publish (build-context) directory as 'Dockerfile'.</summary>
    private static void StageDockerfile(string publishDir)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Docker", "castr.Dockerfile");
        if (!File.Exists(source))
            throw new FileNotFoundException($"Dockerfile not found at {source}.");
        File.Copy(source, Path.Combine(publishDir, "Dockerfile"), overwrite: true);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Castr.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repository root (Castr.sln) from the test output directory.");
    }
}

[CollectionDefinition(Name)]
public sealed class CastrClusterCollection : ICollectionFixture<CastrClusterFixture>
{
    public const string Name = "castr-cluster";
}
