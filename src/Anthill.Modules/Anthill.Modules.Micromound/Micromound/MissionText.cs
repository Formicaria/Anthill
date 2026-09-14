using Micromound.Protocol;

namespace Anthill.Modules.Micromound;

/// <summary>
/// The operator-readable account of what a mission will do, step by step.
///
/// A capability name alone hides the two things W3-06 put on the wire and that decide whether an
/// action ever counts as verified: how long the mission waits for the world to catch up before a
/// step, and what it then insists on observing. An approver who cannot see the postcondition is
/// approving an outcome they were never shown, so the approval description renders both. One
/// implementation, so the console and the approval never disagree about what a step says.
///
/// Numbers are rendered with the invariant culture on purpose: the device compares against the
/// value as written, and an approver in a comma-decimal locale must read the same number.
/// </summary>
public static class MissionText
{
    public static string DescribeSteps(IReadOnlyList<MissionStep> steps)
    {
        var lines = new List<string>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
            lines.Add($"\n  {i + 1}. {DescribeStep(steps[i])}");
        return string.Concat(lines);
    }

    public static string DescribeStep(MissionStep step)
    {
        var parts = new List<string> { string.IsNullOrEmpty(step.Op) ? "step" : step.Op };
        var what = string.IsNullOrEmpty(step.RoutineId) ? step.Capability : $"routine {step.RoutineId}";
        if (!string.IsNullOrEmpty(what)) parts.Add(what);
        if (step.Parameters.Count > 0)
            parts.Add("(" + string.Join(", ", step.Parameters.Select(p => $"{p.Key}={Num(p.Value)}")) + ")");
        if (!string.IsNullOrEmpty(step.Confirms)) parts.Add($"confirms {step.Confirms}");
        // settle_s waits BEFORE the step (Mission.cs), which is the order it is written in here.
        if (step.SettleSeconds > 0) parts.Add($"— after a {Num(step.SettleSeconds)}s settle");
        if (step.Expect is { } e)
        {
            var expect = $"— expects {e.Op} {Num(e.Value)}";
            if (e.Tolerance > 0) expect += $" ±{Num(e.Tolerance)}";
            if (!string.IsNullOrEmpty(e.Unit)) expect += $" {e.Unit}";
            parts.Add(expect);
        }
        return string.Join(" ", parts);
    }

    private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
