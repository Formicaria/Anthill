using Anthill.Core.Configuration;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Tokens to money, and every way the answer is "we cannot say". v0.3.8.90.
///
/// WHY THIS EXISTS. R4's exit gate asks a live run to record cost in the operator's currency, and
/// until this release cost was the one field on that gate with no producer at all. The risk in
/// closing it is not arithmetic — it is that a cost report is read by a human who then acts on the
/// number, so every way this can be WRONG is worse than the field staying empty. These tests are
/// mostly about the refusals, because the refusals are the safety property.
///
/// No globals are touched: <see cref="ModelPricing.Quote"/> takes its table as an argument, which is
/// deliberate (v0.3.8.88's one-shot bootstrap made static-reading code untestable in place) and is
/// why this class needs no collection and no roster snapshot.
/// </summary>
public class ModelPricingTests
{
    private static Dictionary<string, ModelPrice> Table(params (string Key, decimal In, decimal Out)[] rows)
    {
        var table = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, input, output) in rows) table[key] = new ModelPrice(input, output);
        return table;
    }

    private static ModelCallUsage Call(string provider, string model, int? prompt, int? completion) =>
        new(provider, model, prompt, completion);

    // -----------------------------------------------------------------------------------------------
    // The arithmetic, once — then every refusal
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A priced run reports the operator's currency and the operator's rates.
    ///
    /// 1,000,000 prompt tokens at 3.00 and 500,000 completion tokens at 15.00 is 3.00 + 7.50.
    /// Deliberately round numbers: this test exists to prove the units are per MILLION, which is how
    /// every provider publishes a price and therefore how an operator will type one in. A rate read
    /// as per-thousand would be off by a thousand and still look plausible on a small run.
    /// </summary>
    [Fact]
    public void APricedRun_ConvertsTokensAtThePerMillionRate()
    {
        var quote = ModelPricing.Quote(
            Table(("openai/gpt-4o", 3.00m, 15.00m)), "USD",
            new[] { Call("openai", "gpt-4o", 1_000_000, 500_000) });

        Assert.True(quote.Measured, quote.Reason);
        Assert.Equal(10.50m, quote.Amount);
        Assert.Equal("USD", quote.Currency);
        Assert.Equal("USD 10.5", quote.Rendered);
    }

    /// <summary>
    /// EVERY CALL IS PRICED AT ITS OWN MODEL'S RATE.
    ///
    /// A role can be served by two models in one mission — the priority route, then a fallback after
    /// a failure — and the recorder used to hold tokens summed per ROLE with a single provider/model
    /// taken from the first call. Pricing that sum would charge the expensive model's tokens at the
    /// cheap model's rate and produce a total that is confidently wrong. This is the test that pins
    /// the granularity, not the recorder's own.
    /// </summary>
    [Fact]
    public void TwoModelsInOneRun_ArePricedSeparately()
    {
        var quote = ModelPricing.Quote(
            Table(("openai/gpt-4o", 10m, 10m), ("openai/gpt-4o-mini", 1m, 1m)), "USD",
            new[]
            {
                Call("openai", "gpt-4o", 1_000_000, 0),
                Call("openai", "gpt-4o-mini", 1_000_000, 0),
            });

        Assert.True(quote.Measured, quote.Reason);
        Assert.Equal(11m, quote.Amount);
    }

    /// <summary>
    /// A provider-wide wildcard prices a whole provider, which is what makes a LOCAL run priceable.
    ///
    /// And it is the operator who says so. The colony does not assume Ollama is free: "free" is a
    /// claim about somebody's electricity and hardware, not a fact this process can observe. What it
    /// can do is let the operator write the claim down once, as `ollama/*`, and then report a
    /// MEASURED zero — which is a different and much stronger statement than an empty field.
    /// </summary>
    [Fact]
    public void AProviderWildcard_PricesEveryModelThatProviderServed()
    {
        var quote = ModelPricing.Quote(
            Table(("ollama/*", 0m, 0m)), "USD",
            new[]
            {
                Call("ollama", "llama3.1:8b", 5_000, 900),
                Call("ollama", "qwen2.5-coder:7b", 8_000, 1_200),
            });

        Assert.True(quote.Measured, quote.Reason);
        Assert.Equal(0m, quote.Amount);
        Assert.Equal("USD 0", quote.Rendered);
    }

    /// <summary>An exact entry beats the provider wildcard; otherwise a wildcard would flatten a table.</summary>
    [Fact]
    public void AnExactEntry_WinsOverTheWildcard()
    {
        var quote = ModelPricing.Quote(
            Table(("ollama/*", 0m, 0m), ("ollama/llama3.3:70b", 2m, 4m)), "USD",
            new[] { Call("ollama", "llama3.3:70b", 1_000_000, 1_000_000) });

        Assert.True(quote.Measured, quote.Reason);
        Assert.Equal(6m, quote.Amount);
    }

    /// <summary>
    /// The operator's spelling is not a filter. `OpenAI/GPT-4o` and `openai/gpt-4o` are one entry.
    ///
    /// A table that matched only one casing would report the run unpriced while the price sat in the
    /// file — the same failure as having no table, arrived at by a route the operator cannot see.
    /// </summary>
    [Fact]
    public void TheTableIsMatchedWithoutRegardToCase()
    {
        var quote = ModelPricing.Quote(
            Table(("OpenAI/GPT-4o", 1m, 1m)), "USD",
            new[] { Call("openai", "gpt-4o", 1_000_000, 0) });

        Assert.True(quote.Measured, quote.Reason);
        Assert.Equal(1m, quote.Amount);
    }

    // -----------------------------------------------------------------------------------------------
    // The refusals — each names the layer that declined
    // -----------------------------------------------------------------------------------------------

    /// <summary>No table is a SETUP problem, and the note says so in the operator's own vocabulary.</summary>
    [Fact]
    public void WithNoTable_TheRunIsUnpriced_AndTheNoteNamesTheConfiguration()
    {
        var quote = ModelPricing.Quote(
            new Dictionary<string, ModelPrice>(), "USD",
            new[] { Call("openai", "gpt-4o", 100, 100) });

        Assert.False(quote.Measured);
        Assert.Equal(0m, quote.Amount);
        Assert.Null(quote.Rendered);
        Assert.Contains("model_pricing", quote.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A PROVIDER THAT REPORTS NOTHING IS UNKNOWN, NOT FREE.
    ///
    /// The single most dangerous conversion in this file. Absent usage summed to zero, multiplied by
    /// any rate, is zero — a clean, confident, false "this run cost nothing". `V3Readiness` states the
    /// principle the whole repository runs on: unmeasured is not ready. Here: unmeasured is not zero.
    /// </summary>
    [Fact]
    public void AProviderThatReportedNoUsage_MakesTheRunUnpriceable_AtAnyRate()
    {
        var quote = ModelPricing.Quote(
            Table(("ollama/*", 0m, 0m)), "USD",
            new[] { Call("ollama", "llama3.1:8b", null, null) });

        Assert.False(quote.Measured,
            "a run whose provider reported no usage was priced. Zero tokens at any rate is zero "
          + "money, so this reports 'free' for a run nobody measured.");
        Assert.Contains("ollama/llama3.1:8b", quote.Reason, StringComparison.Ordinal);
        Assert.Contains("ollama/llama3.1:8b", quote.UnpricedModels);
    }

    /// <summary>
    /// A PARTIALLY PRICED RUN IS NOT PRICED. The rule that keeps the number honest.
    ///
    /// If one served model has no entry, pricing the rest produces a total LOWER than the run's real
    /// cost, presented with a decimal point and a currency symbol. An operator reading it has no way
    /// to tell it is partial. An absent figure prompts a question; an understated one does not.
    /// </summary>
    [Fact]
    public void OneUnpricedModel_MakesTheWholeRunUnpriced_AndNamesIt()
    {
        var quote = ModelPricing.Quote(
            Table(("openai/gpt-4o-mini", 0.15m, 0.60m)), "USD",
            new[]
            {
                Call("openai", "gpt-4o-mini", 1_000_000, 0),
                Call("anthropic", "claude-sonnet", 1_000_000, 0),
            });

        Assert.False(quote.Measured,
            "the run was priced while one of its models had no entry, so the reported total is "
          + "lower than what the run actually cost.");
        Assert.Equal(0m, quote.Amount);
        Assert.Contains("anthropic/claude-sonnet", quote.UnpricedModels);
        Assert.DoesNotContain("openai/gpt-4o-mini", quote.UnpricedModels);
    }

    /// <summary>A run with no model call is not a free run — there is simply nothing to price.</summary>
    [Fact]
    public void ARunWithNoModelCall_SaysThereIsNothingToPrice()
    {
        var quote = ModelPricing.Quote(
            Table(("ollama/*", 0m, 0m)), "USD", Array.Empty<ModelCallUsage>());

        Assert.False(quote.Measured);
        Assert.Contains("nothing to price", quote.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The refusals are DISTINGUISHABLE. Three different operator actions, three different notes.
    ///
    /// This is the assertion that stops the three collapsing into one polite "cost unavailable" as
    /// the messages get edited. A failure message that does not name the gate that said no makes the
    /// operator infer it, and this repository has decided that is not good enough.
    /// </summary>
    [Fact]
    public void TheThreeRefusals_DoNotShareAMessage()
    {
        var noTable = ModelPricing.Quote(new Dictionary<string, ModelPrice>(), "USD",
            new[] { Call("openai", "gpt-4o", 10, 10) }).Reason;

        var silent = ModelPricing.Quote(Table(("openai/*", 1m, 1m)), "USD",
            new[] { Call("openai", "gpt-4o", null, null) }).Reason;

        var missing = ModelPricing.Quote(Table(("openai/*", 1m, 1m)), "USD",
            new[] { Call("anthropic", "claude-sonnet", 10, 10) }).Reason;

        Assert.NotEqual(noTable, silent);
        Assert.NotEqual(silent, missing);
        Assert.NotEqual(noTable, missing);
    }

    /// <summary>An unset currency falls back to USD rather than rendering a bare number.</summary>
    [Fact]
    public void AnEmptyCurrency_FallsBackRatherThanRenderingANumberWithNoUnit()
    {
        var quote = ModelPricing.Quote(Table(("ollama/*", 0m, 0m)), "  ",
            new[] { Call("ollama", "llama3.1:8b", 10, 10) });

        Assert.Equal("USD", quote.Currency);
    }

    /// <summary>
    /// And the colony ships with NO prices.
    ///
    /// A default rate would be wrong for somebody on the day it shipped and wrong for everybody
    /// eventually, and a stale rate presented as a measurement is exactly the fabricated figure this
    /// whole design exists to refuse. The empty default is the feature.
    /// </summary>
    [Fact]
    public void TheShippedDefault_IsAnEmptyTable()
    {
        Assert.Empty(new AnthillConfig().ModelPricing);
        Assert.Equal("USD", new AnthillConfig().ModelPricingCurrency);
    }
}
