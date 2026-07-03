namespace HotRod.Client;

/// <summary>Which change a <see cref="ClientCacheEntryEvent"/> reports.</summary>
public enum ClientEventType : byte
{
    /// <summary>An entry was added under a key that had none.</summary>
    Created,

    /// <summary>An existing entry's value was replaced.</summary>
    Modified,

    /// <summary>An entry was removed.</summary>
    Removed,

    /// <summary>An entry passed its expiration and was reaped.</summary>
    Expired,
}
