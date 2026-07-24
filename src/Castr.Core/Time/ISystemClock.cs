namespace Castr.Core.Time;

/// <summary>Clock abstraction so protocol state machines are driven by explicit time, never real-time delays — the key move that makes timeout/TTL/retry logic assertable in microseconds instead of flaky real-time tests.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
