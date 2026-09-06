namespace Anthill.Modules.Infrastructure;

/// <summary>
/// Everything the infrastructure module needs from the colony's configuration, handed over rather than
/// read. v3.8.7.
///
/// Eleven settings, which is what the survey found once it counted rather than assumed. Six are
/// infrastructure's own (<c>infrastructure_*</c>) and would have been meaningless in the core anyway; the rest
/// are ambient facts about where the process is running.
///
/// This is the same inversion <c>ReasoningProviderContext</c> performs for providers, and it exists
/// for the same reason: a module that reads <c>AnthillRuntime</c> is a module that references the
/// core, and then there is no module. The composition root builds one of these from the live
/// runtime and passes it in.
/// </summary>
/// <param name="DatabasePath">Fully resolved. The module does not know where the script directory
/// is and must not have to work it out.</param>
/// <param name="StopFileName">The INFRASTRUCTURE_STOP kill switch. Its presence halts every action.</param>
/// <param name="HealthTimeoutMs">Per-check ceiling, so one unreachable host cannot stall a sweep.</param>
/// <param name="NotificationsEnabled">Off by default; a infrastructure that cannot reach the network
/// should be quiet rather than noisy.</param>
public sealed record InfrastructureOptions(
    string DatabasePath,
    string StopFileName,
    int HealthTimeoutMs,
    bool NotificationsEnabled,
    string? SlackWebhook,
    string? DiscordWebhook,
    string? GenericWebhook,
    string ColonyVersion,
    string WorkspaceRootPath);
