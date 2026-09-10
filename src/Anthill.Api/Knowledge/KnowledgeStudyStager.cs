using Anthill.Core.Configuration;

namespace Anthill.Api.Knowledge;

/// <summary>
/// STUDY BOUND KNOWLEDGE BASES ON A TIMER, WHEN THE OPERATOR HAS SAID SO. v0.3.8.156.
///
/// `.154` gave the operator a Study button and argued, in the button's own comment, for why it was a
/// button: "a button that silently enrolled a knowledge base into continuous work would be an
/// automation decision made by a click that did not look like one." That argument is unchanged. What
/// changed is that there is now a way to make that decision where a decision belongs — in the config
/// file, once, as `knowledge_auto_study: on` — instead of it being unavailable.
///
/// ONE PASS, ONE IMPLEMENTATION. This calls `KnowledgeSeeder.SeedOnce`, the same method the button
/// calls, which is why `SeedOnce` was written to return a typed result rather than log and return
/// void. A scheduled path that drifts from the manual one is two implementations of one rule, and
/// this repository has paid for that shape often enough to build against it by default.
///
/// THE SHAPE IS `UpdateStager`'s, deliberately and in every detail: a named background thread rather
/// than a hosted service, a startup grace delay so a colony's first seconds belong to the operator,
/// sleeps through the cancellation handle so a stop is prompt, a re-entrancy guard, and the DISABLED
/// CHECK INSIDE THE ONE-PASS METHOD rather than in the loop — which is what lets an operator action
/// and a test drive the same pass the timer drives.
///
/// AND THE CATCH IS DOUBLE, which `ColonyDirector` learned the hard way: an exception escaping a bare
/// background thread kills the process. The inner handler logs; the outer one leaves quietly, because
/// if even logging throws then the colony's database is gone and there is nothing useful left to do
/// from a thread nobody is watching.
/// </summary>
public static class KnowledgeStudyStager
{
    /// <summary>
    /// Six hours, matching `UpdateStager`. Knowledge changes when somebody publishes a document, not
    /// on a clock, so a tighter cadence would spend listing calls to learn nothing — and the button
    /// is there for the moment an operator knows something changed.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static int _running;

    public sealed record StudyPass(bool Ran, string Message, int Submitted, int Projects);

    /// <summary>Starts the background pass. Returns immediately; never throws into the caller.</summary>
    public static void Start(CancellationToken cancel = default)
    {
        var thread = new Thread(() =>
        {
            // The colony's first minutes belong to the operator, not to us.
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromMinutes(3))) return;

            while (!cancel.IsCancellationRequested)
            {
                try { StudyIfEnabled(cancel).GetAwaiter().GetResult(); }
                catch (Exception error)
                {
                    try { Console.Error.WriteLine($"[knowledge-study] pass failed: {error.Message}"); }
                    catch { break; }
                }
                if (cancel.WaitHandle.WaitOne(Interval)) return;
            }
        })
        { IsBackground = true, Name = "anthill-knowledge-study" };

        thread.Start();
    }

    /// <summary>
    /// One pass over every bound knowledge base. Public so an operator action and a test can drive
    /// exactly what the timer drives — the property that keeps the two from becoming two behaviours.
    /// </summary>
    public static async Task<StudyPass> StudyIfEnabled(CancellationToken cancel = default)
    {
        if (!string.Equals(AnthillRuntime.KnowledgeAutoStudy, "on", StringComparison.OrdinalIgnoreCase))
            return new StudyPass(false,
                $"knowledge_auto_study is '{AnthillRuntime.KnowledgeAutoStudy}'; nothing was studied.", 0, 0);

        if (!AnthillRuntime.Knowledge.Enabled)
            return new StudyPass(false, "knowledge is not enabled; nothing was studied.", 0, 0);

        if (Interlocked.Exchange(ref _running, 1) == 1)
            return new StudyPass(false, "a study pass is already running.", 0, 0);

        try
        {
            // A SNAPSHOT OF THE MAP, taken once. The pass can queue missions for several minutes, and
            // an operator rebinding a project halfway through should not have half a pass reading the
            // old map and half the new one.
            var bindings = AnthillRuntime.Knowledge.ProjectMap.Keys.ToList();
            if (bindings.Count == 0)
                return new StudyPass(true, "no project is bound to a knowledge base.", 0, 0);

            var submitted = 0;
            foreach (var project in bindings)
            {
                if (cancel.IsCancellationRequested) break;

                var scope = ApiHost.ResolveScopeForStudy(project);
                if (!scope.IsQueryable) continue;

                var result = await KnowledgeSeeder
                    .SeedOnce(ApiHost.Queen.Memory, ApiHost.Jobs, scope, project, cancel)
                    .ConfigureAwait(false);

                submitted += result.Submitted;
            }

            return new StudyPass(true,
                submitted == 0
                    ? $"nothing new to study across {bindings.Count} bound project(s)."
                    : $"queued {submitted} mission(s) across {bindings.Count} bound project(s).",
                submitted, bindings.Count);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
