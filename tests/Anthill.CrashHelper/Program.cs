using Anthill.SDK.Common;

// Scenario 17's crashing process. v0.3.8.79.
//
// Opens a real ApplyTransaction against a real workspace, writes the patched bytes, signals that it
// has reached a durable mid-apply state, and then blocks forever waiting to be killed.
//
// WHAT IT MUST NOT DO, and this is the whole point of it existing: commit, roll back, dispose, or
// return. The transaction is left OPEN with its journal and backups on disk, which is precisely the
// state a machine is in when it loses power between the first write and the commit. The test then
// kills this process and runs recovery in the parent.
//
// THE SENTINEL IS WRITTEN LAST, after the mutations. A test that killed on process START would race
// the writes and usually prove nothing — the journal might not exist yet, and "recovered cleanly
// from no journal" is a pass that means nothing happened. Waiting for the sentinel makes the kill
// deterministic: by the time it appears, the journal is durable and the files are patched.
//
// Usage: <workspace-root> <sentinel-path> <file=content> [<file=content> …]
//    or: intent <db-path> <workspace-root> <sentinel-path> <phase> <relative-target> <new-content> <mission-id>
//
// The second mode is v0.3.8.94's crash-injection matrix for the PATCH APPLY INTENT journal — the
// database half of crash safety, where scenario 17 covered the filesystem half. It drives the
// journal to a chosen phase using the EXACT sequence the live apply path uses
// (BeginApplyIntent → AdvanceApplyIntent(Mutating) → write → AdvanceApplyIntent(Applied, postHash)),
// stops at the requested boundary, signals durability, and blocks to be killed. The parent then
// runs PatchApplyReconciler in its own process against the same database, which is precisely the
// restart the journal exists for. Same sentinel discipline, same reason: killing on start would
// race the writes, and "reconciled nothing" is a pass that means nothing happened.
//
// Phases: prepared | mutating-unwritten | mutating-written | applied
//   prepared           — intent row only; nothing touched. Reconciler must discard.
//   mutating-unwritten — phase advanced, write never issued; file still holds pre-apply bytes.
//                        Reconciler must discard (hash-decided).
//   mutating-written   — bytes changed mid-write with no post-hash on record. Reconciler must
//                        refuse to guess: needs-operator, intent left OPEN.
//   applied            — bytes and post-hash recorded; database effects never happened. Reconciler
//                        must FINISH them (the case that used to become an unrevertable phantom).

if (args.Length >= 1 && args[0] == "intent")
{
    if (args.Length < 8)
    {
        Console.Error.WriteLine("usage: intent <db-path> <workspace-root> <sentinel-path> <phase> <relative-target> <new-content> <mission-id>");
        return 2;
    }

    var dbPath = args[1];
    var workspace = args[2];
    var intentSentinel = args[3];
    var phase = args[4];
    var relativeTarget = args[5];
    var newContent = args[6];
    var missionId = args[7];

    using var memory = new Anthill.Core.Memory.SqliteMemory(dbPath);
    var target = Path.Combine(workspace, relativeTarget);
    var preHash = File.Exists(target) ? ApplyTransaction.HashFile(target) : null;

    // The live sequence, verbatim — the matrix characterizes production, not an approximation.
    var intent = memory.BeginApplyIntent(
        patchId: "crash-patch", approvalId: "crash-approval", patchSetId: "crash-set",
        missionId: missionId, targetPath: relativeTarget, preHash: preHash);

    if (phase != "prepared")
        memory.AdvanceApplyIntent(intent.Id, Anthill.Core.Verification.PatchApplyPhase.Mutating);

    if (phase is "mutating-written" or "applied")
        File.WriteAllText(target, newContent);

    if (phase == "applied")
        memory.AdvanceApplyIntent(intent.Id, Anthill.Core.Verification.PatchApplyPhase.Applied,
            ApplyTransaction.HashFile(target));

    // Durable before the signal; the kill lands on committed rows and flushed bytes.
    File.WriteAllText(intentSentinel, intent.Id);
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <workspace-root> <sentinel-path> <relative-file>=<content> …");
    return 2;
}

var root = args[0];
var sentinel = args[1];

var tx = ApplyTransaction.Begin(root, "scenario 17: killed mid-apply");

foreach (var pair in args.Skip(2))
{
    var split = pair.IndexOf('=');
    if (split <= 0) { Console.Error.WriteLine($"malformed pair: {pair}"); return 2; }

    var relative = pair[..split];
    var content = pair[(split + 1)..];
    tx.WriteFile(Path.Combine(root, relative), content);
}

// Flushed to disk before the signal, so the parent's kill lands on a state that is durable rather
// than merely intended.
File.WriteAllText(sentinel, tx.Id);

// Block forever. Not a long sleep with an exit: an exit path is a way for this to end tidily, and
// a tidy end is the thing that must not happen.
Thread.Sleep(Timeout.Infinite);
return 0;
