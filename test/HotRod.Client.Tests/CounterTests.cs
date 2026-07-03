using System.Buffers;
using System.Buffers.Binary;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the counter wire format against Infinispan's encoding: the counter name that prefixes a request
/// body, and the counter configuration exchanged by create/get-configuration (a flags byte packing type
/// and storage, then the type-specific fields, then the initial value). The request-layout tests run
/// without a server; the <c>Live_</c> tests exercise a real server end-to-end and are skipped unless
/// <c>HOTROD_LIVE=1</c> (host/port overridable via <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>).
/// </summary>
public class CounterTests
{
    private static byte[] Bytes(ArrayBufferWriter<byte> w) => w.WrittenMemory.ToArray();

    private static byte[] Long(long value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, value);
        return b;
    }

    // -- Counter name -------------------------------------------------------

    [Fact]
    public void Counter_name_is_a_length_prefixed_utf8_string()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c1");

        Assert.Equal(new byte[] { 0x02, (byte)'c', (byte)'1' }, Bytes(w));
    }

    // -- Configuration flags byte ------------------------------------------

    [Theory]
    [InlineData(CounterType.UnboundedStrong, CounterStorage.Volatile, 0x00)]
    [InlineData(CounterType.UnboundedStrong, CounterStorage.Persistent, 0x04)]
    [InlineData(CounterType.BoundedStrong, CounterStorage.Volatile, 0x02)]
    [InlineData(CounterType.BoundedStrong, CounterStorage.Persistent, 0x06)]
    [InlineData(CounterType.Weak, CounterStorage.Volatile, 0x01)]
    [InlineData(CounterType.Weak, CounterStorage.Persistent, 0x05)]
    public void Flags_byte_packs_type_in_low_bits_and_storage_in_bit_two(CounterType type, CounterStorage storage, int expected)
    {
        CounterConfiguration config = type switch
        {
            CounterType.Weak => CounterConfiguration.Weak(storage: storage),
            CounterType.BoundedStrong => CounterConfiguration.BoundedStrong(0, 10, storage: storage),
            _ => CounterConfiguration.Strong(storage: storage),
        };

        Assert.Equal((byte)expected, CounterCodec.EncodeFlags(config));
    }

    // -- Configuration body layout -----------------------------------------

    [Fact]
    public void Unbounded_strong_config_is_flags_then_initial_value()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteConfiguration(w, CounterConfiguration.Strong(initialValue: 5));

        var expected = new List<byte> { 0x00 };
        expected.AddRange(Long(5));
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public void Bounded_strong_config_is_flags_then_lower_upper_then_initial_value()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteConfiguration(w, CounterConfiguration.BoundedStrong(lowerBound: -3, upperBound: 9, initialValue: 1));

        var expected = new List<byte> { 0x02 };
        expected.AddRange(Long(-3));
        expected.AddRange(Long(9));
        expected.AddRange(Long(1));
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public void Weak_config_is_flags_then_concurrency_vint_then_initial_value()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteConfiguration(w, CounterConfiguration.Weak(initialValue: 7, concurrencyLevel: 4));

        var expected = new List<byte> { 0x01, 0x04 }; // flags, concurrency=4 (single-byte vInt)
        expected.AddRange(Long(7));
        Assert.Equal(expected, Bytes(w));
    }

    // -- Configuration round-trips -----------------------------------------

    [Fact]
    public async Task Unbounded_strong_config_round_trips()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterConfiguration original = CounterConfiguration.Strong(initialValue: 42, storage: CounterStorage.Persistent);
        CounterCodec.WriteConfiguration(w, original);

        CounterConfiguration read = await CounterCodec.ReadConfigurationAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(CounterType.UnboundedStrong, read.Type);
        Assert.Equal(42, read.InitialValue);
        Assert.Equal(CounterStorage.Persistent, read.Storage);
    }

    [Fact]
    public async Task Bounded_strong_config_round_trips()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteConfiguration(w, CounterConfiguration.BoundedStrong(lowerBound: -100, upperBound: 100, initialValue: -5));

        CounterConfiguration read = await CounterCodec.ReadConfigurationAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(CounterType.BoundedStrong, read.Type);
        Assert.Equal(-100, read.LowerBound);
        Assert.Equal(100, read.UpperBound);
        Assert.Equal(-5, read.InitialValue);
    }

    [Fact]
    public async Task Weak_config_round_trips()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteConfiguration(w, CounterConfiguration.Weak(initialValue: 3, concurrencyLevel: 32));

        CounterConfiguration read = await CounterCodec.ReadConfigurationAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(CounterType.Weak, read.Type);
        Assert.Equal(32, read.ConcurrencyLevel);
        Assert.Equal(3, read.InitialValue);
    }

    // -- Full request bodies -----------------------------------------------

    [Fact]
    public void Add_and_get_body_is_counter_name_then_8_byte_delta()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c");
        HotRodCodec.WriteLong(w, -2);

        var expected = new List<byte> { 0x01, (byte)'c' };
        expected.AddRange(Long(-2));
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public void Compare_and_swap_body_is_counter_name_then_expect_then_update()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c");
        HotRodCodec.WriteLong(w, 4);   // expect
        HotRodCodec.WriteLong(w, 11);  // update

        var expected = new List<byte> { 0x01, (byte)'c' };
        expected.AddRange(Long(4));
        expected.AddRange(Long(11));
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public void Create_body_is_counter_name_then_configuration()
    {
        var w = new ArrayBufferWriter<byte>();
        CounterCodec.WriteCounterName(w, "c");
        CounterCodec.WriteConfiguration(w, CounterConfiguration.Strong(initialValue: 0));

        var expected = new List<byte> { 0x01, (byte)'c', 0x00 };
        expected.AddRange(Long(0));
        Assert.Equal(expected, Bytes(w));
    }

    // -- Configuration validation ------------------------------------------

    [Fact]
    public void Bounded_strong_rejects_an_initial_value_outside_the_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CounterConfiguration.BoundedStrong(0, 10, initialValue: 20));
    }

    [Fact]
    public void Bounded_strong_rejects_a_lower_bound_above_the_upper_bound()
    {
        Assert.Throws<ArgumentException>(() => CounterConfiguration.BoundedStrong(10, 0));
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
    public async Task Live_strong_counter_lifecycle()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string name = "strong-" + Guid.NewGuid().ToString("N");
        CounterManager counters = client.Counters;

        StrongCounter counter = await counters.GetOrCreateStrongAsync(name, CounterConfiguration.Strong(initialValue: 10));
        try
        {
            Assert.True(await counters.IsDefinedAsync(name));
            Assert.Equal(10, await counter.GetValueAsync());
            Assert.Equal(13, await counter.AddAndGetAsync(3));
            Assert.Equal(14, await counter.IncrementAsync());

            Assert.True(await counter.CompareAndSetAsync(14, 100));  // matches -> swaps
            Assert.False(await counter.CompareAndSetAsync(14, 200)); // stale -> no-op
            Assert.Equal(100, await counter.GetValueAsync());

            await counter.ResetAsync();
            Assert.Equal(10, await counter.GetValueAsync());

            Assert.Contains(name, await counters.GetCounterNamesAsync());

            CounterConfiguration? config = await counters.GetConfigurationAsync(name);
            Assert.NotNull(config);
            Assert.Equal(CounterType.UnboundedStrong, config!.Type);
            Assert.Equal(10, config.InitialValue);
        }
        finally
        {
            await counter.RemoveAsync();
        }

        // Removing a strong counter clears its value (back to the initial value) but leaves the counter
        // defined cluster-wide — its configuration is retained, so it is still reported as defined.
        Assert.True(await counters.IsDefinedAsync(name));
        Assert.Equal(10, await (await counters.GetStrongCounterAsync(name)).GetValueAsync());
    }

    [Fact]
    public async Task Live_bounded_strong_counter_reports_out_of_bounds()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string name = "bounded-" + Guid.NewGuid().ToString("N");

        StrongCounter counter = await client.Counters.GetOrCreateStrongAsync(
            name, CounterConfiguration.BoundedStrong(lowerBound: 0, upperBound: 5, initialValue: 0));
        try
        {
            Assert.Equal(5, await counter.AddAndGetAsync(5));
            await Assert.ThrowsAsync<CounterOutOfBoundsException>(async () => await counter.AddAndGetAsync(1));
        }
        finally
        {
            await counter.RemoveAsync();
        }
    }

    [Fact]
    public async Task Live_weak_counter_lifecycle()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        string name = "weak-" + Guid.NewGuid().ToString("N");

        WeakCounter counter = await client.Counters.GetOrCreateWeakAsync(name, CounterConfiguration.Weak(initialValue: 0));
        try
        {
            await counter.AddAsync(7);
            await counter.IncrementAsync();
            Assert.Equal(8, await counter.GetValueAsync());
            Assert.Equal(CounterType.Weak, counter.Configuration.Type);
        }
        finally
        {
            await counter.RemoveAsync();
        }
    }

    [Fact]
    public async Task Live_get_value_on_an_undefined_counter_throws()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        await Assert.ThrowsAsync<UndefinedCounterException>(
            async () => await client.Counters.GetStrongCounterAsync("missing-" + Guid.NewGuid().ToString("N")));
    }
}
