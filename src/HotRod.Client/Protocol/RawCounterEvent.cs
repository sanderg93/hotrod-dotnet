namespace HotRod.Client.Protocol;

/// <summary>
/// One counter event exactly as it comes off the wire: the counter's name, the listener id it was
/// pushed for, and the old/new value and state pair. Matches Infinispan's counter event body (op code
/// 0x66): name, listener id, one state byte packing the old state in its low two bits and the new
/// state in the next two, then the old and new values as 8-byte longs.
/// </summary>
internal readonly record struct RawCounterEvent(
    string CounterName,
    byte[] ListenerId,
    long OldValue,
    CounterEventState OldState,
    long NewValue,
    CounterEventState NewState);
