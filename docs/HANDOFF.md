# ANTHILL session handoff

**This file is a POINTER, not a snapshot.** Rewritten at v0.3.8.75 because it had been one, and the
snapshot was forty releases old: it opened "The 3.8 line is CLOSED at v0.3.8.34" while the shipping
release was v0.3.8.74. A handoff document is read by someone who knows nothing else yet, so a stale
one does not merely age — it actively sends the next session in the wrong direction, which is the
most expensive kind of wrong a document can be.

The fix is structural rather than an update. A snapshot has to be rewritten every release to stay
true and will therefore be false most of the time. Pointers stay true on their own.

## Start here

| Question | Where the answer lives, always |
|---|---|
| What is done, what is left, in order | [`PLAN.md`](PLAN.md) — §1 is the current state, §2 the ordered work |
| What shipped, and why | `CHANGELOG.md` at the repository root — newest entry first |
| Which release is current | `AnthillRuntime.Version`, mirrored in `Directory.Build.props`, `README.md` and `PLAN.md`; four markers, pinned equal by tests |
| How a role behaves | [`ANT_EXECUTION.md`](ANT_EXECUTION.md) |
| What "qualified" means | [`QUALIFICATION.md`](QUALIFICATION.md), and the executable ledger in `tests/Anthill.Tests/QualificationMatrixTests.cs` |
| What is still open | The same ledger — open scenarios say WHY, not just that they are open |

## Working rules that are not obvious from the code

- **Every release is branch → PR → squash-merge → tag.** Never push to `main`. CI builds the GitHub
  release from the tag.
- **`RELEASE_MSG.txt` is derived from the changelog's top entry**, not written twice. It becomes the
  commit message and the release notes; a stale one shipped v0.3.8.67 under v0.3.8.60's name.
- **A shipped changelog entry is frozen.** Corrections go in the NEXT entry, never by editing the
  old one — `ShippedChangelogTests` enforces it against the tags.
- **After a merge, `git fetch` + `git reset --hard origin/main`.** CI's squash commit differs from
  your local one, so `git pull` tries to merge and opens an editor.
- **Do not weaken a guard to make a test pass.** Several tests in this suite defend a decision rather
  than describe the code, and they are right often enough that disagreeing with one is a reason to
  re-read the decision.

## The recurring defect classes this repository names

Written down because the same shapes keep recurring, and recognising one early has repeatedly been
worth more than any individual fix:

- **A check answering a question ADJACENT to the one asked, and passing.** The most common by far.
- **Declared and reaching nobody** — a vocabulary, artifact or extension point nothing produces or
  consumes.
- **A declaration that disagrees with the runtime** — a comment, schema entry or stamp asserting
  something the code does not do.
- **Prose as a control channel** — a decision that depends on parsing text a model wrote.
- **Two implementations of one rule**, which eventually disagree.
- **A diagnostic that breaks what it describes.**

## When this file needs changing

Only when one of the pointers above stops being true — a document moves, or a working rule changes.
If you find yourself wanting to paste the current state in here, that belongs in `PLAN.md` §1.
