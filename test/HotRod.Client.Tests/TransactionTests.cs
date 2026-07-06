using HotRod.Client.Protocol;

namespace HotRod.Client.Tests;

/// <summary>
/// Exercises <see cref="HotRodTransaction"/>'s client-side buffering and state machine without a server
/// (a transaction touches the connection only at commit, and only when it has writes to stage), plus the
/// mapping from buffered operations to the PREPARE_TX modification set. The <c>Live_</c> tests drive a
/// real transactional cache end-to-end and are skipped unless <c>HOTROD_LIVE=1</c> (host/port via
/// <c>HOTROD_HOST</c>/<c>HOTROD_PORT</c>, cache via <c>HOTROD_TX_CACHE</c>, default <c>transactional</c>).
/// </summary>
public class TransactionTests
{
    // A transaction reaches the connection only at commit-with-writes, so an offline transaction over a
    // cache with a null client can drive every buffering path. The Raw encoding keeps keys/values as UTF-8.
    private static HotRodTransaction OfflineTransaction() =>
        new TransactionManager(null!).Begin(new RemoteCache(null!, "cache", CacheEncoding.Raw));

    // -- XID generation -----------------------------------------------------

    [Fact]
    public void A_generated_xid_uses_the_remote_format_id_and_32_byte_fields()
    {
        TransactionXid xid = OfflineTransaction().Xid;

        Assert.Equal(TransactionManager.RemoteFormatId, xid.FormatId);
        Assert.Equal(32, xid.GlobalId.Length);
        Assert.Equal(32, xid.BranchQualifier.Length);
    }

    [Fact]
    public void Each_transaction_gets_a_distinct_xid()
    {
        var manager = new TransactionManager(null!);
        var cache = new RemoteCache(null!, "cache", CacheEncoding.Raw);

        TransactionXid first = manager.Begin(cache).Xid;
        TransactionXid second = manager.Begin(cache).Xid;

        Assert.NotEqual(first.GlobalId, second.GlobalId);
        Assert.NotEqual(first.BranchQualifier, second.BranchQualifier);
    }

    // -- Read-your-writes ---------------------------------------------------

    [Fact]
    public async Task A_read_sees_a_value_written_earlier_in_the_transaction()
    {
        await using HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");

        Assert.Equal("v", await tx.GetAsync("k"));
        Assert.True(await tx.ContainsKeyAsync("k"));
    }

    [Fact]
    public async Task A_read_sees_a_key_removed_earlier_in_the_transaction_as_absent()
    {
        await using HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");
        await tx.RemoveAsync("k");

        Assert.Null(await tx.GetAsync("k"));
        Assert.False(await tx.ContainsKeyAsync("k"));
    }

    [Fact]
    public async Task Byte_array_writes_round_trip_within_the_transaction()
    {
        byte[] key = [1, 2, 3];
        byte[] value = [9, 8, 7];
        await using HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync(key, value);

        Assert.Equal(value, await tx.GetAsync(key));
    }

    // -- Modification set construction --------------------------------------

    [Fact]
    public async Task A_blind_put_becomes_a_single_not_read_modification()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");

        List<TransactionModification> modifications = tx.BuildModifications();

