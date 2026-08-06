using System.Runtime.CompilerServices;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Anthill.Modules.Reasoning;

namespace Anthill.Tests;

/// <summary>
/// Gives the test assembly the same reasoning capability a real process has. v3.8.5.
///
/// Before phase 2b, <c>ModelRouter</c> built provider clients itself, so every test that touched a
/// router got real client behaviour for free. Now providers arrive from a module that a composition
/// root registers — and a test assembly is a process with no composition root, so without this the
/// whole suite would silently run against <c>UnavailableProvider</c>.
///
/// That is not a hypothetical. It is precisely how phase 2b was caught:
/// <c>ProviderTests.ModelRouter_KeyedProviderWithoutAConnection_FailsClosedWithoutAnyNetworkCall</c>
/// asserts that a keyed provider with no API key fails <c>ConfigError</c> without touching the
/// network — a check that lives INSIDE the real client — and it started reporting the generic
/// <c>Error</c> instead. The right fix is to give the tests a colony that has providers, not to
/// weaken the assertion.
///
/// Registering the probe as well as the factory matters for the same reason: before this release
/// <c>ModelRouter.CapabilitiesFor("ollama", ...)</c> consulted the Ollama cache unconditionally, so
/// a registered probe is what KEEPS that behaviour rather than changing it.
///
/// A module initializer rather than a fixture because it must run before the first test in any
/// class, and xUnit gives no assembly-wide "before everything" hook that is ordered against
/// parallel collections.
/// </summary>
internal static class ReasoningTestBootstrap
{
    [ModuleInitializer]
    internal static void Register()
    {
        ReasoningProviders.Register(new ReasoningProviderFactory());
        ReasoningProviders.RegisterProbe(new OllamaCapabilityProbe(AnthillRuntime.OllamaHost));
    }
}
