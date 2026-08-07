using System.Text.Json;
using Anthill.SDK.Contracts;

namespace Anthill.SDK.Tools;

/// <summary>
/// Classify an exception that escaped a tool. v3.8.16 — moved out of
/// <c>ToolRegistry.ClassifyThrown</c>, which stays in the core as a delegating alias so its eleven
/// in-core call sites and two tests are untouched.
///
/// It had to move: every tool implementation calls it, and the implementations left for
/// <c>Anthill.Modules.Tools</c>. It is a pure function over an exception type returning an SDK enum,
/// so the SDK is where it always belonged — it was on <c>ToolRegistry</c> only because
/// <c>ToolRegistry</c> is where tools used to live.
/// </summary>
public static class ToolFailure
{
    /// <summary>
    /// The one type this cannot name directly.
    ///
    /// <c>HttpRequestException</c> lives in <c>System.Net.Http</c>, and naming it here would emit an
    /// assembly reference that <c>ModuleBoundaryTests.TheSdkDependsOnNothingOfOursAndNothingHeavy</c>
    /// forbids by name — because everything references the SDK, so anything the SDK depends on is
    /// inherited colony-wide.
    ///
    /// The honest options were to relax that guard for a carve-out it cannot express, or to match by
    /// name. Matching by name is uglier and strictly safer: the guard keeps meaning exactly what it
    /// says, and the cost is this comment plus a string compare on a path that only runs when a tool
    /// has already thrown.
    /// </summary>
    private const string HttpRequestExceptionName = "System.Net.Http.HttpRequestException";

    /// <summary>
    /// This is a fallback, not the intended path: a tool that knows why it failed should say so by
    /// returning a classified <see cref="ToolResult"/>, because the tool knows things the exception
    /// type does not. What this catches is the tool that threw without ever considering failure —
    /// and for that, the exception TYPE is the only honest evidence available.
    ///
    /// Anything unrecognised is an <see cref="FailureClass.InternalDefect"/> and therefore NOT
    /// retryable. Guessing "transient" for an unknown fault is how a deterministic crash becomes a
    /// retry storm.
    /// </summary>
    public static FailureClass Classify(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => FailureClass.Timeout,
        IOException => FailureClass.TransientProviderFailure,
        UnauthorizedAccessException => FailureClass.AuthorizationFailure,
        // The model chose the arguments, so a rejected argument is something it can fix and retry.
        ArgumentException or FormatException or JsonException => FailureClass.ValidationFailure,
        _ => IsHttpRequestFailure(error)
            ? FailureClass.TransientProviderFailure
            : FailureClass.InternalDefect,
    };

    /// <summary>
    /// Walks the base chain rather than comparing the exact type, so a derived HTTP failure is still
    /// recognised as transient. Cheap, and only reached once the switch above has already missed.
    /// </summary>
    private static bool IsHttpRequestFailure(Exception error)
    {
        for (var type = error.GetType(); type is not null; type = type.BaseType)
            if (type.FullName == HttpRequestExceptionName) return true;
        return false;
    }
}
