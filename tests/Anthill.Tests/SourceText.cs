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

    /// <summary>
    /// ONE MEMBER'S BODY, from its signature to the end of that member. v0.3.8.97.
    ///
    /// THE POPULATION THIS EXISTS FOR. The remark above says every guard built on
    /// <see cref="CodeOnly"/> reads by character offset, and names the CRLF failure that made one of
    /// them pass locally and fail on main. v0.3.8.97 hit the other half of the same defect: adding
    /// six lines and an explanatory paragraph to `PatchSetApply.Preflight` pushed
    /// `requireBaseHash: true` past `AutoApplyAtomicityTests`' 2,000-character window, so a guard
    /// whose subject was unchanged and still true reported the strictness gone. Comments are BLANKED
    /// here and not removed, so explaining a change inside a guarded member is by itself enough to
    /// break a budget-sliced guard — and the reflex a false failure invites is to relax the rule.
    ///
    /// A budget is a proxy for "inside this member" and a bad one: the guess is invisible when it is
    /// wrong, it means something different per platform, and it drifts every time the member grows.
    /// Reading the delimiters answers the question that was actually being asked.
    ///
    /// BOTH MEMBER SHAPES, because the second one bit immediately: `AutoApplyRunner.Preflight` is
    /// expression-bodied, so a brace-matcher that assumes `{` finds the NEXT member's brace and
    /// over-reads into it — which is how a guard comes to pass on its neighbour's code. Whichever of
    /// <c>{</c> or <c>=&gt;</c> comes first decides: a block body is brace-matched, an expression
    /// body runs to the semicolon that closes it at depth zero.
    ///
    /// Falls back to the rest of the file when the delimiters do not balance, which reads as "too
    /// much" rather than "too little": over-reading risks a false pass on a neighbour, while
    /// under-reading reports a false failure on a correct change — and that is the one that gets a
    /// real rule weakened.
    /// </summary>
    public static string MemberBody(string code, int signatureAt)
    {
        if (signatureAt < 0 || signatureAt >= code.Length) return "";

        var brace = code.IndexOf('{', signatureAt);
        var arrow = code.IndexOf("=>", signatureAt, StringComparison.Ordinal);

        // Neither delimiter: not a member declaration this can bound. Hand back the rest.
        if (brace < 0 && arrow < 0) return code[signatureAt..];

        if (arrow >= 0 && (brace < 0 || arrow < brace))
        {
            // Expression body. The terminating semicolon is the one outside every paren, bracket and
            // brace opened after the arrow — a lambda inside the expression (`.Select(e => (a, b))`)
            // opens and closes its own, and a collection or object initializer opens braces.
            var depth = 0;
            for (var i = arrow + 2; i < code.Length; i++)
            {
                var c = code[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == ';' && depth <= 0) return code[signatureAt..(i + 1)];
            }
            return code[signatureAt..];
        }

        var braces = 0;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') braces++;
            else if (code[i] == '}' && --braces == 0) return code[signatureAt..(i + 1)];
        }
        return code[signatureAt..];
    }

    /// <summary>
    /// ONE ARGUMENT OF ONE CALL, WHETHER IT WAS WRITTEN AS A LITERAL OR AS A NAMED CONSTANT.
    /// v0.3.8.112.
    ///
    /// THE DEFECT CLASS THIS ENDS, found four times between `.106` and `.109` and swept for here. A
    /// guard whose pattern is <c>Method\(\s*"(?&lt;x&gt;[a-z_]+)"</c> can only see a call site that
    /// spells its argument as a literal. The moment the code does the tidier thing and passes
    /// <c>EventTypes.MissionStarted</c>, the guard stops seeing that call — silently, with no
    /// failure, forever. So a guard written to stop shared names being hand-spelled ends up
    /// REWARDING hand-spelling, and punishing the refactor it was meant to encourage.
    ///
    /// It is worse in both directions. The paired "every declared X is used by something" twin then
    /// reports the constant as unreached, which is a FALSE POSITIVE on correct code — and the
    /// obvious way to make that go away is to delete the constant.
    ///
    /// WIDEN WHERE THE GUARD LOOKS, NEVER WHAT IT ACCEPTS. That rule is why this returns the
    /// argument's VALUE rather than merely reporting that some argument was present: a resolved
    /// constant is checked against exactly the same vocabulary a literal is, so nothing a guard used
    /// to refuse becomes acceptable by being spelled differently.
    ///
    /// RESOLUTION IS BY DECLARED VALUE, NOT BY REFLECTION over a loaded type. The suite's guards run
    /// against SOURCE — `SourceText` has no xunit and no runtime dependency by design — and several
    /// of them read the API and UI projects, whose constants this assembly does not reference.
    /// <paramref name="constants"/> is therefore supplied by the caller, normally from
    /// <see cref="DeclaredConstants"/> over the vocabulary file that owns the names.
    ///
    /// UNRESOLVED IS NULL, AND THAT IS DELIBERATE. A caller must decide what an argument it cannot
    /// read means for its own rule — a local variable, an interpolated string, a method call. Every
    /// guard in this suite treats it as "not a name I can check", which is honest, and the vacuity
    /// floors those guards already carry are what stop that becoming a way to hide.
    /// </summary>
    /// <param name="code">Source, already passed through <see cref="CodeOnly"/> by the caller.</param>
    /// <param name="method">The method name, matched with a word boundary before the open paren.</param>
    /// <param name="argumentIndex">Zero-based position of the argument to read.</param>
    /// <param name="constants">Declared name → value, for resolving a symbol. May be empty.</param>
    public static IReadOnlyList<string> CallArgument(
        string code, string method, int argumentIndex, IReadOnlyDictionary<string, string>? constants = null) =>
        CallSites(code, method)
            .Select(site => site.Resolve(argumentIndex, constants))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

    /// <summary>
    /// One call, bounded by its own parentheses. v0.3.8.112.
    /// </summary>
    /// <param name="Index">Where the method name starts, for reporting.</param>
    /// <param name="Text">Everything between the parentheses, exactly as written. This is what a
    /// guard should search when it needs to know whether a call passes something — bounding by a
    /// character budget or by the next semicolon truncates any call containing a lambda or a
    /// sentence, which reports a violation that is not there.</param>
    /// <param name="Arguments">The same text split at depth-zero commas.</param>
    public sealed record CallSite(int Index, string Text, IReadOnlyList<string> Arguments)
    {
        /// <summary>The nth argument's value — literal or resolved constant — or null.</summary>
        public string? Resolve(int argumentIndex, IReadOnlyDictionary<string, string>? constants) =>
            argumentIndex >= 0 && argumentIndex < Arguments.Count
                ? ResolveArgument(Arguments[argumentIndex], constants)
                : null;
    }

    /// <summary>
    /// Every call to <paramref name="method"/>, each bounded by its own matching parenthesis.
    ///
    /// A call whose parentheses do not close before the end of the file is SKIPPED rather than
    /// truncated — an unbalanced read is not a call, and handing back a fragment would let a guard
    /// draw a conclusion from half a statement.
    /// </summary>
    public static IReadOnlyList<CallSite> CallSites(string code, string method)
    {
        var sites = new List<CallSite>();
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(method)) return sites;

        var call = new System.Text.RegularExpressions.Regex(
            @"\b" + System.Text.RegularExpressions.Regex.Escape(method) + @"\s*\(",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (System.Text.RegularExpressions.Match match in call.Matches(code))
        {
            var open = match.Index + match.Length;
            var arguments = SplitArguments(code, open);
            if (arguments is null) continue;

            var length = arguments.Sum(a => a.Length) + Math.Max(0, arguments.Count - 1);
            sites.Add(new CallSite(match.Index, code.Substring(open, Math.Min(length, code.Length - open)), arguments));
        }

        return sites;
    }

    /// <summary>
    /// The `public const string` declarations on one type, as name → value.
    ///
    /// Source-read rather than reflected for the reason <see cref="CallArgument"/> gives: these
    /// guards read projects this test assembly does not reference, and a helper that worked only for
    /// referenced types would push half the sweep back to literal-only matching.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DeclaredConstants(string code)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     code, @"const\s+string\s+(?<name>[A-Za-z_]\w*)\s*=\s*""(?<value>[^""]*)""") )
            declared[match.Groups["name"].Value] = match.Groups["value"].Value;
        return declared;
    }

    /// <summary>
    /// The argument list of a call, split at depth-zero commas. Null when the parentheses do not
    /// balance before the end of the file — which reads as "this is not a call I can parse", not as
    /// "this call has no arguments".
    ///
    /// Depth-aware because the alternative fails on the shapes this codebase is full of: a nested
    /// call, a collection initializer, a lambda with a tuple. Splitting on every comma would cut
    /// `new() { ["a"] = 1, ["b"] = 2 }` into pieces and report the fragments as arguments.
    /// </summary>
    private static List<string>? SplitArguments(string code, int afterOpenParen)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = afterOpenParen;
        var inString = false;
        var inChar = false;
        var verbatim = false;

        for (var i = afterOpenParen; i < code.Length; i++)
        {
            var c = code[i];

            if (inString)
            {
                if (verbatim) { if (c == '"' && (i + 1 >= code.Length || code[i + 1] != '"')) inString = false; else if (c == '"') i++; }
                else if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (inChar) { if (c == '\\') i++; else if (c == '\'') inChar = false; continue; }

            switch (c)
            {
                case '"':
                    inString = true;
                    verbatim = i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@'));
                    break;
                case '\'': inChar = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' when depth == 0:
                    arguments.Add(code[start..i]);
                    return arguments;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0:
                    arguments.Add(code[start..i]);
                    start = i + 1;
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// One argument's value: the literal it is, the constant it names, or null.
    ///
    /// A NAMED ARGUMENT (<c>eventType: EventTypes.X</c>) is read past rather than refused — the
    /// name is presentation and the value is the thing being checked. Anything else — a local, an
    /// interpolated string, a call, a ternary — is null, and the caller decides what that means.
    /// </summary>
    private static string? ResolveArgument(string argument, IReadOnlyDictionary<string, string>? constants)
    {
        var text = argument.Trim();
        if (text.Length == 0) return null;

        // Strip a named-argument prefix, but only when it is a bare identifier followed by a colon —
        // never a `?:` ternary, whose colon this must not mistake for one.
        var named = System.Text.RegularExpressions.Regex.Match(text, @"^[A-Za-z_]\w*\s*:(?!:)\s*");
        if (named.Success && !text.Contains('?')) text = text[named.Length..].Trim();

        if (text.Length > 1 && text[0] == '"' && text[^1] == '"' && !text.Contains("\" +"))
        {
            var inner = text[1..^1];
            return inner.Contains('"') ? null : inner;   // a concatenation is not one literal
        }

        if (constants is null || constants.Count == 0) return null;

        // `EventTypes.MissionStarted` or a bare `MissionStarted` — the last segment is the name.
        var symbol = System.Text.RegularExpressions.Regex.Match(text, @"^[A-Za-z_][\w.]*$");
        if (!symbol.Success) return null;

        var last = text[(text.LastIndexOf('.') + 1)..];
        return constants.TryGetValue(last, out var value) ? value : null;
    }

    /// <summary>
    /// Every `public const string` declared anywhere under `src/`, as name → value. v0.3.8.112.
    ///
    /// A REPO-WIDE SYMBOL TABLE, because the guards that need it read call sites whose argument
    /// could name a constant declared in any project — and several of them sweep `src/` entirely.
    /// Built once and cached: the sweep runs in ten tests and re-reading eight hundred files per
    /// test would turn a guard suite into a build step.
    ///
    /// COLLISIONS RESOLVE TO THE LAST DECLARATION READ, and the direction that failure points in is
    /// why this is acceptable. <see cref="CallArgument"/> matches on the final segment of a symbol,
    /// so `Roles.Coder` and `Fixtures.Coder` are one key. A collision therefore yields the WRONG
    /// value, which a guard checks against its vocabulary and — being wrong — most likely refuses.
    /// That is a false positive: noisy, visible, and fixed by whoever hits it. The alternative,
    /// declining to resolve, is a false negative: silent, and indistinguishable from a clean tree.
    /// This suite has been bitten by the second kind four times and by the first kind never.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ConstantsAcrossSource(string repoRoot)
    {
        lock (ConstantLock)
        {
            if (_constants is not null) return _constants;

            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in ProductionFiles(repoRoot))
                foreach (var declared in DeclaredConstants(CodeOnly(File.ReadAllText(file))))
                    all[declared.Key] = declared.Value;

            return _constants = all;
        }
    }

    private static readonly object ConstantLock = new();
    private static IReadOnlyDictionary<string, string>? _constants;

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
