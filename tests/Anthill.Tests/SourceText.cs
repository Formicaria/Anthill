namespace Anthill.Tests;

/// <summary>
/// Reading source as CODE rather than as text. v3.8.33.
///
/// Several guards in this suite search the tree for constructs that must not exist — a
/// <c>FailureClass</c> stringified outside the shared converter, a second patch applier, a hardcoded
/// model tag. Every one of them has the same failure mode on its first run, and it caught me three
/// times: this codebase documents its defects IN COMMENTS, at length, by quoting the offending
/// expression. A guard that greps raw text reports the paragraph explaining why
/// <c>"llama3.1:8b"</c> was wrong as an instance of <c>"llama3.1:8b"</c>.
///
/// The tempting fix is to reword the comment. That is backwards — it deletes the reasoning to keep a
/// test green, and the reasoning is usually worth more than the fix. The correct one is to make the
/// guard read what it claims to read.
///
/// Extracted so there is ONE implementation. Three near-copies of a comment stripper is the same
/// shape as the three patch appliers v3.8.32 collapsed.
/// </summary>
public static class SourceText
{
    /// <summary>
    /// C# source with comments blanked, newlines preserved so reported line numbers stay true.
    ///
    /// String and char literals are kept — a guard looking for a literal needs to see it — while
    /// their contents are skipped for delimiter purposes, so a <c>"//"</c> inside a URL does not
    /// start a comment and a quote inside a verbatim string does not end one.
    /// </summary>
    /// <remarks>
    /// LINE ENDINGS ARE NORMALISED FIRST. v0.3.8.92.
    ///
    /// Every guard built on this reads by CHARACTER OFFSET — `code[start..start + 4000]`, an index
    /// comparison, a slice to the next marker. On a Windows checkout with `core.autocrlf` the same
    /// file is one character longer per line, so a window that fits on Linux does not fit there, and
    /// a guard passes on one platform and fails on the other for a reason that has nothing to do
    /// with what it is checking.
    ///
    /// That is exactly what v0.3.8.91 shipped: `TheBypassLane_IsGatedBeforeItSynthesizesItsOwnApproval`
    /// read a 4,000-character window, which held on Linux and ran out three lines short on
    /// windows-latest — and comments are BLANKED here rather than removed, so a long explanatory
    /// block spends that budget without contributing anything the guard reads. Every local run was
    /// green and main went red.
    ///
    /// Normalising here rather than at each call site, because the call sites are the population
    /// that keeps growing.
    /// </remarks>
    public static string CodeOnly(string source)
    {
        source = (source ?? "").Replace("\r\n", "\n");
        var sb = new System.Text.StringBuilder(source.Length);
        bool inLine = false, inBlock = false, inString = false, inChar = false, verbatim = false;

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); }
                else sb.Append(' ');
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; sb.Append("  "); i++; }
                else sb.Append(c == '\n' ? '\n' : ' ');
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                // A doubled quote inside a verbatim string is an escaped quote, not the end of it.
                if (verbatim && c == '"' && next == '"') { sb.Append(next); i++; }
                else if (!verbatim && c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == '"') { inString = false; verbatim = false; }
                continue;
            }
            if (inChar)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/') { inLine = true; sb.Append("  "); i++; continue; }
            if (c == '/' && next == '*') { inBlock = true; sb.Append("  "); i++; continue; }
            if (c == '$' && next == '@' && i + 2 < source.Length && source[i + 2] == '"')
            { verbatim = true; inString = true; sb.Append(c).Append(next).Append('"'); i += 2; continue; }
            if (c == '@' && next == '"') { verbatim = true; inString = true; sb.Append(c).Append(next); i++; continue; }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '\'') { inChar = true; sb.Append(c); continue; }

            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Every production .cs file, excluding build output.</summary>
    public static IEnumerable<string> ProductionFiles(string repoRoot) =>
        Directory.GetFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Walk up to the repository root, so guards do not depend on the runner's cwd.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Anthill.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <summary>
    /// One numbered line from PLAN.md's acceptance-gate list. v0.3.8.57.
    ///
    /// SCOPED TO THE SECTION, and matched at column zero. Two tests independently reached for
    /// `lines.Single(l => l.TrimStart().StartsWith("7."))` and one of them threw: the document also
    /// contains "§7.3/§7.4" and version strings, and `TrimStart` made every indented sub-bullet a
    /// candidate. The gate-10 test used the identical idiom and passed only because no other line
    /// happened to start with "10." — a guard that works by luck is one that fails on an edit
    /// unrelated to what it guards.
    ///
    /// Shared rather than fixed twice, for the reason this release keeps restating: two copies of a
    /// lookup are two things that can drift, and here they would drift silently into passing.
    /// </summary>
    public static string PlanAcceptanceGate(int number)
    {
        var plan = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "PLAN.md"));

        // By NAME, not by section number. This was pinned to "## 5. Acceptance gates" and broke when
        // the plan was renumbered — a failure that said the gates were gone when every gate was
        // present and unchanged. The gate list is the thing; its ordinal is presentation.
        var heading = System.Text.RegularExpressions.Regex.Match(
            plan, @"^##\s+\S+\s+Acceptance gates\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline
          | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!heading.Success) throw new InvalidOperationException(
            "docs/PLAN.md no longer has an '## <n>. Acceptance gates' section, so no gate can be read from it.");

        var start = heading.Index;
        var end = plan.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? plan[start..] : plan[start..end];

        var line = section.Split('\n').FirstOrDefault(l =>
            l.StartsWith($"{number}. ", StringComparison.Ordinal));

        return line ?? throw new InvalidOperationException(
            $"acceptance gate {number} is not in docs/PLAN.md's gate list.");
    }
}
