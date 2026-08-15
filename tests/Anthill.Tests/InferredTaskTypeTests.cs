using Anthill.Core.Agents;
using Anthill.SDK.Common;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// THE NORMALISER MUST NORMALISE TO SOMETHING VALID. v0.3.8.61.
///
/// The planner replaces a model-invented task_type with <see cref="TextUtil.InferTaskType"/>'s
/// answer for the role — that is the whole point of the substitution. For the six specialist
/// roles the inferrer had no case, fell through to "general", and every specialist contract
/// refuses "general": the repair produced a value as broken as the input, the task blocked, and
/// the medic spent the full repair bound on a planning defect no execution repair can reach.
///
/// Structural, so a role added tomorrow cannot reopen it: for EVERY role that has an execution
/// contract, the inferred default must be a type that contract supports.
/// </summary>
public class InferredTaskTypeTests
{
    [Fact]
    public void EveryContractedRole_InfersATypeItsOwnContractAccepts()
    {
        var problems = new List<string>();
        foreach (var (roleId, contract) in AntExecutionCatalog.Contracts)
        {
            var inferred = TextUtil.InferTaskType(roleId);
            if (!contract.SupportsTaskType(inferred))
                problems.Add($"{roleId}: infers '{inferred}', which its own contract refuses — "
                           + "the planner's normaliser would produce a task that blocks at dispatch");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
