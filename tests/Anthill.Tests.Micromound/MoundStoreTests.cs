using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// One set of behaviours, run against every <see cref="IMoundStore"/> implementation.
///
/// This shape is the point. The in-memory store is what the authority tests use, and the SQLite
/// store is what actually runs — so the only thing that makes the fast tests meaningful is the
/// two agreeing. Writing separate suites would let them drift, and the drift would only surface
/// as a bug in production against behaviour a green test suite claimed to cover.
/// </summary>
public abstract class MoundStoreContract
{
    protected static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-15T09:00:00Z");

    protected abstract IMoundStore NewStore();

    private void WithStore(Action<IMoundStore> body)
    {
        using var workspace = new TempWorkspace();
        var store = NewStore();
        try
        {
            body(store);
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }

    protected static MoundRecord Sample(string moundId = "mm-1", string name = "Shed Pi") => new()
    {
        MoundId = moundId,
        Name = name,
        Tier = MoundTiers.EdgeQueen,
        PublicKey = new string('a', 64),
        HardwareProfile = "raspberry-pi-5",
        Capabilities = ["sense.temp", "act.relay_1"],
        EnrolledAt = Now.ToWire(),
        LastSeen = Now.ToWire(),
        LastSeq = 41,
        LastDigest = "sha256:deadbeef",
        SyncIntervalSeconds = 30,
        Stopped = true,
        ProtocolVersion = ProtocolVersion.Current
    };

    [Fact]
    public void A_mound_round_trips_every_field() => WithStore(store =>
    {
        store.UpsertMound(Sample());
        var read = store.GetMound("mm-1");

        Assert.NotNull(read);
        Assert.Equal("Shed Pi", read.Name);
        Assert.Equal(MoundTiers.EdgeQueen, read.Tier);
        Assert.Equal(new string('a', 64), read.PublicKey);
        Assert.Equal("raspberry-pi-5", read.HardwareProfile);
        Assert.Equal(new[] { "sense.temp", "act.relay_1" }, read.Capabilities);
        Assert.Equal(Now.ToWire(), read.EnrolledAt);
        Assert.Equal(41L, read.LastSeq);
        Assert.Equal("sha256:deadbeef", read.LastDigest);
        Assert.Equal(30, read.SyncIntervalSeconds);
        Assert.True(read.Stopped);
        Assert.Equal(ProtocolVersion.Current, read.ProtocolVersion);
    });

    [Fact]
    public void An_unknown_mound_reads_as_null() => WithStore(store =>
        Assert.Null(store.GetMound("mm-nobody")));

    [Fact]
    public void Upserting_twice_updates_rather_than_duplicates() => WithStore(store =>
    {
        store.UpsertMound(Sample());

        var moved = Sample();
        moved.LastSeq = 99;
        moved.Stopped = false;
        store.UpsertMound(moved);

        Assert.Single(store.ListMounds());
        Assert.Equal(99L, store.GetMound("mm-1")!.LastSeq);
        Assert.False(store.GetMound("mm-1")!.Stopped);
    });

    /// <remarks>
    /// Lowercase ASCII names on purpose. The in-memory store sorts with
    /// <see cref="StringComparer.Ordinal"/> and SQLite's <c>ORDER BY name</c> uses BINARY
    /// collation; those agree here but diverge on mixed case. If ordering ever needs to be
    /// case-insensitive, both sides have to change together — do not just widen this test.
    /// </remarks>
    [Fact]
    public void Mounds_list_in_name_order() => WithStore(store =>
    {
        store.UpsertMound(Sample("mm-3", "gamma"));
        store.UpsertMound(Sample("mm-1", "alpha"));
        store.UpsertMound(Sample("mm-2", "beta"));

        Assert.Equal(new[] { "alpha", "beta", "gamma" }, store.ListMounds().Select(m => m.Name));
    });

    [Fact]
    public void Removing_a_mound_takes_its_token_and_its_beats_with_it() => WithStore(store =>
    {
        store.UpsertMound(Sample());
        store.PutEnrollmentToken(new EnrollmentToken { MoundId = "mm-1", TokenHash = "abc" });
        store.RecordBeat(new MoundBeat { MoundId = "mm-1", ReceivedAt = Now.ToWire(), Accepted = true });

        Assert.True(store.RemoveMound("mm-1"));

        Assert.Null(store.GetMound("mm-1"));
        Assert.Null(store.GetEnrollmentToken("mm-1"));
        Assert.Empty(store.RecentBeats("mm-1", 10));
        Assert.False(store.RemoveMound("mm-1"));
    });

    [Fact]
    public void An_enrollment_token_round_trips_including_its_burn_mark() => WithStore(store =>
    {
        store.PutEnrollmentToken(new EnrollmentToken
        {
            MoundId = "mm-1",
            TokenHash = new string('f', 64),
            IssuedAt = Now.ToWire(),
            ExpiresAt = Now.AddMinutes(30).ToWire(),
            IssuedBy = "tyler"
        });

        var read = store.GetEnrollmentToken("mm-1")!;
        Assert.False(read.IsBurned);
        Assert.Equal("tyler", read.IssuedBy);

        read.BurnedAt = Now.AddMinutes(1).ToWire();
        store.PutEnrollmentToken(read);

        Assert.True(store.GetEnrollmentToken("mm-1")!.IsBurned);
    });

    [Fact]
    public void Beats_come_back_newest_first_and_respect_the_limit() => WithStore(store =>
    {
        for (var i = 0; i < 5; i++)
            store.RecordBeat(new MoundBeat
            {
                MoundId = "mm-1",
                ReceivedAt = Now.AddSeconds(i).ToWire(),
                Seq = i,
                State = "chartered",
                EnvelopeCount = 1,
                Accepted = true
            });

        var recent = store.RecentBeats("mm-1", 3);

        Assert.Equal(3, recent.Count);
        Assert.Equal(4L, recent[0].Seq);
        Assert.Equal(2L, recent[2].Seq);
    });

    [Fact]
    public void A_refused_beat_keeps_its_reasons() => WithStore(store =>
    {
        store.RecordBeat(new MoundBeat
        {
            MoundId = "mm-1",
            ReceivedAt = Now.ToWire(),
            Seq = -1,
            State = "refused",
            Accepted = false,
            Refusals = ["signature_refused: bad_signature", "seq does not resume"]
        });

        var beat = store.RecentBeats("mm-1", 1)[0];

        Assert.False(beat.Accepted);
        Assert.Equal(2, beat.Refusals.Count);
        Assert.Contains("bad_signature", beat.Refusals[0]);
    });

    [Fact]
    public void Beats_are_scoped_to_their_own_mound() => WithStore(store =>
    {
        store.RecordBeat(new MoundBeat { MoundId = "mm-1", ReceivedAt = Now.ToWire() });
        store.RecordBeat(new MoundBeat { MoundId = "mm-2", ReceivedAt = Now.ToWire() });

        Assert.Single(store.RecentBeats("mm-1", 10));
        Assert.Empty(store.RecentBeats("mm-3", 10));
    });

    [Fact]
    public void A_widget_payload_round_trips_and_overwrites() => WithStore(store =>
    {
        Assert.Null(store.GetWidgetPayload(MicromoundWidgetKinds.MoundFleet));

        store.PutWidgetPayload(MicromoundWidgetKinds.MoundFleet, """{"total":1}""", Now.ToWire());
        store.PutWidgetPayload(MicromoundWidgetKinds.MoundFleet, """{"total":2}""", Now.AddMinutes(1).ToWire());

        var read = store.GetWidgetPayload(MicromoundWidgetKinds.MoundFleet);

        Assert.NotNull(read);
        Assert.Equal("""{"total":2}""", read.Value.PayloadJson);
        Assert.Equal(Now.AddMinutes(1).ToWire(), read.Value.UpdatedAt);
    });
}

[Collection(MicromoundCollection.Name)]
public class InMemoryMoundStoreTests : MoundStoreContract
{
    protected override IMoundStore NewStore() => new InMemoryMoundStore();
}

[Collection(MicromoundCollection.Name)]
public class SqliteMoundStoreTests : MoundStoreContract
{
    protected override IMoundStore NewStore() => new SqliteMoundStore();

