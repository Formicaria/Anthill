using Anthill.Modules.Micromound;
using Micromound.Protocol;
using Xunit;

namespace Anthill.Tests.Micromound;

/// <summary>
/// THE APPROVER SEES WHAT THEY ARE APPROVING. Before this, an approval description listed capability
/// names and nothing else, so a step that waited three seconds and then insisted on a reading could
/// be approved by someone who saw "moisture". These pin the two additions and the number format.
/// </summary>
public class MissionTextTests
{
    [Fact]
    public void A_step_with_a_settle_and_a_postcondition_says_both_in_the_order_they_happen()
    {
        var step = new MissionStep
        {
            StepId = "s2", Op = "verify", Capability = "moisture", Confirms = "s1",
            SettleSeconds = 2.5,
            Expect = new StepExpectation { Op = "gte", Value = 30, Tolerance = 0.5, Unit = "pct" },
        };

        var text = MissionText.DescribeStep(step);

        Assert.Equal("verify moisture confirms s1 — after a 2.5s settle — expects gte 30 ±0.5 pct", text);
    }

    [Fact]
    public void A_bare_sense_step_says_only_what_it_is()
    {
        var text = MissionText.DescribeStep(new MissionStep { Op = "sense", Capability = "moisture" });
        Assert.Equal("sense moisture", text);
        Assert.DoesNotContain("settle", text, StringComparison.Ordinal);
        Assert.DoesNotContain("expects", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_and_routines_render_and_the_tolerance_is_omitted_when_exact()
    {
        var text = MissionText.DescribeStep(new MissionStep
        {
            Op = "act", RoutineId = "irrigate", Parameters = new() { ["seconds"] = 12 },
            Expect = new StepExpectation { Op = "eq", Value = 1, Unit = "closed" },
        });
        Assert.Equal("act routine irrigate (seconds=12) — expects eq 1 closed", text);
    }

    [Fact]
    public void Numbers_use_the_invariant_decimal_point_whatever_the_thread_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var text = MissionText.DescribeStep(new MissionStep { Op = "verify", Capability = "level", SettleSeconds = 1.25, Expect = new StepExpectation { Op = "lt", Value = 0.75 } });
            Assert.Contains("1.25s settle", text, StringComparison.Ordinal);
            Assert.Contains("lt 0.75", text, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Steps_are_numbered_from_one_on_their_own_lines()
    {
        var text = MissionText.DescribeSteps([
            new MissionStep { Op = "sense", Capability = "moisture" },
            new MissionStep { Op = "act", Capability = "valve", SettleSeconds = 3 },
        ]);
        Assert.Equal("\n  1. sense moisture\n  2. act valve — after a 3s settle", text);
    }
}
