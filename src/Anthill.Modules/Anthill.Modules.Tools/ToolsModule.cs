using Anthill.SDK.Common;
using Anthill.SDK.Events;
using Anthill.SDK.Modules;
using Anthill.SDK.Tools;

namespace Anthill.Modules.Tools;

/// <summary>
/// The tools that act on the machine, as a module. v3.8.16 — phase 5c step 4, the end of phase 5.
///
/// Phase 5 opened by stating what turned out to be the whole answer: the tool layer is COORDINATION,
/// not capability. <c>ITool</c>, <c>ToolRegistry</c>, <c>ToolAuthorization</c> and
/// <c>ToolInventory</c> are the dispatch vocabulary, and the core keeps every one of them. What
/// leaves is only the six classes that read a directory, read a file, write a file, run a command,
/// search the web and apply a patch — the parts that touch the world.
///
/// <c>SystemInfoTool</c> deliberately stayed in the core. It reports the native kernel, parallel
/// execution and FTS state: a window onto core internals rather than a capability, and extracting it
/// would have meant inventing an SDK contract whose only consumer is one tool's output dictionary.
///
/// REGISTRATION IS GATED HERE, and that is not a detail. The colony gates tools TWICE on purpose —
/// the composition root decides whether a tool is registered at all, then the tool re-checks when it
/// runs, so one that somehow reached the registry still refuses to act. <c>Queen.BuildToolRegistry</c>
/// used to hold the first gate. If this module registered everything unconditionally and left the
/// second check to catch it, the two would collapse into one and every existing test would still
/// pass.
/// </summary>
public sealed class ToolsModule : IAnthillModule
{
    private readonly IWorkspacePathGuard _guard;
    private readonly IToolRuntimeOptions _options;

    /// <param name="guard">
    /// The workspace containment check. Supplied by the composition root because the implementation
    /// reads the current mission's workspace through an ambient scope, and missions are core.
    /// </param>
    /// <param name="options">
    /// The capability gates, read live. Defaults to whatever <c>Anthill.Core</c> installed on
    /// <see cref="SafetyPolicy"/>, which in any real process is the live runtime.
    /// </param>
    public ToolsModule(IWorkspacePathGuard guard, IToolRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
        _options = options ?? SafetyPolicy.RequiredToolOptions;
    }

    public string Name => "tools";

    public string Version => "3.8.16";

    /// <summary>
    /// No I/O, per the <see cref="IAnthillModule"/> contract. Constructing a tool touches nothing —
    /// every one of them reads config and the filesystem only when it is CALLED, which is what makes
    /// registering them all at startup safe even for the ones that are switched off.
    /// </summary>
    public void Register(IModuleContext context)
    {
        var registered = new List<string>();

        void Offer(ITool tool)
        {
            context.RegisterTool(tool);
            registered.Add(tool.Name);
        }

        // Reading the filesystem at all.
        if (_options.FileToolsEnabled)
        {
            Offer(new DirectoryListTool(_guard, _options));
            Offer(new ReadTextFileTool(_guard, _options));
        }

        // Writing to it — a separate, narrower gate, and it was separate in the core too.
        if (_options.FileWritingEnabled)
            Offer(new WriteTextFileTool(_guard, _options));

        // Registered unconditionally, exactly as the core did. These three carry their own gates and
        // refuse at call time, and the /tools report is more useful when it can say "present but
        // disabled" rather than going silent — a missing tool and a switched-off one are different
        // operator problems with the same symptom.
        Offer(new WebSearchTool(_options));
        Offer(new ShellCommandTool(_guard, _options));
        Offer(new ApplyPatchTool(_guard, _options));

        context.Events.Publish(new ColonyEvent
        {
            EventType = EventTypes.ModuleRegistered,
            Message = $"Tools available: {string.Join(", ", registered)}.",
            Metadata = new Dictionary<string, object?>
            {
                ["module"] = Name,
                ["version"] = Version,
                ["tools"] = registered,
                ["file_tools_enabled"] = _options.FileToolsEnabled,
                ["file_writing_enabled"] = _options.FileWritingEnabled,
            },
        });
    }
}
