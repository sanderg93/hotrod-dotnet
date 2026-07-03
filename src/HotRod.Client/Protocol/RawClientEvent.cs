namespace HotRod.Client.Protocol;

/// <summary>
/// One cache event exactly as it comes off the wire: the event opcode, the raw key bytes and — for
/// created/modified events only — the entry's new version. <see cref="HasVersion"/> distinguishes a
/// removed/expired event (no version) from a created/modified one whose version happens to be 0.
/// The listener id and command-retried flag are carried through for the dispatcher.
/// </summary>
internal readonly record struct RawClientEvent(
    byte Opcode,
    byte[] ListenerId,
    bool CommandRetried,
    byte[] Key,
    long Version,
    bool HasVersion);
