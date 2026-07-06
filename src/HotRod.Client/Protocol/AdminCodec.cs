using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace HotRod.Client.Protocol;

/// <summary>
/// Encodes and decodes the server-task (EXEC) wire format shared by script execution and cache
/// administration. A request body is the task name followed by a vInt parameter count and that many
/// name/value pairs (each a length-prefixed UTF-8 name and a length-prefixed raw byte value). The
/// response body is a single length-prefixed byte array carrying the task's raw result. Built on
/// <see cref="HotRodCodec"/> for the primitive fields.
/// </summary>
internal static class AdminCodec
{
    /// <summary>Writes an EXEC request body: the task name, the parameter count, then each name/value pair.</summary>
    public static void WriteExecuteRequest(IBufferWriter<byte> writer, string taskName, IReadOnlyDictionary<string, byte[]> parameters)
    {
        HotRodCodec.WriteArray(writer, Encoding.UTF8.GetBytes(taskName));
        HotRodCodec.WriteVInt(writer, parameters.Count);
        foreach (KeyValuePair<string, byte[]> parameter in parameters)
        {
            HotRodCodec.WriteArray(writer, Encoding.UTF8.GetBytes(parameter.Key));
            HotRodCodec.WriteArray(writer, parameter.Value);
        }
    }

    /// <summary>Reads an EXEC response body: the task's raw result as a single length-prefixed byte array.</summary>
    public static ValueTask<byte[]> ReadExecuteResponseAsync(PipeReader reader, CancellationToken ct) =>
        HotRodCodec.ReadArrayAsync(reader, ct);

    /// <summary>Encodes a task parameter as its raw UTF-8 bytes, the form the admin server tasks expect.</summary>
    public static byte[] StringParameter(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>
    /// Serializes admin flags to the comma-separated upper-case token list the server parses, or null
    /// when no flags are set so the caller can omit the parameter entirely.
    /// </summary>
    public static string? EncodeFlags(AdminFlags flags)
    {
        if (flags == AdminFlags.None)
            return null;

        var tokens = new List<string>(2);
        if (flags.HasFlag(AdminFlags.Volatile))
            tokens.Add("VOLATILE");
        if (flags.HasFlag(AdminFlags.Update))
            tokens.Add("UPDATE");
        return string.Join(",", tokens);
    }

    /// <summary>
    /// Parses the result of the "@@cache@names" task: a JSON array of strings, e.g. <c>["a","b"]</c>.
    /// An empty body yields an empty collection.
    /// </summary>
    public static IReadOnlyCollection<string> ParseCacheNames(byte[] json)
    {
        if (json.Length == 0)
            return [];

        var names = new List<string>();
        using JsonDocument document = JsonDocument.Parse(json);
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.GetString() is { } name)
                names.Add(name);
        }
        return names;
    }
}
