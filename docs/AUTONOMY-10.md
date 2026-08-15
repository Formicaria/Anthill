# AUTONOMY-10 — folded into THE PLAN

**This document no longer carries the forward program.** It has moved, in full, to
[`PLAN.md`](PLAN.md).

## Why

Two forward-looking documents is one too many. They drifted, as two copies of anything do — by the
time this was folded in, `AUTONOMY-10.md` still recorded `delete` as unimplemented, `rename` as not
implementable for want of a destination field, and `add`-over-existing as overwriting the target.
All three had shipped: `PatchProposal.DestinationPath` exists, `PatchApply.ComputeDelete` exists, and
`add` onto an existing file is a typed refusal.

A reader planning work from that page would have planned the wrong work. That is the same defect the
colony's own guards exist to catch — a declaration that disagrees with the runtime — appearing in the
documentation rather than the code.

## Where its content went

| Was here | Now |
|---|---|
| Phase 3 — typed artifact collaboration | `PLAN.md` §1 (done) and items 6–8 (what remains) |
| Phase 4 — structurally enforced workflows | `PLAN.md` §1 and acceptance gates 6–10 |
| Phase 5 — qualification | `PLAN.md` items 3–5, and [`QUALIFICATION.md`](QUALIFICATION.md) |
| Phases 6–7 — reputation routing, memory | `PLAN.md` items 11–12 |
| Phase 8 — autonomous PR lifecycle | `PLAN.md` item 13 |
| Phases 9–10 — connectors, self-improvement, production | `PLAN.md` item 14 |

The file itself is kept rather than deleted because several documents and the changelog link to it,
and a link check fails the build on a dead reference. It exists to point at the live plan, not to
compete with it.
