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
