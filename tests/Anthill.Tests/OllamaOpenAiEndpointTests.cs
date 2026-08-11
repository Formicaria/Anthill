using System.Net;
using System.Text;
using Anthill.Core.Models;
using Xunit;
// `Task` in this project resolves to Anthill.Core.Domain.Task (the mission task), so the
// threading one must be named explicitly — the same disambiguation the other suites make.
using ThreadingTask = System.Threading.Tasks.Task;

namespace Anthill.Tests;

/// <summary>
/// v3.3.0 (ADR-006) — Ollama speaks OpenAI on the wire, proven against a stub server.
///
/// WHY A STUB RATHER THAN A LIVE OLLAMA: the operator's Ollama is frequently unreachable (it lives
/// on another host), and a test that only runs when it happens to be up is a test that protects
/// nothing on the days it matters. More importantly, a live call can only tell us *something
/// answered*; it cannot tell us WHAT WE SENT. The bug this guards against is silent — posting to
/// the wrong path, or nesting the tools array where the server ignores it — and in both cases a
/// live Ollama would happily return a normal-looking completion with no tool call in it, which is
/// indistinguishable from a model that chose not to use one.
///
/// So the stub records the request. That is the assertion that has teeth.
/// </summary>
public class OllamaOpenAiEndpointTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _host;
    private string _path = "";
    private string _body = "";

    public OllamaOpenAiEndpointTests()
    {
        var port = FreePort();
        _host = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_host}/");
        _listener.Start();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Answer exactly one request with <paramref name="reply"/>, recording what arrived.</summary>
    private ThreadingTask ServeOnce(string reply, int status = 200) => ThreadingTask.Run(() =>
    {
        var ctx = _listener.GetContext();
        _path = ctx.Request.Url?.AbsolutePath ?? "";
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            _body = reader.ReadToEnd();

        var bytes = Encoding.UTF8.GetBytes(reply);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    });

    public void Dispose() { try { _listener.Stop(); } catch { /* already down */ } }

    [Fact]
    public async ThreadingTask ItPostsToTheOpenAiCompatiblePath_NotApiGenerate()
    {
        var serving = ServeOnce("""{"choices":[{"message":{"content":"hi"}}]}""");
        var response = new OllamaClient("llama3.1:8b", _host).Send(ModelRequest.FromPrompt("hello"), retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("/v1/chat/completions", _path);
        Assert.True(response.Ok);
        Assert.Equal("hi", response.Content);
        Assert.Equal("ollama", response.Provider);
    }

    /// <summary>
    /// Roles travel as roles. On /api/generate they had to be flattened into one prompt string,
    /// which is the loss this move exists to undo.
    /// </summary>
    [Fact]
    public async ThreadingTask MessagesKeepTheirRoles_RatherThanBeingFlattened()
    {
        var serving = ServeOnce("""{"choices":[{"message":{"content":"ok"}}]}""");
        var request = new ModelRequest
        {
            Messages = new[]
            {
                new ModelMessage(ModelMessage.System, "you are terse"),
                new ModelMessage(ModelMessage.User, "hello"),
            },
        };
        new OllamaClient("llama3.1:8b", _host).Send(request, retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("\"role\":\"system\"", _body);
        Assert.Contains("\"role\":\"user\"", _body);
        Assert.Contains("you are terse", _body);
    }

    /// <summary>
    /// THE point of the whole move: a tool-capable local model is offered tools, in the OpenAI
    /// shape, on the wire. /api/generate had no channel for this at all — a local model could not
    /// ask to run anything, which put every self-improvement loop out of reach.
    /// </summary>
    [Fact]
    public async ThreadingTask AToolCapableLocalModel_IsOfferedToolsOnTheWire()
    {
        var serving = ServeOnce("""{"choices":[{"message":{"content":"ok"}}]}""");
        var request = ModelRequest.FromPrompt("list the repo") with
        {
            Tools = new[] { new ModelToolSpec("list_directory", "lists a directory",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}") },
        };
        // hermes: the reference local function-calling family
        new OllamaClient("hermes3:8b", _host).Send(request, retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("\"tools\"", _body);
        Assert.Contains("\"list_directory\"", _body);
        Assert.Contains("\"type\":\"function\"", _body);
    }

    /// <summary>
    /// And a model that cannot use tools is not offered them — negotiated at the seam, so no caller
    /// has to know which local model is loaded.
    /// </summary>
    [Fact]
    public async ThreadingTask AModelWithoutToolSupport_IsNotOfferedTools()
    {
        var serving = ServeOnce("""{"choices":[{"message":{"content":"ok"}}]}""");
        var request = ModelRequest.FromPrompt("list the repo") with
        {
            Tools = new[] { new ModelToolSpec("list_directory", "lists a directory", "{}") },
        };
        new OllamaClient("some-old-local-model", _host).Send(request, retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("\"tools\"", _body);
    }

    /// <summary>A tool call from a local model is read back as structure, not prose.</summary>
    [Fact]
    public async ThreadingTask AToolCallFromALocalModel_ComesBackAsStructure()
    {
        var serving = ServeOnce("""
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call_1","function":{"name":"list_directory","arguments":"{\"path\":\".\"}"}}]},
              "finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":12,"completion_tokens":4}}
            """);
        var response = new OllamaClient("hermes3:8b", _host).Send(ModelRequest.FromPrompt("go"), retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(response.Ok);                       // tool calls without prose are a SUCCESS
        Assert.Equal("list_directory", Assert.Single(response.ToolCalls).Name);
        Assert.Equal(16, response.Usage.TotalTokens);   // and usage now survives the trip
    }

    /// <summary>
    /// The diagnostic worth keeping: a 404 from Ollama nearly always means the model is not pulled,
    /// and saying so with the exact command is the difference between a two-second fix and an
    /// operator debugging their network.
    /// </summary>
    [Fact]
    public async ThreadingTask AMissingModel_StillSaysHowToPullIt()
    {
        var serving = ServeOnce("""{"error":"model not found"}""", status: 404);
        var response = new OllamaClient("not-pulled:70b", _host).Send(ModelRequest.FromPrompt("hi"), retries: 1);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ModelCallOutcome.NotAvailable, response.Status);
        Assert.Contains("ollama pull not-pulled:70b", response.Content);
    }
}
