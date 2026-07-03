using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Locks down Infinispan's custom MurmurHash3 variant. The hash must be deterministic (the server
/// places keys with the same function), spread distinct keys apart, and keep producing the exact
/// values it was verified to produce against a live cluster — a regression net against an accidental
/// edit to the byte-for-byte port.
/// </summary>
public class MurmurHash3Tests
{
    [Theory]
    [InlineData("Stad")]
    [InlineData("Land")]
    [InlineData("session-42")]
    [InlineData("")]
    public void Hash_is_deterministic(string key)
    {
        Assert.Equal(MurmurHash3.Hash(Bytes(key)), MurmurHash3.Hash(Bytes(key)));
    }

    [Fact]
    public void Distinct_keys_hash_to_distinct_values()
    {
        int[] hashes = new[] { "a", "b", "c", "Stad", "Land", "Amsterdam" }
            .Select(k => MurmurHash3.Hash(Bytes(k)))
            .ToArray();

        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    // Captured from the byte-for-byte port that agreed 40/40 with the server's own
    // ?action=distribution view; a change here means the hash no longer matches the server.
    [Theory]
    [InlineData("Stad", 555111633)]
    [InlineData("Land", 899187794)]
    [InlineData("session-42", -1592095108)]
    [InlineData("Amsterdam", -1228527410)]
    [InlineData("", 89125410)]
    public void Hash_matches_the_values_verified_against_a_live_cluster(string key, int expected)
    {
        Assert.Equal(expected, MurmurHash3.Hash(Bytes(key)));
    }

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
