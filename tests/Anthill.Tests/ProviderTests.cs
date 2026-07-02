using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Models;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Model provider connections (v1.8.4): encrypted API-key storage, the secret-free status
/// projection the console reads, and ModelRouter's fail-closed behaviour when a keyed provider
/// has no key configured. Runs fully offline — no test here makes a real network call to any
/// provider, so this suite is safe in sandboxes with no internet egress.
/// </summary>
public class ProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteMemory _memory;

    public ProviderTests()
    {
        AnthillRuntime.Initialize();
        _dir = Path.Combine(Path.GetTempPath(), "anthill_providers_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _memory = new SqliteMemory(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Catalog_HasOllamaPlusFourKeyedProviders()
    {
        Assert.Contains(ProviderCatalog.All, p => p.Id == "ollama" && !p.RequiresKey);
        var keyed = new[] { "openai", "anthropic", "perplexity", "openrouter" };
        foreach (var id in keyed)
        {
            Assert.Contains(id, ProviderCatalog.KeyedProviders);
            var info = ProviderCatalog.Find(id);
            Assert.NotNull(info);
            Assert.True(info!.RequiresKey);
            Assert.False(string.IsNullOrWhiteSpace(info.DefaultEndpoint));
            Assert.False(string.IsNullOrWhiteSpace(info.DefaultModel));
        }
        Assert.DoesNotContain("ollama", ProviderCatalog.KeyedProviders);
    }

    [Fact]
    public void ListProviderConnections_ReportsAllKeyedProvidersEvenWhenUnconfigured()
    {
        var connections = _memory.ListProviderConnections();
        Assert.Equal(ProviderCatalog.KeyedProviders.Count, connections.Count);
        Assert.All(connections, c => Assert.False((bool)c["configured"]!));
        Assert.All(connections, c => Assert.False(c.ContainsKey("api_key"))); // never projected
    }

    [Fact]
    public void UpsertProviderCredential_UnknownProvider_IsRejected()
    {
        var err = _memory.UpsertProviderCredential("not-a-real-provider", "sk-test", null, true, null);
        Assert.False(string.IsNullOrEmpty(err));

        // Ollama is a real provider but is keyless — connecting it as if it needed a key is also rejected.
        var err2 = _memory.UpsertProviderCredential("ollama", "sk-test", null, true, null);
        Assert.False(string.IsNullOrEmpty(err2));
    }

    [Fact]
    public void UpsertProviderCredential_RequiresKeyOnFirstConnect()
    {
        var err = _memory.UpsertProviderCredential("openai", apiKey: null, baseUrl: null, enabled: true, label: null);
        Assert.False(string.IsNullOrEmpty(err));
        Assert.Contains("API key", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderCredential_RoundTripsAndNeverLeaksTheRawKey()
    {
        const string secret = "sk-test-abcdef1234567890";
        var err = _memory.UpsertProviderCredential("openai", secret, "https://api.openai.com/v1/chat/completions", true, "primary");
        Assert.Equal("", err);

        // The decrypted key is only ever readable through the internal accessor ModelRouter uses.
        Assert.Equal(secret, _memory.GetDecryptedApiKey("openai"));

        // The console-facing projection never carries the secret in any form.
        var conn = _memory.ListProviderConnections().Single(c => (string)c["provider"]! == "openai");
        Assert.True((bool)conn["configured"]!);
        Assert.DoesNotContain(conn.Values, v => v is string s && s.Contains(secret));
        Assert.Equal("primary", (string?)conn["label"]);
    }

    [Fact]
    public void UpsertProviderCredential_BlankKeyOnUpdate_KeepsExistingKeyButUpdatesOtherFields()
    {
        const string secret = "sk-keep-me";
        Assert.Equal("", _memory.UpsertProviderCredential("anthropic", secret, null, true, null));

        // Second call omits the key and changes only the base URL — the stored key must survive.
        var err = _memory.UpsertProviderCredential("anthropic", apiKey: null, baseUrl: "https://custom.example/v1/messages", enabled: true, label: "renamed");
        Assert.Equal("", err);

        Assert.Equal(secret, _memory.GetDecryptedApiKey("anthropic"));
        var conn = _memory.ListProviderConnections().Single(c => (string)c["provider"]! == "anthropic");
        Assert.Equal("https://custom.example/v1/messages", (string?)conn["base_url"]);
        Assert.Equal("renamed", (string?)conn["label"]);
    }

    [Fact]
    public void DeleteProviderCredential_RemovesTheConnection()
    {
        Assert.Equal("", _memory.UpsertProviderCredential("perplexity", "pplx-test", null, true, null));
        Assert.NotNull(_memory.GetDecryptedApiKey("perplexity"));

        _memory.DeleteProviderCredential("perplexity");

        Assert.Null(_memory.GetDecryptedApiKey("perplexity"));
        var conn = _memory.ListProviderConnections().Single(c => (string)c["provider"]! == "perplexity");
        Assert.False((bool)conn["configured"]!);
    }

    [Fact]
    public void DisabledConnection_IsNotReturnedByGetDecryptedApiKey()
    {
        Assert.Equal("", _memory.UpsertProviderCredential("openrouter", "or-test", null, enabled: false, label: null));
        // A stored-but-disabled key must not be handed to ModelRouter.
        Assert.Null(_memory.GetDecryptedApiKey("openrouter"));
        // It still shows up as configured (the key exists), just disabled — so the console can
        // offer a "re-enable" action rather than losing the key on a toggle.
        var conn = _memory.ListProviderConnections().Single(c => (string)c["provider"]! == "openrouter");
        Assert.True((bool)conn["configured"]!);
        Assert.False((bool)conn["enabled"]!);
    }

    [Fact]
    public void SetProviderVerification_RecordsTestOutcome()
    {
        Assert.Equal("", _memory.UpsertProviderCredential("openai", "sk-verify-test", null, true, null));
        _memory.SetProviderVerification("openai", ok: true, message: "OK");

        var conn = _memory.ListProviderConnections().Single(c => (string)c["provider"]! == "openai");
        Assert.True((bool)conn["last_verify_ok"]!);
        Assert.Equal("OK", (string?)conn["last_verify_message"]);
        Assert.NotNull(conn["last_verified_at"]);
    }

    [Fact]
    public void ModelRouter_KeyedProviderWithoutAConnection_FailsClosedWithoutAnyNetworkCall()
    {
        var router = new ModelRouter(_memory);
        var client = router.GetClientForProvider("openai");
        var result = client.Generate("say hi", retries: 1);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("API key not configured", result);
    }

    [Fact]
    public void ModelRouter_AnthropicWithoutAConnection_FailsClosedWithoutAnyNetworkCall()
    {
        var router = new ModelRouter(_memory);
        var client = router.GetClientForProvider("anthropic");
        var result = client.Generate("say hi", retries: 1);
        Assert.StartsWith("ERROR:", result);
        Assert.Contains("API key not configured", result);
    }

    // Regression coverage for a real bug hit in production: a "Base URL" override saved as just
    // the conventional host+version prefix (e.g. "https://api.openai.com/v1", exactly how OpenAI's
    // own SDKs define base_url) was sent to the provider as the literal request URL with no path
    // appended, so every call 404'd. NormalizeEndpoint must accept both the bare-prefix form and
    // the full-path form, with or without a trailing slash.
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    public void OpenAiCompatibleClient_NormalizeEndpoint_AcceptsBarePrefixOrFullPath(string input, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleClient.NormalizeEndpoint(input));
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/messages")]
    public void AnthropicClient_NormalizeEndpoint_AcceptsBarePrefixOrFullPath(string input, string expected)
    {
        Assert.Equal(expected, AnthropicClient.NormalizeEndpoint(input));
    }

    [Fact]
    public void AnthropicClient_NormalizeEndpoint_FallsBackToDefaultWhenNoOverrideGiven()
    {
        Assert.Equal("https://api.anthropic.com/v1/messages", AnthropicClient.NormalizeEndpoint(null));
        Assert.Equal("https://api.anthropic.com/v1/messages", AnthropicClient.NormalizeEndpoint(""));
        Assert.Equal("https://api.anthropic.com/v1/messages", AnthropicClient.NormalizeEndpoint("   "));
    }
}
