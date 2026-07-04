namespace HotRod.Client;

/// <summary>
/// Which entry changes a listener wants to be notified of. The values match the HotRod interest
/// bitmask sent on registration, so the server only pushes the event types asked for.
/// </summary>
[Flags]
public enum ClientListenerInterest : byte
{
    /// <summary>No events; the listener receives nothing.</summary>
    None = 0,

    /// <summary>Notify when a new entry is created.</summary>
    Created = 0x01,

    /// <summary>Notify when an existing entry is modified.</summary>
    Modified = 0x02,

    /// <summary>Notify when an entry is removed.</summary>
    Removed = 0x04,

    /// <summary>Notify when an entry expires.</summary>
    Expired = 0x08,

    /// <summary>All event types combined.</summary>
    All = Created | Modified | Removed | Expired,
}

/// <summary>
/// Tunes a client listener registration. By default a listener subscribes to every event type and is
/// not replayed the cache's current contents.
/// </summary>
public sealed record ClientListenerOptions
{
    /// <summary>The event types to subscribe to. Defaults to all of them.</summary>
    public ClientListenerInterest Interests { get; init; } = ClientListenerInterest.All;

    /// <summary>
    /// When true the server first replays a <see cref="ClientEventType.Created"/> event for every entry
    /// already in the cache, then streams live changes. Defaults to false (live changes only).
    /// </summary>
    public bool IncludeCurrentState { get; init; }
}
