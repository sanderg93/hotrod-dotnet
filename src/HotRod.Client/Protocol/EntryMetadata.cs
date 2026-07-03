namespace HotRod.Client.Protocol;

/// <summary>
/// The per-entry metadata that precedes the value in a metadata response (GetWithMetadata, and the
/// previous-value body of a FORCE_RETURN_VALUE write). A <c>-1</c> lifespan or maxIdle means that
/// dimension never expires, in which case its created/lastUsed timestamp is absent too.
/// </summary>
internal readonly record struct EntryMetadata(long Created, int Lifespan, long LastUsed, int MaxIdle, long Version);
