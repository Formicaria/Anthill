using System.Runtime.CompilerServices;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.SDK.Common;

namespace Anthill.Core.Configuration;

/// <summary>
/// Installs the core's live settings readers into the SDK's safety helpers. v3.8.12, extended in
/// v3.8.15 with the tool-definition policy.
///
/// WHY A MODULE INITIALIZER RATHER THAN A COMPOSITION-ROOT CALL. <c>UrlSafety</c> and
/// <c>Validation</c> are static, and plenty of code reaches them without building a colony first —
/// <c>SelfTest</c>, <c>PheromoneEngine</c>, <c>Queen.Views</c>, and most of all the test suite, which
/// calls <c>Validation.ValidateSafePatchPath</c> and <c>UrlSafety.IsBlockedOutboundUrl</c> directly
/// with no host in sight. A <c>Configure</c> call at startup would have left every one of those
/// paths reading the SDK's built-in fallbacks instead of <c>AnthillRuntime</c>, and since the
/// fallbacks are identical at rest, nothing would have failed — the divergence would only appear
/// once an operator or a test changed a setting and the guard ignored it. That is the same shape of
/// silent wrong-green that v3.8.11 was written to avoid.
///
/// The runtime guarantees this runs before any other code in this assembly, so there is no ordering
/// question to get wrong. <c>ReasoningTestBootstrap</c> already uses the mechanism for the same
/// class of problem.
/// </summary>
internal static class SafetyPolicyBootstrap
{
    /// <summary>
    /// v3.8.15 adds <see cref="ToolDefinitionPolicy"/> for the same reason and with the same
    /// hazard: <c>UserDefinedToolTests</c> validates definitions without a colony, and an
    /// uninstalled policy would silently check them against the SDK's mirrored copy of the core's
    /// tables rather than the tables themselves.
    /// </summary>
    // CA2255 says ModuleInitializer is "only intended for application code or advanced source
    // generator scenarios". This is the advanced case and the suppression is deliberate rather than
    // noise-silencing: the alternative is a composition root calling Install(), and MOST CALLERS
    // NEVER BUILD ONE. UserDefinedToolTests validates definitions with no colony present, and an
    // uninstalled policy would silently check them against the SDK's mirrored copy of the core's
    // tables instead of the tables themselves — a check that passes while measuring the wrong thing,
    // which is the exact defect class this repository has spent a release cycle removing.
    //
    // Suppressed at the declaration with the reason attached, so the build is clean and the decision
    // is readable. A warning every build teaches people to ignore warnings.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "Deliberate: the safety policy must be installed for callers that never construct a colony.")]
    [ModuleInitializer]
    internal static void Install() =>
        SafetyPolicy.Configure(SsrfRuntime.Live, ToolRuntime.Live, ToolDefinitionPolicy.Live);
}
