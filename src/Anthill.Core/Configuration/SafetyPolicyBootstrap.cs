using System.Runtime.CompilerServices;
using Anthill.Core.Security;
using Anthill.Core.Tools;
using Anthill.SDK.Common;

namespace Anthill.Core.Configuration;

/// <summary>
/// Installs the core's live settings readers into the SDK's safety helpers. v3.8.12.
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
    [ModuleInitializer]
    internal static void Install() => SafetyPolicy.Configure(SsrfRuntime.Live, ToolRuntime.Live);
}
