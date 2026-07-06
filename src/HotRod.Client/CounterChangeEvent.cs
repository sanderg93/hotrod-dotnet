namespace HotRod.Client;

/// <summary>
/// A counter's validity state at one point of a change. Only a bounded strong counter can be anything
/// other than <see cref="Valid"/>: reaching a bound is reported here rather than by refusing the update,
/// since the update that hits the bound is still the one that produced this event. Matches Infinispan's
/// <c>CounterState</c>.
/// </summary>
public enum CounterEventState : byte
{
    /// <summary>The value is within bounds (always the case for unbounded strong and weak counters).</summary>
    Valid = 0,

    /// <summary>The value is at (or was, for the old state) the counter's configured lower bound.</summary>
    LowerBoundReached = 1,

    /// <summary>The value is at (or was, for the old state) the counter's configured upper bound.</summary>
    UpperBoundReached = 2,
}

/// <summary>
/// A single counter-value change pushed by the server to a registered <see cref="CounterListener"/>.
/// Carries the value and state both before and after the change, so a handler can tell what happened
/// without a separate read.
/// </summary>
public sealed record CounterChangeEvent(
    string CounterName,
    long OldValue,
    CounterEventState OldState,
    long NewValue,
    CounterEventState NewState);
