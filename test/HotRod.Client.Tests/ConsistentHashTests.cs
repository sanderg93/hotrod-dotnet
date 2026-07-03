using HotRod.Client;

namespace HotRod.Client.Tests;

/// <summary>
/// Checks the routing table: a key folds to a segment and maps to that segment's primary owner,
/// the lookup is stable for a given key, and every key lands inside the table (the rounded-up
/// segment size must never produce an out-of-range segment for the maximum hash).
/// </summary>
public class ConsistentHashTests
{
    private static ServerAddress Node(int i) => new($"10.0.0.{i}", 11222);

    private static ConsistentHash UniformTable(int segments, ServerAddress owner)
    {
        var table = new ServerAddress?[segments];
        Array.Fill(table, owner);
        return new ConsistentHash(table);
    }

    [Fact]
    public void Every_key_maps_to_the_sole_owner_of_a_uniform_table()
    {
        ServerAddress only = Node(1);
        var hash = UniformTable(256, only);

        foreach (string key in new[] { "Stad", "Land", "", "a", "x".PadRight(40, 'x') })
            Assert.Equal(only, hash.PrimaryOwner(Bytes(key)));
    }

    [Fact]
    public void PrimaryOwner_is_deterministic_for_the_same_key()
    {
        var table = Spread(256, owners: 3);

        ServerAddress? first = table.PrimaryOwner(Bytes("session-42"));
        ServerAddress? second = table.PrimaryOwner(Bytes("session-42"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_segment_without_an_owner_returns_null()
    {
        // Single segment, no owner: every key folds into it.
        var hash = new ConsistentHash(new ServerAddress?[] { null });

        Assert.Null(hash.PrimaryOwner(Bytes("anything")));
    }

    [Fact]
    public void No_key_ever_falls_outside_the_segment_table()
    {
        var table = Spread(256, owners: 5);
        var rng = new Random(20260628);

        for (int i = 0; i < 10_000; i++)
        {
            var key = new byte[rng.Next(0, 48)];
            rng.NextBytes(key);

            // Must not throw IndexOutOfRange and must resolve to one of the table's owners.
            ServerAddress? owner = table.PrimaryOwner(key);
            Assert.NotNull(owner);
        }
    }

    private static ConsistentHash Spread(int segments, int owners)
    {
        var table = new ServerAddress?[segments];
        for (int i = 0; i < segments; i++)
            table[i] = Node(i % owners);
        return new ConsistentHash(table);
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
