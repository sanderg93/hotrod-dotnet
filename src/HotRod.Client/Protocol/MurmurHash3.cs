namespace HotRod.Client.Protocol;

/// <summary>
/// A byte-for-byte port of Infinispan's <c>org.infinispan.commons.hash.MurmurHash3</c> — its own
/// "bmix" variant of MurmurHash3 x64, which differs from the canonical algorithm (mutating
/// constants, 23/41-bit rotations, <c>h*3</c> mixing, only <c>h2 ^= length</c>) and which sign-
/// extends tail bytes the way Java's signed <c>byte</c> does. The server uses exactly this to place
/// keys, so the client must reproduce it to compute the right segment. The public result is
/// <c>MurmurHash3_x64_32</c>: the high 32 bits of the first 64-bit lane.
/// </summary>
internal static class MurmurHash3
{
    private const ulong Seed = 9001; // Infinispan's fixed seed for hash(byte[])

    public static int Hash(byte[] data) => (int)(Hash128First(data) >> 32);

    /// <summary>Returns the first 64-bit lane (h1) of Infinispan's x64-128 hash.</summary>
    private static ulong Hash128First(byte[] key)
    {
        ulong h1 = 0x9368e53c2f6af274UL ^ Seed;
        ulong h2 = 0x586dcd208f7cd3fdUL ^ Seed;
        ulong c1 = 0x87c37b91114253d5UL;
        ulong c2 = 0x4cf5ad432745937fUL;

        int length = key.Length;
        int blocks = length / 16;

        for (int i = 0; i < blocks; i++)
        {
            ulong k1 = ReadUInt64LE(key, i * 16);
            ulong k2 = ReadUInt64LE(key, i * 16 + 8);
            Bmix(ref h1, ref h2, ref c1, ref c2, k1, k2);
        }

        ulong tk1 = 0;
        ulong tk2 = 0;
        int tail = (length >> 4) << 4;
        int rem = length & 15;

        if (rem >= 15) tk2 ^= Sx(key[tail + 14]) << 48;
        if (rem >= 14) tk2 ^= Sx(key[tail + 13]) << 40;
        if (rem >= 13) tk2 ^= Sx(key[tail + 12]) << 32;
        if (rem >= 12) tk2 ^= Sx(key[tail + 11]) << 24;
        if (rem >= 11) tk2 ^= Sx(key[tail + 10]) << 16;
        if (rem >= 10) tk2 ^= Sx(key[tail + 9]) << 8;
        if (rem >= 9) tk2 ^= Sx(key[tail + 8]);
        if (rem >= 8) tk1 ^= Sx(key[tail + 7]) << 56;
        if (rem >= 7) tk1 ^= Sx(key[tail + 6]) << 48;
        if (rem >= 6) tk1 ^= Sx(key[tail + 5]) << 40;
        if (rem >= 5) tk1 ^= Sx(key[tail + 4]) << 32;
        if (rem >= 4) tk1 ^= Sx(key[tail + 3]) << 24;
        if (rem >= 3) tk1 ^= Sx(key[tail + 2]) << 16;
        if (rem >= 2) tk1 ^= Sx(key[tail + 1]) << 8;
        if (rem >= 1)
        {
            tk1 ^= Sx(key[tail]);
            Bmix(ref h1, ref h2, ref c1, ref c2, tk1, tk2);
        }

        h2 ^= (ulong)(uint)length;

        h1 += h2;
        h2 += h1;
        h1 = FMix(h1);
        h2 = FMix(h2);
        h1 += h2;
        // h2 += h1 here would be a no-op for the value we return (h1).
        return h1;
    }

    private static void Bmix(ref ulong h1, ref ulong h2, ref ulong c1, ref ulong c2, ulong k1, ulong k2)
    {
        k1 *= c1; k1 = RotL(k1, 23); k1 *= c2; h1 ^= k1; h1 += h2;
        h2 = RotL(h2, 41);
        k2 *= c2; k2 = RotL(k2, 23); k2 *= c1; h2 ^= k2; h2 += h1;
        h1 = h1 * 3 + 0x52dce729; h2 = h2 * 3 + 0x38495ab5;
        c1 = c1 * 5 + 0x7b7d159c; c2 = c2 * 5 + 0x6bce6396;
    }

    private static ulong FMix(ulong k)
    {
        k ^= k >> 33;
        k *= 0xff51afd7ed558ccdUL;
        k ^= k >> 33;
        k *= 0xc4ceb9fe1a85ec53UL;
        k ^= k >> 33;
        return k;
    }

    private static ulong RotL(ulong x, int r) => (x << r) | (x >> (64 - r));

    /// <summary>Sign-extends a byte to 64 bits, matching Java's promotion of a signed <c>byte</c>.</summary>
    private static ulong Sx(byte b) => (ulong)(sbyte)b;

    private static ulong ReadUInt64LE(byte[] data, int offset) =>
        (ulong)data[offset]
        | (ulong)data[offset + 1] << 8
        | (ulong)data[offset + 2] << 16
        | (ulong)data[offset + 3] << 24
        | (ulong)data[offset + 4] << 32
        | (ulong)data[offset + 5] << 40
        | (ulong)data[offset + 6] << 48
        | (ulong)data[offset + 7] << 56;
}
