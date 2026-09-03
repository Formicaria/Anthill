using System.Reflection;
using System.Text.Json;
using Anthill.Modules.Micromound;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// EVERY FIELD ON A MOUND RECORD SURVIVES THE DATABASE. v0.3.8.114.
///
/// THE BUG THIS EXISTS FOR, and it was live for about ten minutes during this release. `.114` added
/// six fields to `MoundRecord` — charter id, charter expiry, lease expiry, quiesced, autonomy
/// policy, manifest id — and `SqliteMoundStore` persists that record COLUMN BY COLUMN. The in-memory
/// store holds the object itself, so every test passed while SQLite silently dropped all six. A
/// colony would have chartered a mound, restarted, and believed it had never chartered anything.
///
/// That is the shape a per-field assertion cannot catch, because a per-field assertion only covers
/// the fields somebody remembered to write one for — which is the same set they remembered to
/// persist. So this walks the TYPE by reflection: every property gets a distinctive value, the
/// record round-trips through a real database file, and every property is compared back. A seventh
/// field added tomorrow and not persisted fails here without anybody adding a line.
/// </summary>
[Collection(MicromoundCollection.Name)]
public class StorePersistenceTests
{
    private static IEnumerable<PropertyInfo> Fields() =>
        typeof(MoundRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true });

    /// <summary>
    /// A value that is not the default, derived from the property's own name so a mix-up between two
    /// string columns shows up as a mismatch rather than as two equal strings.
    /// </summary>
    private static object Distinctive(PropertyInfo property)
    {
        var type = property.PropertyType;

        if (type == typeof(string)) return "value-for-" + property.Name;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 4242;
        if (type == typeof(long)) return 424242L;
        if (type == typeof(List<string>)) return new List<string> { "cap.one", "cap.two" };
        if (type == typeof(AutonomyPolicy)) return AutonomyPolicy.WithinCharter;

        throw new NotSupportedException(
            $"MoundRecord.{property.Name} is a {type.Name}, which this round-trip does not know how "
          + "to fill. Teach it, rather than excluding the property — an exclusion list is how a "
          + "field stops being covered without anybody deciding that.");
    }

    [Fact]
    public void EveryMoundRecordField_RoundTripsThroughSqlite()
    {
        var fields = Fields().ToList();

        // Vacuity floor. A reflection filter that stopped matching would compare nothing and pass.
        Assert.True(fields.Count >= 15,
            $"only {fields.Count} settable properties were found on MoundRecord; the filter has "
          + "stopped seeing the type it measures.");

        var written = new MoundRecord();
        foreach (var field in fields) field.SetValue(written, Distinctive(field));

        // mound_id is the primary key, so it has to be a usable one rather than a decorated name.
        written.MoundId = "mm-roundtrip";

        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-rt-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.db");

            using (var store = new SqliteMoundStore(path)) store.UpsertMound(written);

            // A NEW STORE OVER THE SAME FILE. Re-reading through the same instance could be served
            // by anything cached; this is the restart the bug would actually have shown up on.
            using var reopened = new SqliteMoundStore(path);
            var read = reopened.GetMound("mm-roundtrip");

            Assert.NotNull(read);

            var lost = new List<string>();
            foreach (var field in fields)
            {
                var expected = field.GetValue(written);
                var actual = field.GetValue(read);

                var same = expected is List<string> list
                    ? actual is List<string> other && list.SequenceEqual(other, StringComparer.Ordinal)
                    : Equals(expected, actual);

                if (!same) lost.Add($"{field.Name}: wrote {Show(expected)}, read {Show(actual)}");
            }

            Assert.True(lost.Count == 0,
                "these MoundRecord fields did not survive the database:\n  " + string.Join("\n  ", lost)
              + "\nSqliteMoundStore persists this record column by column, and the in-memory store "
              + "holds the object — so a field missing from the schema, the SELECT, ReadMound or "
              + "UpsertMound passes every other test and is lost on restart.");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// A CHARTER SURVIVES TOO, and it is stored as JSON rather than as columns — so the failure mode
    /// is different and worth its own fact: a shape change that stops deserializing returns null,
    /// and `RenewLease` reads a null charter as "nothing to renew", which is a lease that silently
    /// stops being extended rather than an error anybody sees.
    /// </summary>
    [Fact]
    public void ACharter_RoundTripsThroughSqlite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-ch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.db");
            var charter = new Charter
            {
                CharterId = "charter-1",
                MoundId = "mm-roundtrip",
                IssuedAt = DateTimeOffset.UtcNow.ToWire(),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToWire(),
                LeaseTtlSeconds = 900,
                ActionCeiling = "benign",
                Capabilities = ["sense.temperature", "act.water_valve"],
                Routines = ["routine.water_cycle"],
                Limits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal)
                {
                    ["act.water_valve"] = new() { MaxOnSeconds = 30, MinOffSeconds = 300 },
                },
            };

            using (var store = new SqliteMoundStore(path)) store.PutCharter(charter);

            using var reopened = new SqliteMoundStore(path);
            var read = reopened.GetCharter("charter-1");

            Assert.NotNull(read);
            Assert.Equal(charter.LeaseTtlSeconds, read!.LeaseTtlSeconds);
            Assert.Equal(charter.Capabilities, read.Capabilities);
            Assert.Equal(charter.Routines, read.Routines);
            Assert.Equal(30, read.Limits["act.water_valve"].MaxOnSeconds);
            Assert.Equal(300, read.Limits["act.water_valve"].MinOffSeconds);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// AND THE DOWNLINK QUEUE KEEPS ITS ORDER AND ITS SIGNATURE ACROSS A RESTART.
    ///
    /// Order matters because a stop must arrive before whatever was queued behind it, and the
    /// signature matters because an envelope whose bytes changed in storage is one the mound refuses
    /// — which would look like the colony's key being wrong rather than its database.
    /// </summary>
    [Fact]
    public void TheDownlinkQueue_SurvivesARestartInOrderAndStillVerifies()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-dl-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.db");
            string publicKey;

            using (var store = new SqliteMoundStore(path))
            {
                var identity = new MicromoundIdentity(store);
                publicKey = identity.PublicKeyHex;

                foreach (var kind in new[] { EnvelopeKinds.Config, EnvelopeKinds.Charter, EnvelopeKinds.Stop })
                    store.QueueDownlink("mm-roundtrip", identity.Sign(new Envelope
                    {
                        MoundId = "mm-roundtrip",
                        Kind = kind,
                        SentAt = DateTimeOffset.UtcNow.ToWire(),
                        Body = JsonSerializer.SerializeToElement(new { }),
                    }));
            }

            using var reopened = new SqliteMoundStore(path);
            var drained = reopened.DrainDownlink("mm-roundtrip");

            Assert.Equal(
                [EnvelopeKinds.Config, EnvelopeKinds.Charter, EnvelopeKinds.Stop],
                drained.Select(e => e.Kind).ToList());

            var directory = new InMemoryPublicKeyDirectory();
            directory.Register(KeyIds.Controller, Convert.FromHexString(publicKey));
            var verifier = new Ed25519EnvelopeVerifier(directory);

            foreach (var envelope in drained)
                Assert.True(
                    verifier.Verify(KeyIds.Controller, envelope.CanonicalBytes(), envelope.Signature).IsValid,
                    $"a {envelope.Kind} envelope no longer verifies after a database round trip");

            Assert.Empty(reopened.DrainDownlink("mm-roundtrip"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// UNLINKING A MOUND LEAVES NOTHING OF IT BEHIND — and the guard reads the SCHEMA rather than a
    /// list somebody maintained. v0.3.8.114.
    ///
    /// `RemoveMound` swept two tables when there were two; by this release there are nine, and the
    /// comment above it still said "token and beats leave with the record". A mound id can be
    /// re-minted, so a charter, a queued downlink envelope or a pile of evidence outliving the
    /// device it belonged to is authority and proof addressed to whatever claims that id next.
    ///
    /// The rule is expressed as a question about the database: any table with a `mound_id` column
    /// holds rows belonging to one mound, so any such table missing from `PerMoundTables` is a table
    /// an unlink would orphan. `micromound_widget_state` and `micromound_controller_identity` have
    /// no such column and are correctly absent — the guard derives that rather than being told.
    /// </summary>
    [Fact]
    public void EveryPerMoundTable_IsSweptOnRemoveMound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-sweep-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var store = new SqliteMoundStore(Path.Combine(dir, "t.db"));

            // `micromound_mounds` is keyed by `mound_id` and is deliberately NOT in the sweep: it
            // holds the RECORD, which `RemoveMound`'s final DELETE removes. Sweeping it in the loop
            // as well would be harmless and would also mean this guard could no longer tell "the
            // record is deleted" from "a satellite table is swept", which are the two different
            // things it exists to keep straight.
            var keyed = SqliteMoundStore.TableNames
                .Where(t => t != "micromound_mounds")
                .Where(t => ColumnsOf(store, t).Contains("mound_id", StringComparer.Ordinal))
                .ToList();

            // Vacuity floor: a schema read that found nothing would pass this silently.
            Assert.True(keyed.Count >= 8,
                $"only {keyed.Count} table(s) look mound-keyed; the schema read is not working");

            var unswept = keyed.Except(SqliteMoundStore.PerMoundTables, StringComparer.Ordinal).ToList();

            Assert.True(unswept.Count == 0,
                "these tables hold rows keyed to one mound and RemoveMound does not sweep them, so "
              + "unlinking a device leaves its rows for whatever claims the id next: "
              + string.Join(", ", unswept));

            var stale = SqliteMoundStore.PerMoundTables.Except(keyed, StringComparer.Ordinal).ToList();

            Assert.True(stale.Count == 0,
                "RemoveMound sweeps tables that are not mound-keyed: " + string.Join(", ", stale));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// THE TWO STORES ANSWER THE SAME QUESTIONS THE SAME WAY. Every test in this project runs
    /// against `InMemoryMoundStore`, and the colony runs against SQLite — so a semantic the two
    /// disagree about is a semantic proven in a store nobody ships.
    ///
    /// This drives both through the members `.114` added, because those are the ones with no
    /// coverage anywhere else yet, and compares the answers rather than asserting each separately.
    /// </summary>
    [Fact]
    public void TheTwoStores_AgreeOnWhatDotOneOneFourAdded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anthill-mm-parity-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        try
        {
            using var sqlite = new SqliteMoundStore(Path.Combine(dir, "t.db"));

            foreach (IMoundStore store in new IMoundStore[] { new InMemoryMoundStore(), sqlite })
            {
                var label = store.GetType().Name;

                store.UpsertMound(new MoundRecord { MoundId = "mm-p", Name = "Parity" });
                store.PutEnrollmentToken(new EnrollmentToken
                {
                    MoundId = "mm-p", TokenHash = "hash-p", IssuedAt = "", ExpiresAt = "",
                });

                Assert.Contains(store.AllEnrollmentTokens(),
                    t => t.MoundId == "mm-p" && t.TokenHash == "hash-p");

                store.PutMission(new Mission { MissionId = "mi-1", MoundId = "mm-p", ExpiresAt = "2026-09-03T12:00:00Z" });
                Assert.Equal(["mi-1"], store.MissionsForMound("mm-p", 10).Select(m => m.MissionId).ToList());
                Assert.Empty(store.MissionsForMound("mm-other", 10));

                store.PutMissionReport("mm-p", new MissionReport { MissionId = "mi-1", State = "completed" });
                Assert.Equal("completed", store.GetMissionReport("mm-p", "mi-1")?.State);
                Assert.Null(store.GetMissionReport("mm-p", "mi-absent"));
                // Another mound's report is another mound's, even under the same mission id.
                Assert.Null(store.GetMissionReport("mm-other", "mi-1"));

                // A Body is not optional: `default(JsonElement)` is `Undefined`, and both
                // `JsonSerializer.Serialize` and `Envelope.CanonicalBytes` throw on it. An envelope
                // without one could never have been signed either.
                store.QueueDownlink("mm-p", new Envelope
                {
                    MoundId = "mm-p",
                    Kind = EnvelopeKinds.Stop,
                    SentAt = "2026-09-03T12:00:00Z",
                    Body = JsonSerializer.SerializeToElement(new { reason = "parity" }),
                });
                Assert.Equal(1, store.PendingDownlinkCount("mm-p"));
                store.DiscardDownlink("mm-p");
                Assert.Equal(0, store.PendingDownlinkCount("mm-p"));
                Assert.Empty(store.DrainDownlink("mm-p"));

                Assert.True(store.RemoveMound("mm-p"), label);
                Assert.Empty(store.MissionsForMound("mm-p", 10));
                Assert.Null(store.GetMissionReport("mm-p", "mi-1"));
                Assert.DoesNotContain(store.AllEnrollmentTokens(), t => t.MoundId == "mm-p");
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static IReadOnlyList<string> ColumnsOf(SqliteMoundStore store, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = store.DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }

    private static string Show(object? value) => value switch
    {
        null => "(null)",
        List<string> list => "[" + string.Join(", ", list) + "]",
        _ => value.ToString() ?? "(null)",
    };
}
