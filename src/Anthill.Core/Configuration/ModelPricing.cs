using System.Text.Json.Serialization;

namespace Anthill.Core.Configuration;

/// <summary>
/// What one provider's model costs, per million tokens, in the operator's currency. v0.3.8.90.
///
/// WHY THIS IS CONFIGURATION AND NOT A TABLE IN THE SOURCE. Prices change without warning, differ per
/// account, differ per region, and are zero for a local model the operator already paid for in
/// hardware. A rate compiled into this repository would be wrong for somebody on the day it shipped
/// and wrong for everybody eventually — and a stale rate presented as a measured cost is worse than
/// no cost at all, because an operator acts on a number and cannot act on an absence.
///
/// So the colony ships with NO prices. It records tokens, which it can measure, and converts them
/// only against a table the operator wrote down.
/// </summary>
/// <remarks>
/// The JSON names are explicit because <c>AnthillConfig.JsonOptions</c> sets no naming policy: every
/// key in `config.json` is snake_case because every property says so, and a positional record whose
/// parameters were left unannotated would silently want `InputPerMillion` from the operator's file.
/// </remarks>
public sealed record ModelPrice(
    [property: JsonPropertyName("input_per_million")] decimal InputPerMillion,
    [property: JsonPropertyName("output_per_million")] decimal OutputPerMillion);

/// <summary>One model call's usage, as the record layer read it back out of the store.</summary>
/// <param name="PromptTokens">Null means the provider reported nothing — NOT that it used none.</param>
public sealed record ModelCallUsage(string Provider, string Model, int? PromptTokens, int? CompletionTokens);

/// <summary>
/// The answer to "what did this run cost", including every way the answer is "we cannot say".
///
/// <see cref="Measured"/> false is a first-class outcome, not an error, and <see cref="Reason"/>
/// always names WHICH layer declined — an absent table, a silent provider, or a served model the
/// table does not cover. Those are three different operator actions and a single "unknown" would
/// force the operator to guess which one they are looking at.
/// </summary>
public sealed record PriceQuote(
    bool Measured,
    decimal Amount,
    string Currency,
    string Reason,
    IReadOnlyList<string> UnpricedModels)
{
    /// <summary>Rendered for the qualification record. Null when nothing was measured.</summary>
    public string? Rendered => Measured ? $"{Currency} {Amount:0.######}" : null;
}

/// <summary>
/// Tokens to money, and nothing else. v0.3.8.90.
///
/// R4's exit gate asks a live run to record cost in the operator's currency. Every other field on
/// that gate is assembled from something the colony already stores; this one had NO producer at all —
/// `ModelRouter` records prompt and completion tokens per call and nothing converted them. v0.3.8.89
/// shipped the recorder with cost as a declared gap rather than an assumed rate, and `PLAN.md` names
/// the fix in the same sentence it names the gap: *"a per-provider price table as operator
/// configuration — a small, separate change, and one that must not be done inside the recorder."*
///
/// This is that change, and it is deliberately a PURE FUNCTION over a table passed in rather than a
/// reader of <see cref="AnthillRuntime"/>. Two reasons, both learned here:
///   - v0.3.8.88 found that `AnthillRuntime.Initialize` is one-shot and overwrites 51 statics, so
///     anything that reads a static at the wrong moment reads the wrong value. A function that is
///     handed its table cannot have that bug.
///   - the recorder must stay an assembler over records the colony keeps. Pricing lives here; the
///     recorder asks a question and prints the answer.
///
/// THE RULE IT WILL NOT BREAK: it never prices a run PARTIALLY. If one served model has no entry,
/// the whole quote is unmeasured and says which model. A total that silently omits the expensive
/// model is a fabricated figure wearing a decimal point — and the operator-facing report is exactly
/// where this repository has decided a fabricated figure is worse than an absent one.
/// </summary>
public static class ModelPricing
{
    /// <summary>The wildcard a provider-wide entry uses: <c>"ollama/*"</c>.</summary>
    public const string AnyModel = "*";

    /// <summary>Canonical table key for a provider and model.</summary>
    public static string Key(string provider, string model) =>
        $"{(provider ?? "").Trim()}/{(model ?? "").Trim()}";

