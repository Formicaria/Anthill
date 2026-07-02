using Anthill.Api;
using Anthill.Core.Configuration;
using Anthill.Core.Diagnostics;
using Anthill.Core.Orchestration;

// ----------------------------------------------------------------------------
// ANTHILL command-line entry point (v1.8.15.4).
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
    {
        // Parse optional flags before handing off to the web host. These are applied as env-var
        // overrides (ANTHILL_HOST/ANTHILL_PORT/ANTHILL_OLLAMA_HOST/ANTHILL_OLLAMA_MODEL) rather
        // than static field writes, because ApiHost.Run() calls AnthillRuntime.Initialize() as
        // its first step, which reloads everything from config.json — a direct field set here
        // would just get overwritten. Env vars are read inside ProjectConfig() and win over
        // config.json, so this works whether ANTHILL is started via the CLI, Docker, an LXC
        // profile, or a Windows Service.
        var apiArgs = rest.ToList();
        for (int i = 0; i < apiArgs.Count - 1; i++)
        {
            if (apiArgs[i] == "--host") { Environment.SetEnvironmentVariable("ANTHILL_HOST", apiArgs[i + 1]); apiArgs.RemoveRange(i, 2); i--; }
            else if (apiArgs[i] == "--port") { Environment.SetEnvironmentVariable("ANTHILL_PORT", apiArgs[i + 1]); apiArgs.RemoveRange(i, 2); i--; }
            else if (apiArgs[i] == "--ollama-host") { Environment.SetEnvironmentVariable("ANTHILL_OLLAMA_HOST", apiArgs[i + 1]); apiArgs.RemoveRange(i, 2); i--; }
            else if (apiArgs[i] == "--ollama-model") { Environment.SetEnvironmentVariable("ANTHILL_OLLAMA_MODEL", apiArgs[i + 1]); apiArgs.RemoveRange(i, 2); i--; }
        }
        return ApiHost.Run(apiArgs.ToArray());
    }

    case "--add-user":
    {
        // anthill --add-user <username> <password> [admin|coordinator]
        if (rest.Length < 2) { Console.Error.WriteLine("Usage: anthill --add-user <username> <password> [admin|coordinator]"); return 2; }
        var role = rest.Length >= 3 ? rest[2] : "coordinator";
        using var queen = NewQueen();
        var err = queen.Memory.CreateUser(rest[0], rest[1], role);
        if (err.Length > 0) { Console.Error.WriteLine($"Could not create user: {err}"); return 1; }
        Console.WriteLine($"Created user '{rest[0].ToLowerInvariant()}' with role '{role.ToLowerInvariant()}'.");
        return 0;
    }

    case "--set-password":
    {
        // anthill --set-password <username> <newpassword>  (recovery escape hatch)
        if (rest.Length < 2) { Console.Error.WriteLine("Usage: anthill --set-password <username> <newpassword>"); return 2; }
        using var queen = NewQueen();
        var err = queen.Memory.SetUserPassword(rest[0], rest[1]);
        if (err.Length > 0) { Console.Error.WriteLine($"Could not set password: {err}"); return 1; }
        Console.WriteLine($"Password updated for '{rest[0].ToLowerInvariant()}'. Active sessions for this user are unaffected until restart.");
        return 0;
    }

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
  anthill --api [--host <ip>] [--port <n>]          Launch the secured API + colony UI.
            [--ollama-host <url>] [--ollama-model <m>]  Binds 0.0.0.0:8713 by default (all
            [--autonomous]                              interfaces) — reachable at your machine's
                                                        LAN IP out of the box, container/LXC/
                                                        service-style. Pass --host 127.0.0.1 to
                                                        restrict to localhost only.
                                                        Use --ollama-host http://10.10.10.43:11434 for remote Ollama.
                                                        --autonomous starts the 24/7 Colony Director
                                                        at boot (requires autonomy_enabled=true in config).
  anthill --add-user <u> <p> [role]   Create an operator account (role: admin|coordinator).
  anthill --set-password <u> <p>      Reset an operator's password (lock-out recovery).
  anthill --selftest             Run the framework self-test harness.
  anthill --status               Print colony system status.
  anthill --config               Print effective configuration and safety gates.
  anthill --routes               Print model routing table.
  anthill --version              Print version.
  anthill --help                 Show this help.

Security:
  On first launch, open http://127.0.0.1:8713/ui and you will be prompted to create
  the admin password. No environment variable required.

  Roles: admin (full control) | coordinator (send missions + view logs only).
  Add accounts:  anthill --add-user <user> <pass> [admin|coordinator]
  Reset password: anthill --set-password <user> <newpass>   (lock-out recovery)

  Optional: set ANTHILL_API_TOKEN (>= 32 chars) for script/CI bearer access.

  Writes, shell, and web search are OFF by default (SAFE_LOCAL profile).
  Set ANTHILL_ENCRYPTION_KEY (32-byte base64/hex) to seal sensitive DB fields at rest.");
}
