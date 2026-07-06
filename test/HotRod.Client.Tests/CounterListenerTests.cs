using System.Buffers;
using System.Buffers.Binary;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the counter-listener wire format against Infinispan's encoding: the add/remove-listener request
/// bodies (counter name then listener id — no filters, converters, or interest bitmask, unlike a cache
/// listener) and the pushed counter-event body (counter name, listener id, one state byte packing the
/// old state in its low two bits and the new state in the next two, then the old and new values as
/// 8-byte longs). The layout tests run without a server; the <c>Live_</c> test exercises a real server
/// end-to-end and is skipped unless <c>HOTROD_LIVE=1</c> (host/port overridable via
/// <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>).
/// </summary>
public class CounterListenerTests
{
    private static byte[] Bytes(ArrayBufferWriter<byte> w) => w.WrittenMemory.ToArray();

    private static byte[] Long(long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return b;
    }

    // -- Add/remove listener request bodies ---------------------------------

    [Fact]
    public void Add_listener_body_is_counter_name_then_listener_id()
    {
        byte[] listenerId = [1, 2, 3, 4];

        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c1");
        HotRodCodec.WriteArray(w, listenerId);

        var expected = new List<byte> { 0x02, (byte)'c', (byte)'1', 0x04, 1, 2, 3, 4 };
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public void Remove_listener_body_is_counter_name_then_listener_id()
    {
        byte[] listenerId = [9, 9];

        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c");
        HotRodCodec.WriteArray(w, listenerId);

        var expected = new List<byte> { 0x01, (byte)'c', 0x02, 9, 9 };
        Assert.Equal(expected, Bytes(w));
    }

    // -- Counter event body ---------------------------------------------------

    private static byte[] BuildEventBody(string counterName, byte[] listenerId, byte encodedState, long oldValue, long newValue)
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteByte(w, 0);  // status: success
        HotRodCodec.WriteByte(w, 0); // topology marker: none
        CounterCodec.WriteCounterName(w, counterName);
        HotRodCodec.WriteArray(w, listenerId);
        HotRodCodec.WriteByte(w, encodedState);
        HotRodCodec.WriteLong(w, oldValue);
        HotRodCodec.WriteLong(w, newValue);
        return Bytes(w);
    }

    [Fact]
    public async Task Counter_event_decodes_name_listener_id_state_and_values()
    {
        byte[] listenerId = [5, 6, 7];
        byte[] body = BuildEventBody("c1", listenerId, encodedState: 0x00, oldValue: 10, newValue: 11);

        RawCounterEvent ev = await CounterCodec.ReadCounterEventAsync(TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal("c1", ev.CounterName);
        Assert.Equal(listenerId, ev.ListenerId);
        Assert.Equal(10, ev.OldValue);
        Assert.Equal(11, ev.NewValue);
        Assert.Equal(CounterEventState.Valid, ev.OldState);
        Assert.Equal(CounterEventState.Valid, ev.NewState);
    }

    [Theory]
    [InlineData(0x00, CounterEventState.Valid, CounterEventState.Valid)]
    [InlineData(0x01, CounterEventState.LowerBoundReached, CounterEventState.Valid)]
    [InlineData(0x02, CounterEventState.UpperBoundReached, CounterEventState.Valid)]
    [InlineData(0x04, CounterEventState.Valid, CounterEventState.LowerBoundReached)]
    [InlineData(0x08, CounterEventState.Valid, CounterEventState.UpperBoundReached)]
    [InlineData(0x09, CounterEventState.LowerBoundReached, CounterEventState.UpperBoundReached)]
    public async Task Encoded_state_byte_packs_old_state_in_low_bits_and_new_state_in_next_two(
        byte encoded, CounterEventState expectedOld, CounterEventState expectedNew)
    {
        byte[] body = BuildEventBody("c", [], encoded, oldValue: 0, newValue: 0);

        RawCounterEvent ev = await CounterCodec.ReadCounterEventAsync(TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal(expectedOld, ev.OldState);
        Assert.Equal(expectedNew, ev.NewState);
    }

    [Fact]
    public async Task Negative_values_round_trip_through_the_event_body()
    {
        byte[] body = BuildEventBody("c", [1], encodedState: 0x00, oldValue: -5, newValue: -1);

        RawCounterEvent ev = await CounterCodec.ReadCounterEventAsync(TestPipe.Reader(body), CancellationToken.None);

        Assert.Equal(-5, ev.OldValue);
        Assert.Equal(-1, ev.NewValue);
    }

    // -- Live end-to-end (opt-in) ------------------------------------------

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("HOTROD_LIVE") == "1";

    private static async Task<HotRodClient> ConnectLiveAsync()
    {
        string host = Environment.GetEnvironmentVariable("HOTROD_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("HOTROD_PORT"), out int p) ? p : 11222;
        return await HotRodClient.ConnectAsync(host, port);
    }

    [Fact]
    public async Task Live_strong_counter_listener_receives_add_and_get_events()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string name = "counter-listener-" + Guid.NewGuid().ToString("N");

        StrongCounter counter = await client.Counters.GetOrCreateStrongAsync(name, CounterConfiguration.Strong(initialValue: 0));
        try
        {
            var received = new List<CounterChangeEvent>();
            var gotOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using CounterListener listener = await counter.AddListenerAsync(ev =>
            {
                lock (received) { received.Add(ev); }
                gotOne.TrySetResult();
                return ValueTask.CompletedTask;
            });

            await counter.IncrementAsync();
            await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (received)
            {
                Assert.Contains(received, ev => ev.OldValue == 0 && ev.NewValue == 1);
            }
        }
        finally
        {
            await counter.RemoveAsync();
        }
    }

    [Fact]
    public async Task Live_weak_counter_listener_receives_add_events()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string name = "weak-counter-listener-" + Guid.NewGuid().ToString("N");

        WeakCounter counter = await client.Counters.GetOrCreateWeakAsync(name, CounterConfiguration.Weak(initialValue: 0));
        try
        {
            var gotOne = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await using CounterListener listener = await counter.AddListenerAsync(_ =>
            {
                gotOne.TrySetResult();
                return ValueTask.CompletedTask;
            });

            await counter.IncrementAsync();
            await gotOne.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await counter.RemoveAsync();
        }
    }
}
