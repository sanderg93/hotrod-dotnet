namespace HotRod.Client;

/// <summary>
/// A value together with its server-assigned entry version. The version is used for optimistic
/// concurrency: pass it to <c>ReplaceWithVersionAsync</c>/<c>RemoveWithVersionAsync</c> so the
/// operation only runs if the entry has not changed since it was read.
/// </summary>
public sealed record Versioned<T>(T Value, long Version);
