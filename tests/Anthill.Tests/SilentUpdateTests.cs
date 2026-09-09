using System.IO.Compression;
using Anthill.Core.Updates;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// WHAT MAKES AN UNATTENDED UPDATE SAFE TO SHIP. v0.3.8.146.
///
/// The operator's ask was blunt and correct: users hate clicking through an installer, and an
/// application that already has permission to be installed should not beg for it again every
/// release. Granting that removes a human from a loop they were standing in for two different
/// reasons, and only one of those reasons was a ritual.
///
/// The ritual was CONSENT, and it moves: given once, in Settings, instead of once per release.
///
/// The other reason was that a person stood between a downloaded executable and the machine it
/// ran on. Nothing about disliking prompts makes that less necessary — it makes it more so, since
/// nobody is watching now. These tests pin what replaces it:
///
///   · a payload that does not match its published digest is DELETED and refused, never run;
///   · the colony's data is never inside the set of files an update replaces, and an archive that
///     reaches for it is refused whole rather than partially applied;
///   · a container never updates itself in place, because the next recreate would undo it;
///   · one version comparison, so "is this newer" cannot have two answers.
///
/// If a later change makes any of these fail, the honest reading is that silent updating has
/// become unsafe again, not that a test needs relaxing.
/// </summary>
public class SilentUpdateTests : IDisposable
{
    private readonly string _dir;

    public SilentUpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-update-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- the digest is the thing that replaced the click ---------------------------------------

    [Fact]
    public void APayloadThatMatchesItsDigest_IsAccepted()
    {
        var payload = Path.Combine(_dir, "anthill-setup-9.9.9.9.exe");
        File.WriteAllText(payload, "pretend this is an installer");

        var digest = UpdateStaging.DigestOf(payload);

        Assert.True(UpdateStaging.VerifyOrDelete(payload, digest, out var message));
        Assert.Contains(digest, message, StringComparison.Ordinal);
        Assert.True(File.Exists(payload), "a verified payload must survive verification");
    }

    /// <summary>
    /// THE ONE THAT MATTERS. A mismatch deletes the file: it cannot be installed, and an executable
    /// of unknown provenance sitting in the directory the updater runs things from is the hazard,
    /// not a diagnostic asset.
    /// </summary>
    [Fact]
    public void APayloadThatFailsItsDigest_IsDeletedAndRefused()
    {
        var payload = Path.Combine(_dir, "anthill-setup-9.9.9.9.exe");
        File.WriteAllText(payload, "this is not what the release published");

        var somebodyElsesDigest = new string('a', 64);

        Assert.False(UpdateStaging.VerifyOrDelete(payload, somebodyElsesDigest, out var message));
        Assert.False(File.Exists(payload), "a payload that failed its checksum must not be left on disk");
        Assert.Contains("did not match", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was installed", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sidecar's shape is `sha256sum`'s, and only the digest is taken from it.</summary>
    [Theory]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  anthill-setup-1.2.3.exe",
                "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08 *file.zip",
                "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void TheSidecar_YieldsItsDigest(string sidecar, string expected) =>
        Assert.Equal(expected, UpdateStaging.ParseDigest(sidecar));

    [Theory]
    [InlineData("")]
    [InlineData("not a digest at all")]
    [InlineData("deadbeef  short.exe")]                              // too short to be sha256
    [InlineData("zzzz6d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  x")]  // not hex
    public void ASidecarThatIsNotADigest_YieldsNothing(string sidecar) =>
        Assert.Null(UpdateStaging.ParseDigest(sidecar));

    // ---- the colony's data is never in the replacement set -------------------------------------

    /// <summary>
    /// The portable shape keeps its database BESIDE the binary. An archive whose entries reach into
    /// `.anthill` is refused whole — not applied-except-that-entry, because a partially applied
    /// update is a version that never shipped.
    /// </summary>
    [Fact]
    public void AnArchiveThatReachesIntoTheDataDirectory_IsRefusedWhole()
    {
        var program = Path.Combine(_dir, "portable");
        var data = Path.Combine(program, ".anthill");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "anthill.db"), "the colony's entire memory");
        var untouched = Path.Combine(program, "keep-me.txt");
        File.WriteAllText(untouched, "an operator's own file");

        var archive = Path.Combine(_dir, "hostile.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Entry(zip, "anthill.exe", "the new binary");
            Entry(zip, ".anthill/anthill.db", "a database that is not yours");
        }

        var site = Site(program, data, InstallShape.WindowsPortable);
        var result = UpdateApplier.ApplyArchive(site, archive);

        Assert.False(result.Applied);
        Assert.True(result.Fatal);
        Assert.Contains("data directory", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("the colony's entire memory", File.ReadAllText(Path.Combine(data, "anthill.db")));
        Assert.False(File.Exists(Path.Combine(program, "anthill.exe")),
            "a refused update must not have applied its harmless entries either");
        Assert.True(File.Exists(untouched));
    }

    /// <summary>And the ordinary case: only what the archive carries is replaced, everything else stands.</summary>
    [Fact]
    public void AnOrdinaryArchive_ReplacesItsOwnFilesAndNothingElse()
    {
        var program = Path.Combine(_dir, "portable2");
        var data = Path.Combine(program, ".anthill");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "anthill.db"), "colony memory");
        File.WriteAllText(Path.Combine(program, "anthill.exe"), "the OLD binary");
        File.WriteAllText(Path.Combine(program, "operator-notes.txt"), "mine, not yours");

        var archive = Path.Combine(_dir, "good.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Entry(zip, "anthill.exe", "the NEW binary");
            Entry(zip, "README.md", "release notes");
        }

