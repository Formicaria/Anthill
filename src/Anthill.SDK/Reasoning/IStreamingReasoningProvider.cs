namespace Anthill.SDK.Reasoning;

/// <summary>
/// v0.3.8.44 — a provider that can deliver the answer AS IT IS PRODUCED.
///
/// Additive by design: <see cref="IReasoningProvider.Send"/> remains the one contract every
/// provider meets, and streaming is a capability a caller may ASK about with a type test — the
/// same shape the capability catalog uses for tool calling. A provider that cannot stream is not
/// wrapped, faked, or chunk-simulated; the caller falls back to <c>Send</c> and the operator sees
/// one honest reply instead of a pretend trickle. Simulated streaming is the "Streaming claims"
/// lie the v0.3.8.42 truthfulness audit exists to forbid.
/// </summary>
public interface IStreamingReasoningProvider : IReasoningProvider
{
    /// <summary>
    /// Streams content deltas through <paramref name="onDelta"/> and returns the SAME final
    /// <see cref="ModelResponse"/> that <see cref="IReasoningProvider.Send"/> would have returned
    /// for this request — status classified identically, content equal to the concatenation of
    /// the deltas. The final response is the record; the deltas are presentation.
    ///
    /// Cancellation is the ambient <see cref="ModelCallScope"/> token, exactly as in
    /// <c>Send</c> — a caller that wants a disconnecting client to abort the model call binds the
    /// request's token into the scope before calling.
    /// </summary>
    ModelResponse SendStreaming(ModelRequest request, Action<string> onDelta, int retries = 2);
}
