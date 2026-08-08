using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Models;
using Anthill.SDK.Reasoning;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Which local model the colony runs on, and the end of the hardcoded one. v3.8.33.
///
/// `llama3.1:8b` was the built-in default in three places. On a host that had pulled anything else,
/// every ant call failed with `model 'llama3.1:8b' not found` while the console reported Ollama
/// reachable — because reachability and model presence are different questions and only the first
/// was surfaced. A default model name is a guess about someone else's machine.
///
/// The replacement rule: configured wins; exactly one installed is used; zero or several is REFUSED
/// with a reason that says what to do. Refusing on ambiguity is the same judgment
/// <c>PatchApply</c> makes when `old_content` matches twice — when the system cannot know which one
/// you meant, saying so beats picking. It matters more here, because an auto-pick could select an
/// embedding or draft model and the colony would not fail; it would run and reason badly, and record
/// that as evidence.
/// </summary>
public class LocalModelResolverTests
{
    private const string Host = "http://localhost:11434";

    private static LocalModelResolver.ModelLister Holding(params string[] models) => _ => models;
    private static LocalModelResolver.ModelLister Unreachable => _ => throw new HttpRequestException("connection refused");

    /// <summary>A configured model wins, and discovery is never even attempted — the common case must
    /// not depend on the host being up to answer.</summary>
    [Fact]
    public void AConfiguredModel_IsUsedWithoutAskingTheHost()
    {
        var asked = false;
        var choice = LocalModelResolver.Resolve("qwen2.5-coder:14b", Host,
            _ => { asked = true; return Array.Empty<string>(); });

        Assert.True(choice.Resolved);
        Assert.Equal("qwen2.5-coder:14b", choice.Model);
        Assert.Equal(ModelChoiceKind.Configured, choice.Kind);
        Assert.False(asked, "a configured model must not require the host to be reachable");
    }

    /// <summary>ANY model name works. The whole point: no allow-list, no known-family preference, no
    /// built-in favourite.</summary>
    [Theory]
    [InlineData("qwen2.5-coder:32b")]
    [InlineData("deepseek-r1:70b")]
    [InlineData("mistral-small:24b")]
    [InlineData("some-private-registry/custom-finetune:v3")]
    [InlineData("gemma3:27b")]
    public void AnyConfiguredModelName_IsAccepted(string model)
    {
        var choice = LocalModelResolver.Resolve(model, Host, Holding());

        Assert.True(choice.Resolved);
        Assert.Equal(model, choice.Model);
    }

    /// <summary>Nothing configured, one model installed: there was no choice to make, so make it.</summary>
    [Fact]
    public void WithNothingConfiguredAndOneModelInstalled_ThatModelIsUsed()
    {
        var choice = LocalModelResolver.Resolve("", Host, Holding("phi4:14b"));

        Assert.True(choice.Resolved);
        Assert.Equal("phi4:14b", choice.Model);
        Assert.Equal(ModelChoiceKind.SoleInstalled, choice.Kind);
    }

    /// <summary>
    /// THE decision. Several installed and none configured: refuse, and list them.
    ///
    /// The alternative — picking the biggest, or the first, or the most familiar name — would let a
    /// colony silently run on an embedding model. It would not error; it would produce weak output
    /// and record it as a real outcome.
    /// </summary>
    [Fact]
    public void WithNothingConfiguredAndSeveralInstalled_ItRefusesAndListsThem()
    {
        var choice = LocalModelResolver.Resolve("", Host, Holding("llama3.2:3b", "qwen2.5:7b", "nomic-embed-text"));

        Assert.False(choice.Resolved);
        Assert.Equal(ModelChoiceKind.AmbiguousInstalled, choice.Kind);
        Assert.Equal("", choice.Model);

        foreach (var installed in new[] { "llama3.2:3b", "qwen2.5:7b", "nomic-embed-text" })
            Assert.Contains(installed, choice.Reason);
        Assert.Equal(3, choice.Available.Count);
    }

    /// <summary>Nothing configured and nothing installed: refuse, and name the host.</summary>
    [Fact]
    public void WithNothingConfiguredAndNothingInstalled_ItRefusesAndNamesTheHost()
    {
        var choice = LocalModelResolver.Resolve("", Host, Holding());

        Assert.False(choice.Resolved);
        Assert.Equal(ModelChoiceKind.NoneInstalled, choice.Kind);
        Assert.Contains(Host, choice.Reason);
    }

    /// <summary>
    /// "Could not ask" is DISTINCT from "has none". They need different fixes — start Ollama, versus
    /// pull a model — and collapsing them would print the wrong instruction.
    /// </summary>
    [Fact]
    public void AnUnreachableHost_IsNotReportedAsHavingNoModels()
    {
        var choice = LocalModelResolver.Resolve("", Host, Unreachable);

        Assert.False(choice.Resolved);
        Assert.Equal(ModelChoiceKind.HostUnreachable, choice.Kind);
        Assert.NotEqual(ModelChoiceKind.NoneInstalled, choice.Kind);
        Assert.Contains(Host, choice.Reason);
    }

