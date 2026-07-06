using System.Buffers;
using System.Text;
using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Pins the server-task (EXEC) wire format shared by script execution and cache administration: a
/// request is the task name, a vInt parameter count, then that many length-prefixed name/value pairs;
/// a response is the task's raw result as a single length-prefixed byte array. The layout tests run
/// without a server; the <c>Live_</c> tests exercise a real server end-to-end and are skipped unless
/// <c>HOTROD_LIVE=1</c> (host/port overridable via <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>).
/// </summary>
public class ScriptExecTests
{
    private static byte[] Bytes(ArrayBufferWriter<byte> w) => w.WrittenMemory.ToArray();

    [Fact]
    public void Execute_request_is_task_name_then_zero_count_when_no_parameters()
    {
        var w = new ArrayBufferWriter<byte>();
        AdminCodec.WriteExecuteRequest(w, "t", new Dictionary<string, byte[]>());

        // "t" as a length-prefixed string, then a zero parameter count.
        Assert.Equal(new byte[] { 0x01, (byte)'t', 0x00 }, Bytes(w));
    }

    [Fact]
    public void Execute_request_is_task_name_count_then_length_prefixed_name_value_pairs()
    {
        var w = new ArrayBufferWriter<byte>();
        AdminCodec.WriteExecuteRequest(w, "task", new Dictionary<string, byte[]>
        {
            ["k"] = [0xAA, 0xBB],
        });

        var expected = new List<byte>
        {
            0x04, (byte)'t', (byte)'a', (byte)'s', (byte)'k', // task name
            0x01,                                             // one parameter
            0x01, (byte)'k',                                  // parameter name "k"
            0x02, 0xAA, 0xBB,                                 // parameter value (length-prefixed)
        };
        Assert.Equal(expected, Bytes(w));
    }

    [Fact]
    public async Task Execute_request_round_trips_all_parameters()
    {
        var parameters = new Dictionary<string, byte[]>
        {
            ["name"] = Encoding.UTF8.GetBytes("mycache"),
            ["template"] = Encoding.UTF8.GetBytes("org.infinispan.DIST_SYNC"),
            ["flags"] = Encoding.UTF8.GetBytes("VOLATILE"),
        };

        var w = new ArrayBufferWriter<byte>();
        AdminCodec.WriteExecuteRequest(w, "@@cache@create", parameters);

        (string taskName, Dictionary<string, byte[]> read) = await DecodeExecuteRequestAsync(Bytes(w));

        Assert.Equal("@@cache@create", taskName);
        Assert.Equal(parameters.Count, read.Count);
        foreach (KeyValuePair<string, byte[]> entry in parameters)
            Assert.Equal(entry.Value, read[entry.Key]);
    }

    [Fact]
    public async Task Execute_response_is_a_length_prefixed_byte_array()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(w, [0x01, 0x02, 0x03]);

        byte[] result = await AdminCodec.ReadExecuteResponseAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result);
    }

    [Fact]
    public async Task Empty_execute_response_reads_as_empty()
    {
        var w = new ArrayBufferWriter<byte>();
        HotRodCodec.WriteArray(w, []);

        byte[] result = await AdminCodec.ReadExecuteResponseAsync(TestPipe.Reader(w), CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>Reads back a request body written by <see cref="AdminCodec.WriteExecuteRequest"/>.</summary>
    private static async Task<(string, Dictionary<string, byte[]>)> DecodeExecuteRequestAsync(byte[] body)
    {
        var reader = TestPipe.Reader(body);
        string taskName = await HotRodCodec.ReadStringAsync(reader, CancellationToken.None);
        int count = await HotRodCodec.ReadVIntAsync(reader, CancellationToken.None);
        var map = new Dictionary<string, byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            string key = await HotRodCodec.ReadStringAsync(reader, CancellationToken.None);
            map[key] = await HotRodCodec.ReadArrayAsync(reader, CancellationToken.None);
        }
        return (taskName, map);
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
    public async Task Live_execute_runs_a_deployed_server_task()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server with a task named by HOTROD_TASK

        string? taskName = Environment.GetEnvironmentVariable("HOTROD_TASK");
        if (string.IsNullOrEmpty(taskName)) return; // no task deployed to run against

        await using HotRodClient client = await ConnectLiveAsync();
        byte[] result = await client.ExecuteAsync(taskName);
        Assert.NotNull(result);
    }
}