    [Fact]
    public void What_was_written_survives_the_process_that_wrote_it()
    {
        using var workspace = new TempWorkspace();

        using (var writer = new SqliteMoundStore())
        {
            writer.UpsertMound(Sample());
            writer.RecordBeat(new MoundBeat
            {
                MoundId = "mm-1", ReceivedAt = Now.ToWire(), Seq = 7, Accepted = true
            });
        }

        using var reader = new SqliteMoundStore();

        Assert.Equal(41L, reader.GetMound("mm-1")!.LastSeq);
        Assert.Equal(7L, reader.RecentBeats("mm-1", 1)[0].Seq);
    }

    [Fact]
    public void Opening_an_existing_database_again_is_safe()
    {
        using var workspace = new TempWorkspace();

        using var first = new SqliteMoundStore();
        first.UpsertMound(Sample());

        // Idempotent DDL: a second store over the same file must not throw or wipe anything.
        using var second = new SqliteMoundStore();

        Assert.NotNull(second.GetMound("mm-1"));
    }

    [Fact]
    public void Beat_history_is_a_ring_buffer_not_a_leak()
    {
        using var workspace = new TempWorkspace();
        using var store = new SqliteMoundStore();

        var overflow = SqliteMoundStore.BeatsRetainedPerMound + 5;
        for (var i = 0; i < overflow; i++)
            store.RecordBeat(new MoundBeat
            {
                MoundId = "mm-1", ReceivedAt = Now.AddSeconds(i).ToWire(), Seq = i, Accepted = true
            });

        var all = store.RecentBeats("mm-1", overflow * 2);

        Assert.Equal(SqliteMoundStore.BeatsRetainedPerMound, all.Count);
        Assert.Equal(overflow - 1L, all[0].Seq);   // newest kept
        Assert.Equal(5L, all[^1].Seq);             // oldest five evicted
    }

    [Fact]
    public void The_store_creates_its_database_file_where_it_was_told_to()
    {
        using var workspace = new TempWorkspace();
        using var store = new SqliteMoundStore();

        store.UpsertMound(Sample());

        Assert.True(File.Exists(store.DbPath));
        Assert.StartsWith(workspace.Root, store.DbPath, StringComparison.OrdinalIgnoreCase);
    }
}