        TransactionModification only = Assert.Single(modifications);
        Assert.Equal(TransactionCodec.ControlNotRead, only.Control);
        Assert.Equal("v"u8.ToArray(), only.Value);
        Assert.Equal("k"u8.ToArray(), only.Key);
    }

    [Fact]
    public async Task A_blind_remove_becomes_a_not_read_remove_modification_with_no_value()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.RemoveAsync("k");

        TransactionModification only = Assert.Single(tx.BuildModifications());
        Assert.Equal((byte)(TransactionCodec.ControlNotRead | TransactionCodec.ControlRemoveOp), only.Control);
        Assert.Null(only.Value);
    }

    [Fact]
    public async Task Writing_a_key_then_removing_it_yields_one_remove_modification()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");
        await tx.RemoveAsync("k");

        TransactionModification only = Assert.Single(tx.BuildModifications());
        Assert.Equal((byte)(TransactionCodec.ControlNotRead | TransactionCodec.ControlRemoveOp), only.Control);
        Assert.Null(only.Value);
    }

    [Fact]
    public async Task Overwriting_a_key_yields_one_modification_with_the_latest_value()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "first");
        await tx.PutAsync("k", "second");

        TransactionModification only = Assert.Single(tx.BuildModifications());
        Assert.Equal("second"u8.ToArray(), only.Value);
    }

    // -- State machine ------------------------------------------------------

    [Fact]
    public async Task A_read_only_transaction_commits_without_contacting_the_server()
    {
        await using HotRodTransaction tx = OfflineTransaction();

        // No writes were buffered, so commit stages nothing and never dereferences the (null) connection.
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.PutAsync("k", "v"));
    }

    [Fact]
    public async Task Rolling_back_discards_buffered_writes_and_ends_the_transaction()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");

        await tx.RollbackAsync();

        Assert.Empty(tx.BuildModifications());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.GetAsync("k"));
    }

    [Fact]
    public async Task Disposing_an_active_transaction_rolls_it_back()
    {
        HotRodTransaction tx = OfflineTransaction();
        await tx.PutAsync("k", "v");

        await tx.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync());
    }

    [Fact]
    public async Task A_committed_transaction_cannot_be_committed_again()
    {
        await using HotRodTransaction tx = OfflineTransaction();
        await tx.CommitAsync(); // read-only

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.CommitAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.RollbackAsync());
    }

    // -- Live (opt-in) ------------------------------------------------------

    private static bool LiveEnabled => Environment.GetEnvironmentVariable("HOTROD_LIVE") == "1";

    private static async Task<HotRodClient> ConnectLiveAsync()
    {
        string host = Environment.GetEnvironmentVariable("HOTROD_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("HOTROD_PORT"), out int p) ? p : 11222;
        string? user = Environment.GetEnvironmentVariable("HOTROD_USER");
        string? pass = Environment.GetEnvironmentVariable("HOTROD_PASS");
        return await HotRodClient.ConnectAsync(host, port, user, pass);
    }

    private static string TxCacheName => Environment.GetEnvironmentVariable("HOTROD_TX_CACHE") ?? "transactional";

    [Fact]
    public async Task Live_commit_applies_all_writes_atomically()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server with a transactional cache (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        RemoteCache cache = client.GetCache(TxCacheName, CacheEncoding.Raw);
        string k1 = "tx-" + Guid.NewGuid().ToString("N");
        string k2 = "tx-" + Guid.NewGuid().ToString("N");

        await using (HotRodTransaction tx = cache.BeginTransaction())
        {
            await tx.PutAsync(k1, "one");
            await tx.PutAsync(k2, "two");
            await tx.CommitAsync();
        }

        Assert.Equal("one", await cache.GetAsync(k1));
        Assert.Equal("two", await cache.GetAsync(k2));

        await cache.RemoveAsync(k1);
        await cache.RemoveAsync(k2);
    }

    [Fact]
    public async Task Live_rollback_discards_writes()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server with a transactional cache (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        RemoteCache cache = client.GetCache(TxCacheName, CacheEncoding.Raw);
        string key = "tx-" + Guid.NewGuid().ToString("N");

        await using (HotRodTransaction tx = cache.BeginTransaction())
        {
            await tx.PutAsync(key, "value");
            await tx.RollbackAsync();
        }

        Assert.Null(await cache.GetAsync(key));
    }

    [Fact]
    public async Task Live_a_read_sees_the_committed_value_and_its_own_writes()
    {
        if (!LiveEnabled) return; // opt-in: requires a live server with a transactional cache (set HOTROD_LIVE=1)

        await using HotRodClient client = await ConnectLiveAsync();
        RemoteCache cache = client.GetCache(TxCacheName, CacheEncoding.Raw);
        string key = "tx-" + Guid.NewGuid().ToString("N");
        await cache.PutAsync(key, "committed");

        try
        {
            await using HotRodTransaction tx = cache.BeginTransaction();
            Assert.Equal("committed", await tx.GetAsync(key)); // reads through to the server
            await tx.PutAsync(key, "updated");
            Assert.Equal("updated", await tx.GetAsync(key));    // then sees its own write
            await tx.CommitAsync();

            Assert.Equal("updated", await cache.GetAsync(key));
        }
        finally
        {
            await cache.RemoveAsync(key);
        }
    }
}
