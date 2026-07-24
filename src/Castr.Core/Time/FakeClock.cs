namespace Castr.Core.Time;

/// <summary>Test double for <see cref="ISystemClock"/>: time only moves when the test tells it to.</summary>
public sealed class FakeClock(DateTimeOffset start) : ISystemClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public void Advance(TimeSpan delta) => UtcNow += delta;

    public void Set(DateTimeOffset value) => UtcNow = value;
}
