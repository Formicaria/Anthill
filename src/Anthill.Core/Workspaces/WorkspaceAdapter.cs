using Anthill.Core.Tools;

namespace Anthill.Core.Workspaces;

/// <summary>
/// v3.5.0 — a declarative description of how to build, test and format ONE kind of project.
///
/// The exit gate it serves: "verification commands come from the manifest or operator
/// configuration, never model invention." <see cref="CheckCatalog"/> already had the right shape —
/// declared commands with fixed arguments and hard timeouts — but was hard-coded to three .NET
/// entries, so a Node workspace had nothing to verify with and the only way to check a frontend
/// change was to hand a model a shell.
///
/// THE SHARP BOUNDARY, and the reason this is a C# record rather than a config file read out of the
/// workspace: an adapter's commands are declared HERE, in this repository, under review. They are
/// never read from the project being worked on.
///
/// That distinction is easy to miss and load-bearing. A <c>package.json</c> "scripts" block is
/// content of the repository under modification — which, in a harness whose stated purpose is
/// self-improvement, is a file an agent can edit. Running <c>npm run test</c> by executing whatever
/// string that file currently holds would mean an agent could rewrite its own verification step into
/// anything at all, and the check that was supposed to catch it would be the thing carrying it out.
/// So detection reads the project; EXECUTION reads only this file.
/// </summary>
public sealed record WorkspaceAdapter(
    string Id,
    string Version,
    string Description,

    /// <summary>
    /// Filename globs whose presence means this adapter applies. Matched against the workspace's
    /// top two directory levels — deep enough to find a solution in <c>src/</c>, shallow enough not
    /// to be fooled by a fixture buried in test data.
    /// </summary>
    IReadOnlyList<string> DetectWhen,

    /// <summary>The checks this adapter contributes, already in <see cref="CheckDefinition"/> form.</summary>
    IReadOnlyList<CheckDefinition> Checks)
{
    /// <summary>Whether this adapter applies to <paramref name="root"/>.</summary>
    public bool Detects(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;

        foreach (var pattern in DetectWhen)
        {
            // TopDirectoryOnly plus one explicit level, rather than AllDirectories: a recursive
            // search of a real repository walks node_modules and bin/obj, which is both slow and
            // wrong — a package.json inside a dependency does not make the workspace a Node project.
            if (Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any()) return true;

            foreach (var child in SafeDirectories(root))
                if (Directory.EnumerateFiles(child, pattern, SearchOption.TopDirectoryOnly).Any()) return true;
        }

        return false;
    }

    /// <summary>
    /// Immediate subdirectories worth looking in. Skips the directories every ecosystem fills with
    /// other people's projects — a <c>package.json</c> under <c>node_modules</c> describes a
    /// dependency, not this workspace, and treating it as detection makes every repository a Node
    /// repository.
    /// </summary>
    private static IEnumerable<string> SafeDirectories(string root)
    {
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name is ".git" or "node_modules" or "bin" or "obj" or "dist" or "target"
                or "vendor" or ".venv" or "__pycache__") continue;
            yield return child;
        }
    }
}

/// <summary>
/// v3.5.0 — the reference adapters. Adding a language is an entry here, reviewed, rather than a new
/// code path.
///
/// Every command is a fixed filename plus a fixed argument string. Nothing here interpolates a
/// path, a task description, or anything else a model can influence — the moment an argument string
/// becomes a template, "declared commands" turns back into command construction with extra steps.
/// </summary>
public static class WorkspaceAdapters
{
    public static readonly IReadOnlyList<WorkspaceAdapter> All = new[]
    {
        new WorkspaceAdapter("dotnet", "1", ".NET solution or project",
            DetectWhen: new[] { "*.sln", "*.csproj", "*.fsproj" },
            Checks: new[]
            {
                new CheckDefinition("dotnet_build", "dotnet", "build -c Release --nologo", 600, true, ".NET build"),
                new CheckDefinition("dotnet_test", "dotnet", "test -c Release --nologo", 1200, true, ".NET test suite"),
                new CheckDefinition("dotnet_format_check", "dotnet", "format --verify-no-changes", 300, true,
                    ".NET formatting check (reports, never rewrites)"),
            }),

        new WorkspaceAdapter("node", "1", "Node / frontend package",
            DetectWhen: new[] { "package.json" },
            Checks: new[]
            {
                // `npm ci` rather than `npm install`: ci is reproducible from the lockfile and fails
                // when the lockfile disagrees with package.json, which is exactly the signal wanted
                // before verifying anything. install would quietly rewrite the lockfile — a source
                // change made by the verification step.
                new CheckDefinition("node_install", "npm", "ci --no-audit --no-fund", 900, true,
                    "Reproducible dependency install from the lockfile"),
                // `npm test` runs the project's declared test script. The COMMAND is fixed here; what
                // it invokes is the project's own, which is the unavoidable boundary of testing
                // someone else's repository — see the WorkspaceAdapter remarks. It is bounded by a
                // timeout and run inside the mission workspace, never the live checkout.
                new CheckDefinition("node_test", "npm", "test --silent", 1200, true, "Project test script"),
                new CheckDefinition("node_build", "npm", "run build --silent", 900, true, "Project build script"),
            }),

        new WorkspaceAdapter("python", "1", "Python project",
            DetectWhen: new[] { "pyproject.toml", "requirements.txt", "setup.py" },
            Checks: new[]
            {
                new CheckDefinition("python_version", "python3", "--version", 30, true, "Interpreter probe"),
                new CheckDefinition("python_test", "python3", "-m pytest -q", 1200, true, "pytest suite"),
            }),
    };

    /// <summary>Adapters that apply to <paramref name="root"/>, in declaration order for determinism.</summary>
    public static IReadOnlyList<WorkspaceAdapter> DetectAll(string root) =>
        All.Where(a => a.Detects(root)).ToList();
}
