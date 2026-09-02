# The guard hierarchy

*Written down at v0.3.8.112, R0's last item. Enforced by `GuardHierarchyTests`.*

This repository defends its decisions with tests, and roughly a third of them are *guards*: tests
whose subject is the tree itself rather than a behaviour. They exist because the same defects keep
recurring and a comment cannot refuse a commit.

A guard is only worth what it can actually see. This is the order to reach for, strongest first, and
the reasons are failures this project has already paid for.

## 1. A runtime black-box test

Run the real thing through the real composition root and assert what came out. Nothing else can be
wrong about what the code *means* — it observes what the code *does*.

Prefer this always. It is the only level at which "the feature works" is the thing being asserted.

## 2. A typed registry

When the property is about a SET — every role has a contract, every capability is granted or
recorded as withheld, every tool is registered — put the set in a typed structure and range over it.
The compiler then carries half the guard: a renamed member does not silently stop being checked, it
stops compiling.

`CapabilityDeclarationTests` and `AntExecutionCatalog.Contracts` are the shape.

## 3. Compiled inspection

Reflection over the built assemblies. `ModuleBoundaryTests` reads `Assembly.GetReferencedAssemblies`
rather than parsing project files, so it sees what the linker sees.

Weaker than a registry because a name resolved at runtime is a string again, but far stronger than
text: it cannot be fooled by a comment, a rename, or a formatting change.

## 4. A source scan — last

Sometimes the property is about an ABSENCE, and no behavioural test can observe one. "No orchestration
layer knows a role by name", "no refusal is anonymous", "nothing reads the per-call route trail" — each
is a claim about text that is not there, and only a source scan can make it.

**Two rules bind every source scan in this repository.**

### A source scan may never depend on a character count

v0.3.8.91 shipped a guard that read a 4,000-character window. Its marker sat 27 characters inside the
window on Linux and outside it on a CRLF checkout, so every local run was green and `main` went red on
a property that had not changed. v0.3.8.97 hit the other half: adding an explanatory paragraph inside
a guarded member pushed the marker past the budget, and a guard whose subject was unchanged and still
true reported the strictness gone.

A budget is a proxy for "inside this thing" and a bad one — the guess is invisible when it is wrong,
it means something different per platform, and it drifts every time the code grows. Read the
delimiters. `SourceText.MemberBody` bounds a member; `SourceText.CallSites` bounds a call.

The reflex a false failure invites is to relax the rule it was guarding. That is the real cost.

### A source scan must resolve a named constant, not only a literal

Found four times between `.106` and `.109`, swept at `.112` with ten more instances. A guard whose
pattern is `Method\(\s*"(?<x>[a-z_]+)"` cannot see a call site that passes a shared constant — so it
stops covering the code exactly as the code gets tidier, silently, with no failure. A guard written to
stop shared names being hand-spelled ends up *rewarding* hand-spelling.

It is worse in both directions: the paired "every declared X is used" twin then reports the constant
as unreached, which is a false positive on correct code whose obvious fix is deleting the constant.

`SourceText.CallArgument` and `SourceText.ConstantsAcrossSource` are the shared readers. **Widen where
a guard LOOKS, never what it ACCEPTS** — a resolved constant is checked against exactly the same
vocabulary a literal is, so nothing a guard used to refuse becomes acceptable by being spelled
differently.

## Every guard needs a vacuity floor

A guard asserting that a set of violations is empty passes when the tree is clean AND when the guard
has stopped seeing the tree. Those are indistinguishable from the outside, and this suite has been
bitten by the second four times.

So a scan asserts that it found something to scan: `Assert.NotEmpty(roles)`, `refusals.Count >= 8`,
`declared.Count >= 50`. Pick the floor from measured values and set it where a real regression trips
it rather than where a formatting change does.

**A guard that cannot express success is not a guard, it is a deadline.**
`PartialCoverage_IsDeclaredRatherThanImplied` asserted `NotEmpty(partial)` — which would have failed
for the single outcome the ledger exists to reach. That was needed twice (v0.3.8.74, v0.3.8.79).

## A privilege-gated skip is a silent pass

`PathContainmentTests` opened seven link facts with `if (!SymlinksAvailable) return;`. On a Windows
agent without Developer Mode all seven pass green having asserted nothing — and a junction, the one
reparse point an unprivileged writer inside a workspace can actually create, was the case they could
never have caught. If a fact cannot run, say so; do not let it report as passing.

## Do not weaken a guard to make a test pass

Several tests here defend a *decision* rather than describe the code, and they are right often enough
that disagreeing with one is a reason to re-read the decision. When a guard is wrong, it is usually
its READER that is too narrow while its RULE is right — widen the reader.