    /// <summary>
    /// The price for one provider/model, exact entry first and the provider-wide wildcard second.
    ///
    /// The wildcard exists for the local case and is the reason local runs are priceable at all:
    /// an operator running Ollama writes <c>"ollama/*": { "input_per_million": 0, ... }</c> and the
    /// run reports a measured zero. The colony does NOT assume that itself — "local models are free"
    /// is an operator's claim about their own electricity bill, not a fact this process can observe.
    /// </summary>
    public static ModelPrice? For(
        IReadOnlyDictionary<string, ModelPrice>? table, string provider, string model)
    {
        if (table is null || table.Count == 0) return null;

        foreach (var candidate in new[] { Key(provider, model), Key(provider, AnyModel) })
            foreach (var (key, price) in table)
                if (string.Equals(key.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
                    return price;

        return null;
    }

    /// <summary>
    /// Price a whole run, or say precisely why it cannot be priced.
    ///
    /// The order of the refusals is the order an operator can act on them: no table at all is a
    /// setup step, a silent provider is a provider fact they cannot change, and a missing entry is
    /// one line of configuration away. Each message names the gate that said no rather than leaving
    /// the operator to infer it from an empty field.
    /// </summary>
    public static PriceQuote Quote(
        IReadOnlyDictionary<string, ModelPrice>? table,
        string currency,
        IEnumerable<ModelCallUsage> calls)
    {
        var money = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim();
        var seen = (calls ?? Array.Empty<ModelCallUsage>()).ToList();

        if (seen.Count == 0)
            return new(false, 0m, money,
                "no model call was made in this mission, so there is nothing to price",
                Array.Empty<string>());

        // THE UNFIXABLE REFUSAL IS CHECKED FIRST, and the order is the message.
        //
        // A provider that reports nothing is UNKNOWN, not zero — the same rule the tokens field
        // holds. Summing absent usage to zero would turn "this provider does not report usage" into
        // "this run was free", and the second is a claim an operator would act on.
        //
        // This ran AFTER the empty-table check in the first draft, and its first live run showed why
        // that was wrong: a run with no table and a silent provider was told to configure
        // `model_pricing`. An operator who does that gets the same unmeasured field back, because
        // configuring prices cannot recover usage nobody recorded. The message named a gate that was
        // not the binding one — the exact defect these three refusals exist to avoid. When both are
        // true, the one the operator CANNOT clear is the honest answer, and it is one round trip
        // instead of two.
        var silent = seen
            .Where(c => c.PromptTokens is null && c.CompletionTokens is null)
            .Select(c => Key(c.Provider, c.Model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (silent.Count > 0)
            return new(false, 0m, money,
                "these providers served calls and reported no token usage, so the run cannot be "
              + "priced from measurement: " + string.Join(", ", silent)
              + ". Unknown usage is not zero usage",
                silent);

        if (table is null || table.Count == 0)
            return new(false, 0m, money,
                "no price table is configured. The runtime measured this run's tokens; converting "
              + "them to currency needs `model_pricing` in config.json, and a rate assumed here "
              + "would be a fabricated figure in an operator-facing report",
                Array.Empty<string>());

        var unpriced = seen
            .Where(c => For(table, c.Provider, c.Model) is null)
            .Select(c => Key(c.Provider, c.Model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (unpriced.Count > 0)
            return new(false, 0m, money,
                "the price table does not cover every model this run used: "
              + string.Join(", ", unpriced)
              + ". Pricing only the covered ones would report a total lower than the run's real "
              + "cost, which is worse than reporting none. Add an entry, or a provider-wide "
              + "`<provider>/*` entry",
                unpriced);

        var total = 0m;
        foreach (var call in seen)
        {
            var price = For(table, call.Provider, call.Model)!;
            total += (call.PromptTokens ?? 0) / 1_000_000m * price.InputPerMillion;
            total += (call.CompletionTokens ?? 0) / 1_000_000m * price.OutputPerMillion;
        }

        return new(true, total, money,
            $"priced from {seen.Count} model call(s) against the operator's configured "
          + "`model_pricing` table, at the tokens each call actually reported",
            Array.Empty<string>());
    }
}