        var result = UpdateApplier.ApplyArchive(Site(program, data, InstallShape.WindowsPortable), archive);

        Assert.True(result.Applied, result.Message);
        Assert.Equal("the NEW binary", File.ReadAllText(Path.Combine(program, "anthill.exe")));
        Assert.Equal("release notes", File.ReadAllText(Path.Combine(program, "README.md")));
        // Untouched, because the archive never mentioned them.
        Assert.Equal("mine, not yours", File.ReadAllText(Path.Combine(program, "operator-notes.txt")));
        Assert.Equal("colony memory", File.ReadAllText(Path.Combine(data, "anthill.db")));
    }

    /// <summary>A path-traversal entry is refused for the same reason, one directory further out.</summary>
    [Fact]
    public void AnArchiveThatEscapesTheProgramDirectory_IsRefused()
    {
        var program = Path.Combine(_dir, "portable3");
        Directory.CreateDirectory(program);
        var archive = Path.Combine(_dir, "escape.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            Entry(zip, "../escaped.txt", "somewhere else entirely");

        var result = UpdateApplier.ApplyArchive(Site(program, Path.Combine(program, ".anthill"), InstallShape.WindowsPortable), archive);

        Assert.False(result.Applied);
        Assert.True(result.Fatal);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
    }

    [Fact]
    public void IsInside_KnowsADirectoryFromItsNeighbour()
    {
        var root = Path.Combine(_dir, "app");
        Assert.True(UpdateApplier.IsInside(Path.Combine(root, ".anthill", "anthill.db"), Path.Combine(root, ".anthill")));
        Assert.True(UpdateApplier.IsInside(Path.Combine(root, ".anthill"), Path.Combine(root, ".anthill")));
        // A sibling whose name merely STARTS with the same text is not inside it.
        Assert.False(UpdateApplier.IsInside(Path.Combine(root, ".anthill-backup", "x"), Path.Combine(root, ".anthill")));
        Assert.False(UpdateApplier.IsInside(Path.Combine(root, "bin", "anthill"), Path.Combine(root, ".anthill")));
    }

    // ---- the shapes that must never self-update ------------------------------------------------

    /// <summary>
    /// A container is replaced, not updated. Writing into the image layer would be undone by the
    /// next recreate and would leave the running code disagreeing with its own tag.
    /// </summary>
    [Fact]
    public void ADetectedContainer_RefusesToSelfUpdate()
    {
        var site = Site("/app", "/app/.anthill", InstallShape.Docker) with { CanSelfUpdate = false };

        Assert.False(site.CanSelfUpdate);
        Assert.Null(site.AssetFor("9.9.9.9"));
    }

    /// <summary>And an install nothing identified does nothing at all, rather than guessing.</summary>
    [Fact]
    public void AnUnknownShape_ConsumesNoAssetAndCannotUpdate()
    {
        var site = Site("/somewhere", "/somewhere/.anthill", InstallShape.Unknown) with { CanSelfUpdate = false };

        Assert.Null(site.AssetFor("9.9.9.9"));
        Assert.False(site.CanSelfUpdate);
    }

    /// <summary>Each shape consumes the asset the release workflow actually publishes for it.</summary>
    [Theory]
    [InlineData(InstallShape.WindowsInstalled, "anthill-setup-1.2.3.4.exe")]
    [InlineData(InstallShape.WindowsPortable, "anthill-1.2.3.4-win-x64.zip")]
    [InlineData(InstallShape.LinuxService, "anthill-1.2.3.4-linux-x64.tar.gz")]
    public void EachShape_NamesTheAssetTheReleasePublishes(InstallShape shape, string expected) =>
        Assert.Equal(expected, Site("/p", "/p/.anthill", shape).AssetFor("1.2.3.4"));

    /// <summary>Staging always lands under the DATA directory — the one place every shape can write.</summary>
    [Fact]
    public void StagingLivesUnderTheDataDirectory()
    {
        var site = Site(Path.Combine(_dir, "bin"), Path.Combine(_dir, "data"), InstallShape.LinuxService);
        Assert.True(UpdateApplier.IsInside(site.StagingDirectory, site.DataDirectory));
    }

    // ---- one version comparison -----------------------------------------------------------------

    /// <summary>
    /// v0.3.8.146 — there were two. `UpdateChecker.Compare` handled four-part versions; the desktop
    /// updater used `System.Version.TryParse`, which FAILS on a five-part or one-part string and
    /// fell toward "no update available" when it did. Two answers to "is this newer" is how a
    /// colony comes to believe it is current while an update sits on the shelf.
    /// </summary>
    [Theory]
    [InlineData("0.3.8.145", "0.3.8.146", -1)]
    [InlineData("0.3.8.146", "0.3.8.145", 1)]
    [InlineData("0.3.8.146", "0.3.8.146", 0)]
    [InlineData("v0.3.8.146", "0.3.8.146", 0)]      // a tag and a version are the same thing
    [InlineData("1.8.14", "1.8.14.0", 0)]           // a missing part is zero
    [InlineData("1.9", "1.8.99.99", 1)]             // minor beats patch
    [InlineData("0.3.8.9", "0.3.8.10", -1)]         // numeric, not lexical
    public void OneComparison_AnswersForEveryShape(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(UpdateVersions.Compare(left, right)));

    // ---- helpers ---------------------------------------------------------------------------------

    private static InstallSite Site(string program, string data, InstallShape shape) =>
        new(shape, program, data, CanSelfUpdate: true, Explanation: "test");

    private static void Entry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}
