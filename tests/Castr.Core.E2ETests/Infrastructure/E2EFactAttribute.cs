using System.Diagnostics;

namespace Castr.Core.E2ETests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> for the container-based end-to-end tier. It keeps this expensive,
/// Docker-dependent tier <b>opt-in</b>: a test is skipped unless BOTH
/// <list type="bullet">
///   <item>the <c>CASTR_E2E</c> environment variable is set (to anything non-empty), and</item>
///   <item>a Docker daemon is actually reachable.</item>
/// </list>
/// This means a plain <c>dotnet test</c> on a developer box or a CI stage that has not opted in never
/// runs (and never hangs on) these tests, satisfying the "opt-in / slower stage" requirement. Combine
/// with <c>--filter Category=E2E</c> (the class carries <c>[Trait("Category","E2E")]</c>) to target
/// exactly this tier in a dedicated CI job.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!IsOptedIn())
        {
            Skip = "E2E tier is opt-in: set the CASTR_E2E environment variable to run the Docker fan-out tests.";
            return;
        }

        if (!DockerAvailability.IsAvailable)
        {
            Skip = "Docker daemon is not reachable; skipping container-based E2E tests.";
        }
    }

    private static bool IsOptedIn() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASTR_E2E"));
}

/// <summary>Cheap, cached one-shot probe for a reachable Docker daemon, so E2E tests can skip cleanly rather than hang.</summary>
internal static class DockerAvailability
{
    private static readonly Lazy<bool> Probe = new(Detect);

    public static bool IsAvailable => Probe.Value;

    private static bool Detect()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format \"{{.Server.Version}}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
