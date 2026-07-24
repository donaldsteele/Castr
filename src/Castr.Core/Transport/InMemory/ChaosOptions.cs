namespace Castr.Core.Transport.InMemory;

/// <summary>Per-delivery fault injection for <see cref="InMemoryNetwork"/>, so repair-protocol tests can exercise loss/duplication/reordering deterministically via a seeded RNG.</summary>
public sealed record ChaosOptions(
    double LossProbability = 0,
    double DuplicateProbability = 0,
    TimeSpan MinLatency = default,
    TimeSpan MaxLatency = default,
    int? RandomSeed = null)
{
    public static ChaosOptions None => new();
}
