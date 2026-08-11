namespace Anthill.Desktop;

/// <summary>
/// v0.3.8.44 — the shell's memory of what happened. A WinExe has no console, so everything the
/// colony host PRINTS — including the security posture's refusal, which is exactly the message a
/// failing first run needs — vanished. Console out/err are redirected here, the shell's own
/// events are appended, and the error screen quotes the tail, so a field report can say what the
/// process said instead of "nothing happens".
/// </summary>
internal static class DesktopLog
{
    private static readonly object Lock = new();
    private static string? _path;

    public static string PathHint => _path ?? "(log unavailable)";

    public static void Attach()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anthill");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "desktop.log");
        // One run, one log: the previous run's tail answers "what happened last time", but an
        // append-forever file answers nothing after a month.
        try { File.WriteAllText(_path, $"AnthillDesktop start {DateTime.Now:O}{Environment.NewLine}"); }
        catch { _path = null; return; }

        var writer = new LockedWriter(_path);
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    public static void Write(string line)
    {
        if (_path is null) return;
        lock (Lock) { try { File.AppendAllText(_path, line + Environment.NewLine); } catch { } }
    }

    /// <summary>The last few lines — the part of the story an error screen has room for.</summary>
    public static string Tail(int lines = 12)
    {
        if (_path is null) return "";
        try
        {
            var all = File.ReadAllLines(_path);
            return string.Join(Environment.NewLine, all.TakeLast(lines))
                 + Environment.NewLine + $"(full log: {_path})";
        }
        catch { return $"(log: {_path})"; }
    }

    private sealed class LockedWriter : TextWriter
    {
        private readonly string _file;
        public LockedWriter(string file) => _file = file;
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            lock (Lock) { try { File.AppendAllText(_file, (value ?? "") + Environment.NewLine); } catch { } }
        }
        public override void Write(char value) { /* char-wise writes are noise; line writes carry the story */ }
        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (Lock) { try { File.AppendAllText(_file, value); } catch { } }
        }
    }
}
