using System.Text.RegularExpressions;
using Anthill.Core.Configuration;
using Anthill.Core.Memory;
using Anthill.Core.Security;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// The window before the first administrator exists. v0.3.8.91.
///
/// THE CHAIN THIS CLOSES. A fresh install binds `0.0.0.0:8713` — every shipped safety profile forces
/// it — and `/auth/setup` was gated on `CountUsers() == 0` and nothing else, so on a server or LXC
/// the administrator account belonged to whoever reached the port first. `operator_shell_enabled`
/// shipped `true`, and its own comment calls it host command execution for administrators. Reach the
/// port, win the race, open a shell.
///
/// Underneath it, a second defect: the zero-user question was asked OUTSIDE the insert, so two
/// simultaneous setup requests with different usernames could both see zero users and both create an
/// administrator.
///
/// `DEPLOYMENT.md` argued the real boundary was "the operator login, not network isolation". True
/// from the second account onward; false for exactly this window, where no login exists yet to be
/// the boundary. That sentence is corrected in the same release.
/// </summary>
public class FirstRunSecurityTests : IDisposable
{
    private readonly string _dir;

    public FirstRunSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anthill-firstrun-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
        SetupAuthority.ResetForTests();
    }

    public void Dispose()
    {
        SetupAuthority.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // -----------------------------------------------------------------------------------------------
    // Who may create the first administrator
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A LOOPBACK bind needs no token: reaching the port already proves local access.
    ///
    /// This is not laziness about the desktop case, it is what the bind means. The Windows desktop
    /// app forces `127.0.0.1`, and sending its user hunting for a token file would be ceremony that
    /// protects nothing — nobody off-box can reach the endpoint to begin with.
    /// </summary>
    [Fact]
    public void OnALoopbackBind_NoTokenIsMinted()
    {
        var secret = SetupAuthority.Arm("127.0.0.1", forceRequired: false, usersExist: false, workspaceRoot: _dir);

        Assert.Equal("", secret);
        Assert.False(SetupAuthority.SecretRequired);
        Assert.Equal(SetupAdmission.Admitted, SetupAuthority.Admit(null));
        Assert.False(File.Exists(Path.Combine(_dir, SetupAuthority.SecretFileName)));
    }

    /// <summary>
    /// A NETWORK bind mints one, and setup without it is refused.
    ///
    /// This is the fresh-server case and the one the whole class exists for.
    /// </summary>
    [Fact]
    public void OnANetworkBind_ATokenIsMintedAndRequired()
    {
        var secret = SetupAuthority.Arm("0.0.0.0", forceRequired: false, usersExist: false, workspaceRoot: _dir);

        Assert.NotEqual("", secret);
        Assert.True(secret.Length >= 20, "the bootstrap secret is short enough to be worth guessing");
        Assert.True(SetupAuthority.SecretRequired);

        Assert.Equal(SetupAdmission.SecretRequired, SetupAuthority.Admit(null));
        Assert.Equal(SetupAdmission.SecretRequired, SetupAuthority.Admit("   "));
        Assert.Equal(SetupAdmission.SecretWrong, SetupAuthority.Admit("not-the-token"));
        Assert.Equal(SetupAdmission.Admitted, SetupAuthority.Admit(secret));

        var file = Path.Combine(_dir, SetupAuthority.SecretFileName);
        Assert.True(File.Exists(file), "a service operator reads the token from this file");
        Assert.Contains(secret, File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RULE IS THE BIND, NOT THE CALLER'S ADDRESS — the reverse-proxy trap, stated as a test.
    ///
    /// A rule written on the remote IP would be defeated by any reverse proxy, because every request
    /// then arrives from loopback. `Admit` takes no address at all, which is what makes that mistake
    /// unavailable rather than merely avoided.
    /// </summary>
    [Fact]
    public void AdmissionDoesNotConsiderTheCallersAddress()
    {
        var admit = typeof(SetupAuthority).GetMethod(nameof(SetupAuthority.Admit))!;

        Assert.Single(admit.GetParameters());
        Assert.Equal(typeof(string), Nullable.GetUnderlyingType(admit.GetParameters()[0].ParameterType)
                                     ?? admit.GetParameters()[0].ParameterType);
    }

    /// <summary>And the proxy shape the bind rule cannot see has an explicit override.</summary>
    [Fact]
    public void AnOperatorCanForceTheTokenOnALoopbackBind()
    {
        var secret = SetupAuthority.Arm("127.0.0.1", forceRequired: true, usersExist: false, workspaceRoot: _dir);

        Assert.NotEqual("", secret);
        Assert.Equal(SetupAdmission.SecretRequired, SetupAuthority.Admit(null));
    }

    /// <summary>
    /// A colony that already has an administrator mints nothing.
    ///
    /// Otherwise every restart of a live installation would drop a credential file on disk that
    /// opens nothing — a secret with no lock, which is a liability and not a control.
    /// </summary>
    [Fact]
    public void WhenAnAdministratorAlreadyExists_NoTokenIsMinted()
    {
        Assert.Equal("", SetupAuthority.Arm("0.0.0.0", forceRequired: false, usersExist: true, workspaceRoot: _dir));
        Assert.False(File.Exists(Path.Combine(_dir, SetupAuthority.SecretFileName)));
    }

    /// <summary>
    /// The token is SINGLE USE, and spending it deletes the file.
    ///
    /// A bootstrap credential that survives bootstrap is a permanent backdoor with a friendly name.
    /// </summary>
    [Fact]
    public void ConsumingTheToken_RetiresItPermanently()
    {
        var secret = SetupAuthority.Arm("0.0.0.0", forceRequired: false, usersExist: false, workspaceRoot: _dir);
        Assert.Equal(SetupAdmission.Admitted, SetupAuthority.Admit(secret));

        SetupAuthority.Consume();

        Assert.False(SetupAuthority.SecretRequired);
        Assert.Equal(SetupAdmission.AlreadyComplete, SetupAuthority.Admit(secret));
        Assert.Equal(SetupAdmission.AlreadyComplete, SetupAuthority.Admit(null));
        Assert.False(File.Exists(Path.Combine(_dir, SetupAuthority.SecretFileName)));
    }

    // -----------------------------------------------------------------------------------------------
    // Exactly one first administrator
    // -----------------------------------------------------------------------------------------------

    private SqliteMemory Memory() => new(Path.Combine(_dir, $"users-{Guid.NewGuid():N}.db"));

    [Fact]
    public void TheFirstAdministrator_IsCreatedOnce_AndTheSecondAttemptIsRefused()
    {
        using var memory = Memory();

        var first = memory.CreateInitialAdministrator("owner", "correct horse battery");
        Assert.Equal(InitialAdminOutcome.Created, first.Outcome);
        Assert.Equal("", first.Error);

        var second = memory.CreateInitialAdministrator("someone-else", "another password entirely");
        Assert.Equal(InitialAdminOutcome.AlreadyInitialised, second.Outcome);
        Assert.Equal(1, memory.CountUsers());
    }

    /// <summary>
    /// TWO SIMULTANEOUS SETUP REQUESTS PRODUCE EXACTLY ONE ADMINISTRATOR.
    ///
    /// The defect this pins is not theoretical: `CountUsers()` was asked by the endpoint and
    /// `CreateUser` inserted afterwards, with nothing spanning the two. Different usernames meant no
    /// primary-key collision to save it, so both requests inserted an admin.
    ///
    /// Sixteen threads released together rather than two, because a two-thread race reproduces this
    /// only occasionally and a guard that catches the bug one run in ten is not a guard.
    /// </summary>
    [Fact]
    public void ConcurrentSetupAttempts_CreateExactlyOneAdministrator()
    {
        using var memory = Memory();

        const int racers = 16;
        var start = new ManualResetEventSlim(false);
        var outcomes = new InitialAdminOutcome[racers];

        var threads = Enumerable.Range(0, racers).Select(i => new Thread(() =>
        {
            start.Wait();
            outcomes[i] = memory.CreateInitialAdministrator($"admin{i}", "a sufficiently long password").Outcome;
        })).ToList();

        foreach (var thread in threads) thread.Start();
        start.Set();
        foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(30));

        var created = outcomes.Count(o => o == InitialAdminOutcome.Created);

        Assert.Equal(1, created);
        Assert.Equal(1, memory.CountUsers());
        Assert.Equal(racers - 1, outcomes.Count(o => o == InitialAdminOutcome.AlreadyInitialised));
    }

    /// <summary>Validation still refuses, and refuses as REJECTED rather than as a lost race.</summary>
    [Fact]
    public void AWeakPassword_IsRejectedWithoutConsumingTheFirstAdministratorSlot()
    {
        using var memory = Memory();

        Assert.Equal(InitialAdminOutcome.Rejected, memory.CreateInitialAdministrator("owner", "short").Outcome);
        Assert.Equal(0, memory.CountUsers());
        Assert.Equal(InitialAdminOutcome.Created,
            memory.CreateInitialAdministrator("owner", "a sufficiently long password").Outcome);
    }

    // -----------------------------------------------------------------------------------------------
    // The capability at the end of the chain
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The operator terminal ships OFF.
    ///
    /// It is arbitrary command execution on the host, which its own configuration comment has always
    /// said. Shipping it on meant the account created in the window above came with a shell. An
    /// operator who wants it turns it on, like every other capability that reaches outside the
    /// colony — and an existing `config.json` carrying `true` keeps it, because the raw overlay wins
    /// over the profile.
    /// </summary>
    [Fact]
    public void TheOperatorTerminal_ShipsOff_InTheDefaultsAndInEveryProfile()
    {
        Assert.False(new AnthillConfig().OperatorShellEnabled);

        foreach (var profile in new[] { "SAFE_LOCAL", "RESEARCH_LOCAL", "POWER_USER" })
        {
            var config = new AnthillConfig { OperatorShellEnabled = true };
            AnthillConfig.ApplySafetyProfile(config, profile);
            Assert.False(config.OperatorShellEnabled,
                $"the {profile} profile leaves the operator terminal on. It is host command "
              + "execution; it belongs with patch application and the shell tool, which this same "
              + "method forces off.");
        }
    }

    // -----------------------------------------------------------------------------------------------
    // No control decision reads prose
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// THE APPROVE-AND-APPLY PATH DOES NOT BRANCH ON ENGLISH. v0.3.8.91.
    ///
    /// It used to decide whether a patch had landed with
    /// `result.Contains("applied") &amp;&amp; !result.Contains("not applied")`. Three refusal sentences
    /// satisfy that — including "Patch cannot be applied because status is rejected", which made a
    /// REJECTED patch report success, return HTTP 200 and fire a real `git commit` over a file
    /// nothing had written.
    ///
    /// A source assertion, and deliberately so: the property is "this decision is not made from a
    /// string", which has no runtime input that demonstrates its absence. It is paired with the
    /// typed `PatchApplyResult` that makes the correct decision available.
    /// </summary>
    [Fact]
    public void ApproveAndApply_MakesNoDecisionFromAMessageString()
    {
        var file = Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "Queen.Views.cs");
        var code = SourceText.CodeOnly(File.ReadAllText(file));

        var start = code.IndexOf("public (bool Ok, string Message) ApproveAndApplyPatch", StringComparison.Ordinal);
        Assert.True(start >= 0, "ApproveAndApplyPatch has moved or been renamed.");

        var end = code.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        var body = end > start ? code[start..end] : code[start..];

        Assert.DoesNotContain(".Contains(\"applied\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".Contains(\"approved\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ApplyApprovedPatchTyped", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A VERIFICATION FAULT BLOCKS PROMOTION. v0.3.8.91.
    ///
    /// The catch around patch-set verification logged `patch_set_verification_faulted` and returned,
    /// leaving `DeterministicBlock` null — and `ApplyUnderBypass`'s first gate is exactly that field.
    /// So a fault in materialisation, workspace scope, the evidence store or revision registration
    /// let a Bypass conversation write an unverified patch to the operator's tree. Failed to run is
    /// not the same as ran and passed.
    ///
    /// A source assertion because the fault is a crash in a dependency, and manufacturing one at
    /// runtime would mean injecting a failure into the materialiser — worth doing, and named in
    /// PLAN.md as part of the crash-injection work rather than faked here.
    /// </summary>
    [Fact]
    public void AFaultedVerification_LeavesADeterministicBlock()
    {
        var file = Path.Combine(SourceText.RepoRoot(),
            "src", "Anthill.Core", "Orchestration", "ExecutionService.cs");
        var code = SourceText.CodeOnly(File.ReadAllText(file));

        var marker = code.IndexOf("patch_set_verification_faulted", StringComparison.Ordinal);
        Assert.True(marker >= 0, "the verification-fault path has moved; this guard reads it by its event name.");

        // The catch block, read backwards from the event to the `catch` that owns it.
        var open = code.LastIndexOf("catch (Exception", marker, StringComparison.Ordinal);
        Assert.True(open >= 0, "the fault event is no longer inside a catch block.");

        var block = code[open..marker];

        Assert.True(Regex.IsMatch(block, @"DeterministicBlock\s*\?\?="),
            "the verification-fault catch no longer sets task.DeterministicBlock. Without it "
          + "ApplyUnderBypass's first gate passes and a patch nothing verified can be written to the "
          + "operator's tree under a Bypass conversation.");
    }
}
