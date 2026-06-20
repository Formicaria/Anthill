using Anthill.Api;
using Anthill.Core.Configuration;
using Anthill.Core.Diagnostics;
using Anthill.Core.Orchestration;

// ----------------------------------------------------------------------------
// ANTHILL command-line entry point (v1.8.0).
// Successor to `python -m anthill`: runs missions, the self-test harness, status
// views, or launches the secured API/UI host — all over the same Anthill.Core engine.
// ----------------------------------------------------------------------------

AnthillRuntime.Initialize();

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var rest = args.Skip(1).ToArray();

switch (command)
{
    case "--help" or "-h" or "help":
        PrintHelp();
        return 0;

    case "--version" or "-v":
        Console.WriteLine($"ANTHILL Core v{AnthillRuntime.Version} (.NET, schema v{AnthillRuntime.SchemaVersion})");
        return 0;

    case "--api":
        // Hand off to the shared API host so CLI and standalone API are identical.
        return ApiHost.Run(rest);

    case "--selftest":
    {
        using var queen = NewQueen();
        var report = SelfTest.Run(queen);
        Console.WriteLine(SelfTest.FormatReport(report));
        return report.Ok ? 0 : 1;
    }

    case "--status":
    {
        using var queen = NewQueen();
        Console.WriteLine(queen.FormatSystemStatus());
        return 0;
    }

    case "--config":
    {
        using var queen = NewQueen();
        Console.WriteLine(queen.FormatConfigStatus());
        return 0;
    }

    case "--routes":
    {
        using var queen = NewQueen();
        Console.WriteLine(queen.FormatModelRoutes());
        return 0;
    }

    case "--mission" or "--run":
    {
        var goal = string.Join(" ", rest).Trim();
        if (goal.Length == 0) { Console.Error.WriteLine("Provide a mission goal: anthill --mission \"<goal>\""); return 2; }
        using var queen = NewQueen();
        Console.WriteLine(queen.RunMission(goal));
        return 0;
    }

    default:
        // Treat any other input as a mission goal (e.g. `anthill "summarize my repo"`).
        var inlineGoal = string.Join(" ", args).Trim();
        using (var queen = NewQueen())
            Console.WriteLine(queen.RunMission(inlineGoal));
        return 0;
}

static Queen NewQueen() => new();

static void PrintHelp()
{
    Console.WriteLine($@"ANTHILL Core v{AnthillRuntime.Version} — visible swarm-intelligence harness (.NET edition)

Usage:
  anthill --mission ""<goal>""    Run a mission through the colony and print the result.
  anthill ""<goal>""              Shorthand for --mission.
  anthill --api                  Launch the secured local API + colony UI (http://{AnthillRuntime.ApiHost}:{AnthillRuntime.ApiPort}/ui).
  anthill --selftest             Run the framework self-test harness.
  anthill --status               Print colony system status.
  anthill --config               Print effective configuration and safety gates.
  anthill --routes               Print model routing table.
  anthill --version              Print version.
  anthill --help                 Show this help.

Security:
  The API refuses to start unless ANTHILL_API_TOKEN is set to a strong value
  (>= 32 chars, not the default placeholder). Generate one and export it first:

    export ANTHILL_API_TOKEN=""$(head -c 32 /dev/urandom | base64)""

  Writes, shell, and web search are OFF by default (SAFE_LOCAL profile).
  Set ANTHILL_ENCRYPTION_KEY (32-byte base64/hex) to seal sensitive DB fields at rest.");
}