    /// <summary>Whitespace is not a configuration. A blank setting means "not chosen", not a model
    /// whose name is a space — which would go on the wire and 404.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSetting_CountsAsNotChosen(string? configured)
    {
        var choice = LocalModelResolver.Resolve(configured, Host, Holding("only:one"));

        Assert.Equal(ModelChoiceKind.SoleInstalled, choice.Kind);
    }

    /// <summary>A configured name is trimmed rather than sent with its whitespace.</summary>
    [Fact]
    public void AConfiguredNameIsTrimmed() =>
        Assert.Equal("qwen3:8b", LocalModelResolver.Resolve("  qwen3:8b  ", Host, Holding()).Model);

    /// <summary>Duplicates from the host collapse, so two tags of one model do not read as ambiguity
    /// that the operator cannot resolve by looking.</summary>
    [Fact]
    public void DuplicateNamesFromTheHost_DoNotCreateFalseAmbiguity()
    {
        var choice = LocalModelResolver.Resolve("", Host, Holding("gemma3:4b", "gemma3:4b", "  gemma3:4b "));

        Assert.Equal(ModelChoiceKind.SoleInstalled, choice.Kind);
        Assert.Equal("gemma3:4b", choice.Model);
    }

    /// <summary>An unresolved choice NEVER carries a model. A caller that ignored `Resolved` must not
    /// be handed a plausible-looking guess.</summary>
    [Fact]
    public void AnUnresolvedChoice_CarriesNoModel()
    {
        foreach (var choice in new[]
                 {
                     LocalModelResolver.Resolve("", Host, Holding()),
                     LocalModelResolver.Resolve("", Host, Holding("a:1", "b:2")),
                     LocalModelResolver.Resolve("", Host, Unreachable),
                 })
        {
            Assert.False(choice.Resolved);
            Assert.Equal("", choice.Model);
        }
    }

    /// <summary>Every refusal says what to DO. A reason that only states the problem sends the
    /// operator back to the source to work out the remedy.</summary>
    [Fact]
    public void EveryRefusal_NamesTheRemedy()
    {
        foreach (var choice in new[]
                 {
                     LocalModelResolver.Resolve("", Host, Holding()),
                     LocalModelResolver.Resolve("", Host, Holding("a:1", "b:2")),
                     LocalModelResolver.Resolve("", Host, Unreachable),
                 })
            Assert.True(
                choice.Reason.Contains("ollama_model", StringComparison.OrdinalIgnoreCase)
                || choice.Reason.Contains("pull", StringComparison.OrdinalIgnoreCase),
                $"refusal gives no remedy: {choice.Reason}");
    }

    // ---------------------------------------------------------------------------------------
    // The hardcoding must not come back.
    // ---------------------------------------------------------------------------------------

    private static string Root() => SourceText.RepoRoot();

    /// <summary>The three defaults that named a model are empty, and stay empty.</summary>
    [Fact]
    public void NoBuiltInDefaultModel_Anywhere()
    {
        Assert.Equal("", new AnthillConfig().OllamaModel);
        Assert.Equal("", ProviderCatalog.Ollama.DefaultModel);
    }

    /// <summary>
    /// THE GUARD. No source file may name a concrete local model tag.
    ///
    /// Scoped to `name:tag` shapes for the known local families, because that is what a default looks
    /// like when someone adds one back. The capability HINT table is exempt by design: it maps model
    /// FAMILIES (`llama3.1`, with no tag) to what they support, and it is consulted only as a
    /// fallback after asking the host — it constrains nothing and selects nothing.
    /// </summary>
    [Fact]
    public void NoSourceFile_HardcodesAConcreteModelTag()
    {
        var offenders = new List<string>();
        var tagged = new Regex(@"""(?:llama|qwen|mistral|gemma|phi|deepseek|codellama)[\w.\-]*:[\w.\-]+""",
            RegexOptions.IgnoreCase);

        foreach (var file in SourceText.ProductionFiles(Root()))
        {
            // CODE only. Both first-run hits were doc comments — one of them the paragraph explaining
            // why the hardcoded tag was removed. See SourceText.
            var text = SourceText.CodeOnly(File.ReadAllText(file));
            foreach (Match m in tagged.Matches(text))
            {
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(Root(), file)}:{line} {m.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A concrete model tag in source is a guess about the operator's machine — it is what made "
            + "`model 'llama3.1:8b' not found` the default experience. Resolve through "
            + "LocalModelResolver instead: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// An unresolved model must reach the caller as a REFUSAL naming the reason, never as an empty
    /// model string on the wire — which is what would 404 with no explanation.
    /// </summary>
    [Fact]
    public void AnUnresolvedModel_BecomesATypedRefusalCarryingTheReason()
    {
        var choice = LocalModelResolver.Resolve("", Host, Holding("a:1", "b:2"));
        var refusal = UnavailableProvider.NoModelChosen("ollama", choice.Reason);

        var response = refusal.Send(ModelRequest.FromPrompt("hello"));

        Assert.Equal(ModelCallOutcome.Error, response.Status);
        Assert.Contains("ollama_model", response.Content, StringComparison.OrdinalIgnoreCase);
    }
}
