# ANTHILL Changelog

> Versioning convention: each autonomy phase or notable feature ships as a patch bump.
> Phase 1 = **v1.8.1**, live console + operator accounts = **v1.8.2**, enterprise shell UI = **v1.8.3**,
> model provider connections = **v1.8.4**, Phase 2 autonomy (Strategist) = **v1.8.5**, container-style
> deployment (Docker) = **v1.8.6**, LXC deployment = **v1.8.7**, provider base-URL fix = **v1.8.8**,
> LXC upgrade-in-place fix (ETXTBSY) = **v1.8.9**, LXC upgrade-in-place fix (stale native asset
> cache) = **v1.8.10**, Autonomy page recursion fix = **v1.8.11**, Phase 3 autonomy
> (concurrency + ResourceGovernor) = **v1.8.12**, coder Python-bias fix = **v1.8.13**, Phase 4
> autonomy (learning loop) = **v1.8.14**, mission reports (readable observability) = **v1.8.14.1**,
> UI cache + approval dedupe fixes = **v1.8.14.2**, Security + Shell config tabs = **v1.8.14.3**,
> header status + update check = **v1.8.14.4**, and so on.

## v1.8.14.4 — Live header status: update check, model/provider popover, local-vs-cloud icon

The top-right header was static — a fixed model string and an "Online" badge that didn't say what
was online. It's now a live, clickable status chip.

- **Update check** (`GET /update/check`): compares the running version against the latest release
  tag on the public GitHub repo and flags when a newer one exists. Result is cached server-side
  (30 min) so the header poll never hammers GitHub, and every failure (offline, rate-limited, no
  releases) degrades to "unknown" rather than erroring. When an update is available the chip shows
  a pulsing dot and the popover gives the new version, a release-notes link, and the exact LXC
  upgrade command. A "Re-check" button forces a fresh look (`?force=1`).
- **Local vs Providers icon**: the chip carries a monitor icon (green **LOCAL**) when every model
  role runs on local Ollama, or a cloud icon (purple **PROVIDERS** / **MIXED**) when any role is
  routed to OpenAI/Anthropic/Perplexity/OpenRouter — so the colony's cost/privacy posture is
  visible at a glance.
- **What's actually online**: the chip's dot now reflects backend health, not just the API — it
  goes red if Ollama is unreachable even while the API answers. The popover breaks it down: API
  server, Ollama backend reachability (with a live 3s probe), and Ollama host.
- **Model visibility + quick actions** (`GET /system/summary`): the popover lists every role's
  provider + model (each tagged local/cloud), the default model, and how many providers are
  connected, with one-click buttons to Ant Config (change models) and Settings → Providers.
- New: `src/Anthill.Api/UpdateChecker.cs`; `GET /update/check` + `GET /system/summary`.
- Tests: `UpdateCheckerTests` (dotted four-part version ordering, leading-v tolerance).

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

> **Pre-commit checklist** — the docs must be true before every commit:
> 1. Bump the version in all markers: `Directory.Build.props`, `AnthillRuntime.Version`,
>    `src/Anthill.Api/Ui/index.html` (title + auth logo + nav badge), `src/Anthill.Api/Program.cs`,
>    `src/Anthill.Cli/Program.cs`, `build.sh`, `build.ps1`, and the README banner.
>    Verify with: `grep -rn "<old version>" --exclude-dir=.git --exclude-dir=obj --exclude-dir=bin .`
> 2. Add the CHANGELOG entry (this file) — it becomes the GitHub release notes.
> 3. **Update README.md sections touched by the change** (Colony UI Guide, API Reference,
>    Configuration Reference, deployment sections) and `config.example.json` for new knobs.
> 4. Update `docs/AUTONOMY.md` / `docs/DEPLOYMENT.md` when behavior in their scope changes.
> 5. Sweep for leftovers: stale comments, dead config keys, outdated status claims, debug code.
> 6. `dotnet test Anthill.sln -c Release` green, then commit + tag + push.

## v1.8.14.3 — Configuration: Security tab + admin-only Shell console

Two new admin-only pages under Configuration.

- **Security tab**: a single place for the app's security posture — auth mode, safety profile,
  network bind exposure, and encryption-at-rest at a glance — plus live toggles for every
  capability gate (web search, file read/write, patch application, the AI ants' shell tool), the
  **workspace boundary** (`agent_workspace_dir`, the only path the file/coder ants may touch), and
  the operator-shell controls. All persist through the existing `/settings` path.
- **Shell tab**: a direct interactive terminal into the host ANTHILL runs on (the LXC/VM/box) —
  command input with history (↑/↓), streamed stdout/stderr, exit code, elapsed time, and a
  settable working directory. Built for host maintenance the AI ants must never do (restart the
  service, pull updates, edit config).

Because the Shell console is host remote-code-execution, it is gated four independent ways:
(1) authenticated, (2) **admin role only** — the new `operator_shell` permission is never in the
coordinator set, so a Mission Coordinator cannot see or use it; (3) the `operator_shell_enabled`
config gate (toggleable from the Security tab); and (4) **every command is written to the audit
event log** (`operator_shell_command` before it runs, `operator_shell_result` after) with the
operator's username, so there's a durable record of who ran what. Each command is bounded by a
60-second timeout and its output capped. Per operator request it ships **enabled for admins** by
default; set `operator_shell_enabled: false` (or toggle it off in Security) on any install you
don't fully trust on the network. Distinct from `shell_tool_enabled`, which gates the AI ants'
*allowlisted* tool and stays off by default.

- New: `GET /shell/info`, `POST /shell/exec` (both `operator_shell`, admin-only);
  `src/Anthill.Api/OperatorShell.cs`; config keys `operator_shell_enabled` / `operator_shell_dir`;
  `api_auth_enabled` + `agent_workspace_dir` added to the settings snapshot.
- Tests: `OperatorShellTests` — admin-only permission, command execution, non-zero exit,
  working-directory handling.

## v1.8.14.2 — Results page; stale-UI cache fix; approval-queue dedupe

New — **Results page** (operator request: mission results shouldn't take over the whole screen):

- A dedicated **Results** nav page lists every mission (newest first, filterable by
  completed / partial / failed) as compact collapsible rows — status in plain English, goal,
  score, and finish time. Expanding a row lazily loads the full Mission Report inline: final
  output, per-task readable results, tangible changes with approval states, problems, and — new —
  the **autonomous-run context** (which objective drove the mission) and the **objectives this
  mission created** (Strategist follow-ups, now stamped with `created_by_mission_id` /
  `created_by_run_id` metadata when the Director saves them, so the lineage is queryable).
- Every **View Result** button routes here and auto-expands that mission (jobs lists, Missions
  page, and the Autonomy runs table's View). Running jobs keep a compact "View Status" quick
  view; the old full-screen overlay remains only as a fallback for legacy jobs without a
  mission id.
- New API: `GET /missions/json?limit=` (mission history as JSON) and mission reports now include
  `autonomy_run` + `created_objectives`. New `SqliteMemory.GetAutonomyRunForMission` /
  `ListObjectivesCreatedByMission`.

Verified live against the running LXC instance (v1.8.14.1) before shipping: the backend live
task feed and the new canvas logic were confirmed working in a fresh browser session — particles
flowing, per-ant activity tracking real task states, correct idle when nothing runs. The
"still broken" console was the *browser's cached copy of the previous UI*.

- **`/ui` is now served with `Cache-Control: no-store`.** The console is embedded in the binary,
  and without cache headers a browser can silently pin an operator to the previous version's
  UI after every upgrade (stale canvas logic, missing panels) until a manual hard-refresh. Now
  every page load fetches the UI the running binary actually ships. (One last hard-refresh is
  needed to pick this version up; after that, never again.)
- **Approval-queue flooding fixed with dedupe.** High-frequency autonomous testing (observed
  live: 62 missions/hour, 1000/day until the daily budget tripped) re-proposes the same change
  run after run while the first request sits unreviewed — every rerun stacked another identical
  approval request. `Queen.ProcessPatchProposals` now checks
  `SqliteMemory.HasDuplicatePendingApproval` (same file, change type, and old/new content,
  compared after decryption) and skips creating a duplicate, logging an
  `approval_request_deduped` event instead. Decided (approved/rejected) requests never block a
  fresh proposal. Reminder that stacking ≠ malfunction: the Director *never* auto-approves —
  clearing the queue is the operator's half of the workflow until gated auto-apply (Phase 5)
  lands.
- **Tests**: `ApprovalDedupeTests` — identical pending change detected; different content/file
  not deduped; decided approvals don't block; null-content comparisons.

## v1.8.14.1 — Mission Reports: see exactly what the colony did, in plain English

Operator feedback from live use: work was "seemingly being done" (autonomous runs completing,
follow-up objectives appearing) but nothing readable in the UI showed what actually happened or
changed. Two root causes: (1) the only result view was the raw CLI dump — final output, debug
trace, and task JSON in one wall of jargon; (2) the tangible outputs of missions (patch
proposals waiting in the approval queue) were never connected to the mission/run that produced
them. Note the design constraint that makes visibility essential: **the colony cannot change
files or its own UI by itself** — every file change is a patch proposal that waits for human
approval + apply. If nothing is approved, nothing changes; the console now says so explicitly.

New:

- **`GET /missions/{id}/report`**: structured, human-readable report per mission — goal, status,
  score; the mission-level **final output** (kept separate from per-task outputs, since tasks are
  the steps and the mission is the deliverable); a per-task breakdown (title, ant, status,
  elapsed, readable output, and the *why* for failed/skipped/blocked tasks); **tangible changes**
  (every patch proposal the mission created, its file, reason, and current state — awaiting
  approval / approved / applied to disk / rejected, with apply errors); pending-approval count;
  sources saved; and **problems** — including `patch_proposal_parse_failed`, the silent killer
  where the coder did work but its proposal never reached the approval queue.
- **Plain-English task outputs**: coder results (raw JSON patch sets) are translated to
  "Proposed modify to src/... : reason" lines server-side (`ApiHost.ReadableTaskOutput`);
  other ants' prose passes through; malformed output falls back to raw text.
- **Mission Report modal**: "View Result" on any completed job now renders the structured report
  — status in words, final output, tangible-changes list with a pointer into Approvals,
  problems, and an expandable per-task list — instead of the raw CLI text (which remains the
  fallback for legacy jobs without a mission id).
- **Autonomy runs are inspectable**: each row in Recent Autonomous Runs gains a **View** button
  opening the same mission report, so every unattended run answers "what did it actually do,
  and did anything tangible come out of it?" in one click.
- **`SqliteMemory`**: `ListPatchProposalsForMission` / `ListApprovalRequestsForMission`
  (secret-free, per-mission).
- **Tests**: `ReportTests` — coder-JSON translation, empty-proposal wording, malformed-output
  fallback, prose passthrough. `InternalsVisibleTo("Anthill.Tests")` added to Anthill.Api.

Fixed (Colony canvas + Autonomy page housekeeping):

- **Ant/Queen hover tooltips showed activity over 100% and not live data.** Three bugs, one of
  them structural: (1) an operator-precedence error in the animation loop —
  `(colonyActivity[ant]||0-n.activity)` parses as `colonyActivity || (0-activity)`, accumulating
  activity unboundedly every frame; (2) the activity source was "this ant's share of all tasks"
  (including finished ones), not a live reading; and (3) — the structural one — **task rows were
  only persisted at mission start (before tasks exist) and mission end**, so `/graph` had no
  nodes at all while a mission ran; every mid-run number the canvas ever showed was stale data
  from the previous completed mission. Fixed end to end: the Queen now persists the planned task
  DAG before execution and upserts each task on every status transition (started → live
  "running"; complete/failed/skipped on finalize — new `SqliteMemory.SaveTask`), and the canvas
  computes activity from those current task states each poll (running = 100%, queued work = 35%,
  idle = 0%), clamped to [0,1] everywhere. Signal particles, node glow, the hover panel, and the
  task-DAG dataflow arrows now reflect what the colony is doing *right now* — the graph poll
  tightened from 5s to 2.5s to match.
- **Colony canvas sharpened**: the canvas now renders at the display's real pixel density
  (devicePixelRatio-scaled backing store, logical-coordinate drawing) — crisp nodes, edges, and
  labels on HiDPI screens instead of the previous blurry 1x upscale.
- **Autonomy tables no longer grow the page unboundedly**: the Objectives and Recent Autonomous
  Runs boxes are collapsible (click the header) and cap at ~20 rows with their own scrollbar and
  sticky column headers.
- **Docs housekeeping**: README brought up to date with everything shipped since v1.8.12
  (Concurrency/Governor status card, Score column, Mission Report views, `/missions/{id}/report`
  in the API reference, autonomy-knob pointers), and a pre-commit checklist added at the top of
  this file so the docs stay true on every release.

No schema change, no config change.

## v1.8.14 — Phase 4 autonomy: the learning loop

Mission outcomes now feed back into what the Director chooses to work on. Design per operator
review: read-time bias (stored priorities never drift — same philosophy as Phase 3 aging) and
auto-pause retirement with explicit events (never delete; a human reviews and resumes).

New:

- **Per-objective success EMA** (`objectives.success_ema`, schema v10 → v11, additive migration):
  every recorded run folds its mission success score into an exponential moving average
  (`autonomy_score_ema_alpha`, default 0.3; an unscored/failed run counts as 0). Always recorded
  — even with learning disabled — so history exists the moment it's turned on.
- **Selection bias** (`Autonomy/ObjectiveLearning.cs`, new): at selection time an objective's
  EMA adds a bounded, linear bias to its effective priority — EMA 1.0 → +`autonomy_priority_bias_max`
  (default 2), EMA 0.5 → 0, EMA 0.0 → −max. Computed read-time in
  `SqliteMemory.EffectivePriority` alongside Phase 3 aging; new objectives (null EMA) are
  unbiased. Operator numbers in the backlog stay authoritative.
- **Stale retirement**: after `autonomy_retire_min_runs` (default 5) runs, an objective whose EMA
  is below `autonomy_retire_score_threshold` (default 0.25) is auto-paused — it keeps running
  without producing value.
- **Loop retirement**: if the last `autonomy_loop_window` (default 4, 0 = off) generated goals
  are all near-identical (≥ `autonomy_dedupe_similarity` keyword overlap — the exact metric the
  Strategist's dedup uses), the objective is auto-paused. Catches the charter-fallback spiral:
  dedup already replaces repeat goals with the charter, so a true loop shows up as the same goal
  run after run.
- **Retirement = pause + event, never delete**: the Director emits an `objective_retired` event
  (code `stale_low_success` or `looping_goals`, with reason, EMA, and run count) and sets the
  objective to Paused, exactly like the existing failure circuit breaker. Resume from the
  Autonomy page after review. Retirement checks run on the director thread after each outcome is
  recorded, so nothing races the objective's own bookkeeping.
- **Config**: `autonomy_learning_enabled` (default true; false = exact Phase 3 behavior),
  `autonomy_priority_bias_max`, `autonomy_score_ema_alpha`, `autonomy_retire_min_runs`,
  `autonomy_retire_score_threshold`, `autonomy_loop_window` — all clamped, all in the settings
  whitelist; toggle + integer knobs editable from Settings → Colony.
- **Observability**: `/objectives` and the Autonomy page's backlog table gain a **Score** column
  (the EMA, color-coded); `autonomy_mission_finished` events and `/autonomy/status` include
  `success_ema` / `learning_enabled`.
- **Tests**: `LearningTests` — EMA seeding/smoothing/persistence, bias linearity and bounds,
  EMA-driven selection ordering (and its disappearance when learning is off), stale/loop/never
  retirement decisions.

## v1.8.13 — Fix: coder ant proposed Python patches regardless of the project's language

Reported from live use: send the colony an objective against this (C#) repo and the coder ant
comes back proposing Python. Root cause was leftover DNA from ANTHILL's original Python build
(`py.old/`) — three compounding biases, no model misbehavior:

- **CoderAnt's JSON format example showed a `.py` path** (`"file_path":
  "relative/path/to/file.py"`). Small local models imitate format examples very literally, so
  the example language became the answer language. Now a neutral `file.ext`, plus a new
  first-position rule: match the language/conventions of the files visible in context, and if no
  existing code is visible and the goal names no language, return an empty proposals list rather
  than guess.
- **FileAnt injected `anthill.py` as a candidate path** whenever a mission mentioned "anthill",
  "this script", or "main script" — a relic of the Python-era entry point that fed Python-flavored
  context to every downstream ant. Removed; candidate paths now come only from what the mission
  text actually names.
- **FileAnt's path-extraction regex couldn't see .NET paths**: its suffix list (`py|txt|md|...`)
  predated the port and omitted `cs|csproj|sln|props|targets` (and other patchable types:
  `sh|bat|ps1|cmd|go|rs|java|kt|rb|php|tf|hcl|sql`). A mission saying "fix
  src/Anthill.Api/ApiHost.cs" never surfaced that path as a read candidate. The list now matches
  `AnthillRuntime.PatchAllowedSuffixes`, so every file type the coder may patch is also one the
  file ant can spot and read — including the colony's own sources for self-modification missions.

No schema change, no API change, no config change.

## v1.8.12 — Phase 3 autonomy: concurrent missions + ResourceGovernor

The Director can now run up to `autonomy_concurrency` missions side by side (default 1 —
behavior is unchanged until the operator raises it; clamped 1–8). Design decisions per operator
review: strict-priority scheduling with anti-starvation aging, and a load/probe governor with
full VRAM tracking deferred to a later hardware-aware scheduler phase.

New:

- **`ResourceGovernor`** (`src/Anthill.Core/Autonomy/ResourceGovernor.cs`): sizes effective
  concurrency each cycle from the configured cap — and can only ever lower it. Signals:
  normalized CPU load per core (≥1.25 halves, ≥2.0 clamps to 1), available-memory fraction
  (≤20% halves, ≤10% clamps to 1), and an Ollama probe (`GET /api/version`, 15s cache —
  unreachable clamps to 1, ≥2.5s latency halves). Unreadable *host* signals fail open (skip);
  a dead *backend* fails safe (clamp to 1 — missions would fail anyway, don't multiply them).
  Skipped entirely when `use_ollama` is false, so offline installs are never clamped by it.
- **Concurrent Director loop** (`src/Anthill.Api/ColonyDirector.cs`): non-blocking launches with
  an in-flight table, reaped as jobs finish. Everything still happens on the one director thread
  (Strategist/BudgetGuard stay sequential by construction); the hard rails are re-checked before
  every individual launch. Stop/kill-switch now *drains*: no new launches, in-flight missions
  finish and are recorded, then the thread exits — nothing is ever left unrecorded.
- **Strict priority + aging** (`SqliteMemory.NextReadyObjectives`): slots fill with the
  highest-effective-priority distinct ready objectives; an objective never runs two missions at
  once. Effective priority = priority + 1 per `autonomy_aging_minutes` waited (default 30;
  0 = pure strict priority); longest-queued wins ties. Computed at read time — stored priorities
  never drift.
- **Config**: `autonomy_concurrency`, `autonomy_aging_minutes` — in `config.example.json`, the
  settings whitelist, `/settings`, and the Settings → Autonomy panel.
- **Observability**: `/autonomy/status` gains `concurrency_configured`/`concurrency_effective`,
  `governor_code`/`governor_reason`/`governor_signals`, `aging_minutes`, and `in_flight`;
  `autonomy_mission_started` events carry the governor verdict. Autonomy page: Concurrency KPI +
  In-flight and Governor rows.

Fixed (latent, pre-existing):

- **`Queen.LastMissionId` race**: with >1 job worker, a finishing worker could stamp its job with
  *another* worker's mission id. `Queen.RunMission` now reports the mission id through an
  `onMissionCreated` callback the moment the row is persisted, and `ApiJobRegistry` uses it —
  also making the mission id visible on the job while it's still running. `LastMissionId` remains
  for the single-mission CLI path.
- Job worker pool is sized `max(api_job_workers, autonomy_concurrency)` at boot so concurrent
  autonomous missions actually get worker slots instead of queueing behind each other.

Validation: `GovernorTests` (every clamp path, fail-open vs. fail-safe, tightest-constraint-wins,
throwing readers), multi-slot selection/aging tests in `AutonomyTests`, and an offline two-slot
Director run in `DirectorTests` asserting both objectives complete with distinct mission ids and
per-objective run records. Existing Phase 0–2 suites unchanged and still green.

## v1.8.11 — Fix: Autonomy page's Start/Stop (kill switch) froze the UI via infinite recursion

No schema change, no API change — pure front-end JS bug in `src/Anthill.Api/Ui/index.html`.
Reported live as "the web app and service crashes when you hit the kill switch in the autonomy
page." Reproduced by driving the real running instance directly: clicking the "■ Stop" kill
switch caused the browser tab to stop responding to input.

Root cause: `openAutonomy()` called `showPage('autonomy')` at its top, but `showPage()` itself
calls `PAGE_ENTER['autonomy']()` right after switching pages — and `PAGE_ENTER['autonomy']` was
wired to call `openAutonomy()` again. That's unbounded mutual recursion:
`openAutonomy → showPage → PAGE_ENTER.autonomy → openAutonomy → showPage → ...`. It fired on
*every* visit to the Autonomy page — including the periodic status refresh the page runs while
open — and threw `RangeError: Maximum call stack size exceeded` hundreds of times per trigger
(confirmed live via the browser console). Each occurrence briefly pegs the JS main thread as it
unwinds thousands of stack frames, which is what made the tab appear to hang or "crash" right as
a click (like Stop) landed. `openAntConfig()` (the Ant Config page) had the exact same bug
pattern, not yet reported but fixed here too. The .NET backend was never actually affected —
`/health` and the Director's own stop/start logic kept working the entire time this was
happening; confirmed via `/autonomy/status` and the `autonomy_stopped`/`autonomy_started` event
log entries recording correctly through repeated live reproduction.

Fixed:

- **`openAutonomy()`**: no longer calls `showPage('autonomy')` — it's now a pure data-loader,
  correct since its only caller is `PAGE_ENTER['autonomy']`, which `showPage()` already invokes
  *after* switching to the page.
- **`openAntConfig()`**: same fix, same reasoning (its `showPage('antconfig')` call is gone; its
  second caller, the Ant Config "Reset" button, doesn't need a page-switch either since the user
  is already on that page when clicking Reset).

Validation:

- Reproduced and fixed live against the user's running LXC instance via direct browser
  automation: captured the exact `RangeError` stack trace from the browser console, hot-patched
  the corrected function into the live page, then repeated the same Start → Stop sequence with
  the patch active — no errors, no hang, instant response both times. `bash -n`/syntax not
  applicable (HTML/JS); confirmed by the live before/after test described above. Ship this build
  to make the fix permanent (the hot-patch only lived in that one browser tab's memory).

## v1.8.10 — Fix: LXC upgrade republish silently dropped the SQLite native library

No schema change. Bug found live re-running `deploy/lxc/setup.sh` on the user's LXC instance
immediately after the v1.8.9 ETXTBSY fix — the very first time a republish onto that install
directory ever ran to full completion. The service came up, then immediately crashed in a
restart loop:

```
Unhandled exception. System.TypeInitializationException: The type initializer for
'Microsoft.Data.Sqlite.SqliteConnection' threw an exception.
 ---> System.DllNotFoundException: Unable to load shared library 'e_sqlite3' or one of its
dependencies.
/opt/anthill/bin/e_sqlite3.so: cannot open shared object file: No such file or directory
```

Root cause: that same install directory had been publish-targeted several times across this
session's earlier v1.8.7/v1.8.8/v1.8.9 attempts, including at least one run that was killed
mid-bundle by the ETXTBSY bug itself. `dotnet publish` reused the leftover `obj/`/`bin`
incremental state from those prior (partially-failed) runs and decided the RID-specific SQLite
native asset (`e_sqlite3.so`) was already up to date — so it skipped copying it into the output
directory, even though it wasn't actually there. The resulting single-file binary builds, starts,
and immediately SIGABRTs the moment it touches the database.

Fixed:

- **`deploy/lxc/setup.sh`**: wipes `obj/`/`bin` for `Anthill.Cli`, `Anthill.Core`, and
  `Anthill.Api` immediately before every publish, so install/upgrade is always a from-scratch
  build rather than trusting incremental state that a prior interrupted run may have left
  inconsistent. Adds a post-publish check that fails loudly (with a clear error) if no
  `e_sqlite3` native library made it into the output directory, instead of letting it surface
  later as a silent SIGABRT crash loop under systemd.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full stack trace confirmed
  root cause precisely (`SqliteConnection..cctor` → `SQLitePCL.Batteries_V2.Init` →
  `DllNotFoundException`). Fix itself has **not been re-verified live** — no LXC/Proxmox host or
  dotnet SDK available in the environment this was authored in. `bash -n` syntax check passes.
  Confirm by re-running `bash deploy/lxc/setup.sh` and checking `ls /opt/anthill/bin/*e_sqlite3*`
  finds the native library, then that the service stays up (`systemctl status anthill`).

## v1.8.9 — Fix: LXC upgrade-in-place failed with "Text file busy"

No schema change. Bug found live re-running `deploy/lxc/setup.sh` to upgrade an already-running
LXC install to v1.8.8: `dotnet publish` failed with
`System.IO.IOException: Text file busy : '/opt/anthill/bin/anthill'` inside the `GenerateBundle`
MSBuild task.

Root cause: `setup.sh` republishes directly into `$INSTALL_DIR/bin`, which is exactly where the
systemd unit's `ExecStart` runs the binary from. .NET's single-file bundler does an in-place file
copy rather than write-to-temp-then-atomic-rename, and Linux refuses to open a currently-executing
binary for direct write access (`ETXTBSY`) — replacing a running program's file via `rename()` is
fine, overwriting it in place while it's executing is not. First-time installs never hit this
(nothing running yet); every subsequent upgrade-in-place did, 100% of the time.

Fixed:

- **`deploy/lxc/setup.sh`**: stops the `anthill` systemd unit immediately before the publish step
  (`systemctl stop anthill 2>/dev/null || true` — safe no-op on a first install, since the unit
  doesn't exist yet). The existing `systemctl restart anthill` at the end of the script already
  starts it back up regardless of whether it was freshly installed or stopped for an upgrade, so
  this was a one-line, symmetrical fix.

Validation:

- Found via the user's real upgrade attempt on their LXC instance — full MSBuild stack trace
  confirmed root cause precisely (`Microsoft.NET.HostModel.Bundle.Bundler.GenerateBundle` →
  `SafeFileHandle.Open` → `ETXTBSY`). Fix itself has **not been re-verified live** — no LXC/Proxmox
  host or dotnet SDK available in the environment this was authored in. `bash -n` syntax check
  passes. Confirm the fix by re-running `bash deploy/lxc/setup.sh` on the same instance and
  checking it completes without stopping mid-publish this time.

## v1.8.8 — Fix: provider Base URL override sent as a bare prefix, not a real endpoint

No schema change. Bug found live on a real LXC deployment: testing the OpenAI connection in
Settings → Providers failed every time with `ERROR: OpenAI request failed (404): `.

Root cause: the stored `base_url` override (`https://api.openai.com/v1`) was used as the literal
request URL in `OpenAiCompatibleClient`, with no path appended — so the request actually hit
`https://api.openai.com/v1`, not a real API route, and OpenAI correctly 404'd it. The value that
was stored is exactly how OpenAI's own SDKs define `base_url` (host + version prefix only, path
appended internally), so typing it that way into the override field is a completely reasonable
thing to do even though the field's placeholder shows the full path — the code should tolerate
both forms rather than silently breaking on one of them.

Fixed:

- **`OpenAiCompatibleClient.NormalizeEndpoint`** (covers OpenAI, Perplexity, and OpenRouter, which
  all share this client): if a configured endpoint doesn't already end with `/chat/completions`,
  it's appended automatically. Handles a trailing slash either way. Applied in the constructor, so
  it self-corrects for any already-stored override without needing a database fix or the user to
  re-save anything.
- **`AnthropicClient`**: previously didn't accept a `base_url` override at all —
  `ModelRouter.BuildKeyedClient`'s `"anthropic"` branch built the client with only the API key and
  model, silently discarding whatever was stored in `provider_credentials.base_url`. Now accepts
  an optional endpoint, normalized the same way (`/messages` appended if missing), wired through
  from `ModelRouter`.
- **`tests/Anthill.Tests/ProviderTests.cs`**: added `NormalizeEndpoint` coverage for both
  providers — bare prefix, full path, and either with/without a trailing slash — plus confirms
  `AnthropicClient` falls back to its documented default when no override is stored. Made both
  `NormalizeEndpoint` methods `public` (were `private`) specifically so this is directly
  unit-testable without a network call.

Validation:

- Found via live testing against a real running LXC instance (`10.10.10.60:8713`, connected via
  browser automation) — reproduced the exact failing request, read the actual response body
  (`ERROR: OpenAI request failed (404): `) and the stored `base_url` from `GET /providers`,
  confirmed the root cause by reading `OpenAiCompatibleClient`/`ModelRouter` against that data.
  The fix itself has **not yet been re-verified live** — no `dotnet` SDK available in the
  environment this was authored in. Brace/paren balance checked manually. Once deployed
  (`git pull && bash deploy/lxc/setup.sh` on the LXC box, or a new tagged release), re-run Test
  Connection on OpenAI and confirm it now succeeds.

## v1.8.7 — LXC deployment

No schema change. Second step of the container/LXC/Windows-Service deployment push (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)) — a one-shot installer for a fresh Debian/Ubuntu-family
LXC container, Proxmox or otherwise.

Added:

- **`deploy/lxc/setup.sh`** — unattended installer/upgrader for a fresh Debian 12+/Ubuntu 22.04+
  LXC container. Installs the .NET 9 SDK if missing (Microsoft's apt repo, resolved dynamically
  from `/etc/os-release` rather than hardcoded to one distro/version, with a `dotnet-install.sh`
  fallback for distros/versions Microsoft's repo doesn't have an entry for), clones/updates the
  repo, publishes a self-contained `linux-x64` binary, creates a dedicated unprivileged system
  user, installs + enables the systemd unit, and starts it. Idempotent — re-running it is the
  upgrade path (pulls latest, republishes, restarts). Configurable via `ANTHILL_REPO_URL`,
  `ANTHILL_INSTALL_DIR`, `ANTHILL_SERVICE_USER` env vars.
- **`deploy/lxc/anthill.service.template`** — the systemd unit `setup.sh` installs, with the same
  hardening as the manual systemd install already documented in the README (`NoNewPrivileges`,
  `PrivateTmp`, `ProtectSystem=strict`, scoped `ReadWritePaths`), plus `Environment=ANTHILL_HOME`
  for unambiguous workspace resolution and a generated `/etc/anthill/token.env` for an optional
  static API token.
- No special LXC features (nesting, privileged mode) are required — this is a plain systemd
  service, not Docker-in-LXC, so `NetworkUtil`'s auto-detected reachable IP works with zero extra
  networking config (LXC containers get a real interface directly, unlike Docker's bridge
  default).
- **README**: new "Option E — LXC" under Deploy on Linux, quick-start + pointer to the full guide.
- **`docs/DEPLOYMENT.md`**: §3 filled in — creating the container (Proxmox `pct` commands +
  generic LXD/incus note), installing, upgrading, customizing install location/service user,
  uninstalling. Roadmap table updated (LXC done, Windows Service next).
- **CI**: new `lint-lxc-installer` job — `shellcheck` (GitHub-hosted runners have it preinstalled)
  plus a `bash -n` syntax check on `deploy/lxc/setup.sh`.
- **Releases** (`.github/workflows/release.yml`, added after this version shipped): triggered by
  pushing a `vX.Y.Z` tag. A `verify-version` job fails loudly if the tag doesn't match
  `AnthillRuntime.Version` in the tagged commit, guarding against tagging before a version bump
  actually landed. Builds self-contained `linux-x64`/`win-x64` binaries and a versioned Docker
  image pushed to `ghcr.io/thexonexone/operation-anthill`, then opens a **draft** GitHub Release
  (never auto-published) with notes pulled from the matching `## vX.Y.Z` CHANGELOG section and
  the binaries/image attached. Also fixed the stray `YOUR_ORG`/`anthill-dotnet` placeholder repo
  references across `README.md` to the real `thexonexone/operation-anthill` URL and directory
  name. See [docs/DEPLOYMENT.md §4](docs/DEPLOYMENT.md#4-releases).

Validation:

- `bash -n deploy/lxc/setup.sh` passes (checked manually — no shellcheck available in the
  authoring environment, hence the new CI job to actually run it). Not run against a real LXC
  container; no LXC/Proxmox host available in the environment this was authored in. Try it on an
  actual container before trusting it in production, and watch the first CI run for the new
  shellcheck job.

## v1.8.6 — Container-style deployment: Docker, all-interfaces binding, env var config

No schema change. First step of ongoing work to make ANTHILL deployable like a normal home-lab
appliance — standalone Docker container today, LXC and Windows Service to follow (see
[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)).

Added:

- **Docker packaging** — `Dockerfile` (multi-stage: SDK build stage, framework-dependent publish
  onto the `aspnet:9.0` runtime image, non-root user, unauthenticated `/health`-backed
  `HEALTHCHECK`), `docker-compose.yml` (defaults to `network_mode: host` on Linux so the
  container is reachable at the host's real LAN IP, with a bridge-mode alternative documented for
  Windows/macOS Docker Desktop), and `.dockerignore`. No config file or token required to start —
  ANTHILL self-seeds a container-safe default `config.json` into the mounted `.anthill` volume on
  first boot.
- **All-interfaces binding by default** — `api_host` now defaults to `0.0.0.0` (was `127.0.0.1`)
  in both `AnthillConfig`'s default and every safety profile's forced override. The operator login
  was already the real security boundary (auth is forced on in every profile regardless of bind
  host), so this changes what a fresh container/LXC/service install looks like on first boot, not
  the actual security posture. Set `api_host` to `127.0.0.1` (or `ANTHILL_HOST=127.0.0.1`)
  explicitly for a localhost-only install.
- **`ANTHILL_HOST` / `ANTHILL_PORT` / `ANTHILL_OLLAMA_HOST` / `ANTHILL_OLLAMA_MODEL` env vars** —
  new highest-precedence overrides (win over `config.json`) in `AnthillRuntime.ProjectConfig`,
  so container/LXC/service deployments can be configured entirely from the outside. This also
  fixes a latent bug: the CLI's `--host`/`--port`/`--ollama-host`/`--ollama-model` flags
  previously wrote a static field that `AnthillRuntime.Initialize()` immediately overwrote from
  `config.json` a moment later inside `ApiHost.Run()`, silently ignoring the flags; they now set
  these same env vars instead, which survive.
- **`NetworkUtil.GetLikelyLanIPv4()`** (`Anthill.Core/Common/NetworkUtil.cs`) — cross-platform,
  no-network-traffic LAN IP auto-detection (local UDP "connect", no packet sent), used to print a
  real, clickable URL in the startup console banner and in `GET /status` (`reachable_ip` field)
  instead of the unusable `0.0.0.0` bind address, on both Linux and Windows.
- **Config hygiene**: `config.example.json` had a trailing comma making it invalid JSON — copying
  it verbatim per the documented Quick Start steps would silently fall back to all-default config
  with just a console warning easy to miss in a container. Fixed, and added the three Phase 2
  Strategist knobs (`autonomy_dedupe_similarity`, `autonomy_max_followups_per_run`,
  `autonomy_max_objective_depth`) that were missing from the example file.
- **`Anthill.sln` fix**: the solution file only registered `Anthill.Tests`; `Anthill.Cli`,
  `Anthill.Api`, and `Anthill.Core` were never top-level entries, so `dotnet build Anthill.sln`
  (as documented in the README) silently never built the actual CLI/API projects — only
  `build.sh`/`build.ps1`'s explicit direct-publish step did. Added all three so the documented
  build/IDE-open workflow matches what actually ships.
- **README**: Docker section expanded to include the full `docker-compose.yml` walkthrough (field
  table, bridge-mode alternative for Windows/macOS Docker Desktop, plain `docker build`/`docker
  run` commands, Dockerfile summary, upgrade steps) instead of just pointing at
  `docs/DEPLOYMENT.md`; Security Model table gained a network-binding row; Windows Service section
  flagged as pending proper SCM integration (planned next, see `docs/DEPLOYMENT.md` §4); CI badge
  added.
- **CI** (`.github/workflows/ci.yml`, previously an empty placeholder): now runs on every push/PR
  to `main` (and manually via `workflow_dispatch`) — `dotnet build`/`dotnet test` on Linux and
  Windows, a self-contained `linux-x64` publish + `--selftest` run, and a Docker build + container
  boot smoke test that polls the `HEALTHCHECK` status and hits `GET /health` directly. This is the
  actual "does this work" signal going forward, since neither a .NET SDK nor a Docker daemon is
  available in the environment these changes were authored in.

Validation:

- Manual review only — no `dotnet` SDK or Docker daemon was available in the environment this
  change was authored in, so `docker build`/`docker compose up` and `dotnet build`/`dotnet test`
  have **not** been run against this change. Brace/paren balance checked by hand on every edited
  `.cs` file; `config.example.json` re-validated as parseable JSON. Run `dotnet build && dotnet
  test`, then `docker compose up -d --build` and confirm the console banner prints a real LAN IP,
  before relying on this in production.

## v1.8.5 — Autonomy Phase 2: Strategist

No schema change. Mission goals for the 24/7 Colony Director are now LLM-generated per objective
instead of charter-as-goal, with dedup against recent mission history and capped self-enqueued
follow-up objectives. See [docs/AUTONOMY.md](docs/AUTONOMY.md) for the full design.

Added:

- **`Anthill.Core.Autonomy.Strategist`** — turns an objective + recent `autonomy_runs` history +
  top pheromone trails into a concrete mission goal via a new `strategist` model-router role.
  Always computes the deterministic charter-as-goal fallback first; any router failure, missing
  router, or unparseable response falls back to it — never blocks or throws. `StrategistResult`
  reports `Source` (`"strategist"` or `"fallback"`) for auditability.
- **Dedup** — rejects a generated goal that's a near-duplicate of a recent completed/partial run
  for the same objective (`TextUtil.ExtractKeywords` containment-ratio overlap, threshold
  `autonomy_dedupe_similarity`, default `0.8`); a rejected goal falls back to the charter goal.
- **Follow-up objectives** — the Strategist can propose follow-ups in its JSON response;
  `ColonyDirector` saves them only after a successful mission, capped by
  `autonomy_max_followups_per_run` (default `1`) and `autonomy_max_objective_depth` (default `3`,
  walked via new `SqliteMemory.ObjectiveDepth`). Follow-ups inherit `ParentObjectiveId` and run at
  `Priority - 1`.
- **New config knobs** (all fail-closed): `autonomy_dedupe_similarity`,
  `autonomy_max_followups_per_run`, `autonomy_max_objective_depth`.
- **`ColonyDirector.RunObjectiveOnce`** rewritten to call `Strategist.GenerateGoal`; logs
  `goal_source`/`strategist_notes` on `autonomy_mission_started` and `follow_ups_created` on
  `autonomy_mission_finished`. Writes remain queue-only — the Strategist only chooses the next
  mission, never applies patches.
- **UI: Autonomy page** *(admin only)* — Director status card (start/stop, budgets, kill switch),
  objectives backlog editor (add/pause-resume/reprioritize/delete), and a recent-runs table
  showing goal source and outcome per run.

Validation:

- Offline `StrategistTests` cover the no-router fallback path (never blocks/throws) and
  `ObjectiveDepth` walking/edge cases. The LLM-driven generation/dedup paths require a live
  provider and aren't testable offline.
- Manual review only — no `dotnet` SDK was available in the environment this change was authored
  in, so this release has **not** been compiled or run. Run `dotnet build`/`dotnet test`, then
  exercise the Autonomy page end-to-end (add an objective, start the Director, confirm a run
  appears with a goal source) before shipping.

## v1.8.4 — Model provider connections

Schema bumped to **v10** (new `provider_credentials` table, migration `model_provider_connections`).
Ants can now be routed to paid cloud model providers — OpenAI (ChatGPT), Anthropic (Claude),
Perplexity, and OpenRouter — alongside free local Ollama, with API keys managed from the console.

Added:

- **`provider_credentials` table** — one row per external provider. The API key is sealed at rest
  with the existing AES-256-GCM `FieldCipher` (same key resolution as other encrypted columns:
  `ANTHILL_ENCRYPTION_KEY` env var, else an auto-generated 0600 workspace key file) and is never
  read back over the API; only a `configured` boolean, `enabled` flag, optional base-URL override,
  and last-verification status are ever exposed.
- **Real provider clients** (`Anthill.Core.Models.ProviderClients`) — `OpenAiCompatibleClient`
  (shared by OpenAI, Perplexity, and OpenRouter, which all speak the same `{model, messages}` →
  `choices[0].message.content` contract) and `AnthropicClient` (Messages API, `x-api-key` header,
  `content[]` block array). Both fail closed with `ERROR:` sentinel strings on missing keys,
  auth failures, timeouts, or transport errors, matching `OllamaClient`'s existing contract so the
  rest of the colony (retries, pheromone scoring, event logging) needs no changes.
- **`ProviderCatalog`** — static metadata (display name, free/paid kind, curated model list, key
  help URL, default endpoint) for every known provider, driving both the API's `/providers/catalog`
  response and the console's dropdowns.
- **`ModelRouter` keyed-client routing** — OpenAI/Anthropic/Perplexity/OpenRouter clients are built
  fresh on every call (not cached like Ollama's) so a rotated or removed key takes effect
  immediately without a process restart.
- **New endpoints**: `GET /providers/catalog`, `GET /providers`, `POST /providers` (upsert — a
  blank `api_key` on update leaves the stored key untouched), `DELETE /providers/{provider}`,
  `POST /providers/{provider}/test` (fires one live probe call through the real routing path and
  records the verification result). Gated by two new permissions, `read_providers` and
  `manage_providers`, both admin-only like `manage_settings`/`manage_users`.
- **Settings → Providers tab** — a card per provider with API-key input, optional base-URL
  override, Save / Test Connection / Remove actions, and a status pill (not connected / connected /
  verified / verification failed).
- **Ant Config provider + model routing** — each caste's model route editor now has a Provider
  selector (Ollama or any connected external provider) in addition to the existing model picker;
  saving writes `{provider, model}` into `model_routes` exactly as before.

Validation:

- Manual review only — no `dotnet` SDK was available in the environment this change was authored
  in, so this release has **not** been compiled or run. Run `dotnet build` and exercise
  `POST /providers`, `POST /providers/{provider}/test`, and an Ant Config route change before
  shipping.

## v1.8.3 — Enterprise shell UI

No schema change. The web console is rebuilt as a full enterprise-grade shell; all existing API
endpoints, auth, and backend behaviour are unchanged.

Added:

- **Left navigation rail** — 240 px expanded / 60 px icon-only collapsed, toggled by the **‹**
  button; collapse state persisted in `localStorage`. Eight nav items with icons: Overview, Colony,
  Missions, Event Log, Pheromones, Ant Config, Settings, Users.
- **8 routed pages** — `showPage(id)` swaps the active page, updates the nav active state, updates
  the header title, and fires a per-page `PAGE_ENTER` callback. All former modal overlays are now
  full pages:

  | Page | Replaces |
  |------|---------|
  | **Overview** | *(new)* — KPI grid, mission dispatch, recent jobs, live event feed |
  | **Colony** | Canvas page (unchanged layout; canvas now fills `#colony-canvas-area`) |
  | **Missions** | *(new)* — dedicated dispatch + full job history |
  | **Event Log** | `log-modal` overlay |
  | **Pheromones** | `phero-modal` overlay |
  | **Ant Config** | `antcfg-modal` overlay |
  | **Settings** | `settings-overlay` |
  | **Users** | `users-modal` overlay |

- **Fixed top header (50 px)** containing: page title (left), active mission goal + status dot
  (centre), model badge / connection badge / approval bell / logout button (right). The approval
  bell is always visible regardless of active page; clicking it navigates to Colony and scrolls
  to the Approvals card.
- **KPI cards on Overview** — Model Calls, Tasks Done, Events, Approvals; updated by the same
  `pollStatus()` that drives the Colony sidebar (both sets of stat elements are kept in sync).
- **Three mission dispatch inputs** — Colony canvas bar (`mission-input`), Missions page
  (`ms-mission-input`), Overview page (`ov-mission-input`); all feed the same `dispatchMission()`
  function and `enableInput()` locks/unlocks all three simultaneously.
- **Canvas mouse coordinate fix** — `mousedown`, `mousemove`, `mouseup`, `dblclick`, and `wheel`
  handlers now derive canvas-local coordinates via `canvas.getBoundingClientRect()` instead of the
  former `e.clientY - 50` hardcode. Hit-testing and zoom-pivot are accurate at every zoom level and
  nav-rail state.
- **Canvas resize fix** — `resize()` reads from `document.getElementById('colony-canvas-area')`
  (the flex container) instead of the former `#cw` element, so the canvas fills its area exactly
  regardless of sidebar widths.
- **Keyboard shortcuts** — `Ctrl+K` focuses the mission input on Colony; `Ctrl+L` navigates to
  Event Log; `Escape` closes the result overlay and rename popover.
- **`ov-jobs-list` / `ov-feed-list`** — the Overview page renders job lists and the live event
  feed (capped at 20 entries) from the same poll functions (`pollJobs`, `pollEvents`) that drive
  the Colony sidebar; no additional polling.
- **Card collapse** — every card in the Colony sidebars has a **▾** toggle; collapsed state is
  persisted in `localStorage` keyed by card id.
- **Result overlay** — position-fixed over the entire shell, shown when viewing a job result;
  includes Copy, Refresh, and Close controls.

Changed:

- All event IDs and API calls are **unchanged** — only the surrounding layout changed.
- `pollJobs()` now renders to three containers: `jobs-list` (colony left), `ov-jobs-list`
  (overview), `ms-jobs-list` (missions page).
- `pollEvents()` renders to `feed-list` (colony right) and `ov-feed-list` (overview, capped at 20).
- `pollStatus()` updates both `s-calls/tasks/events/approvals` (colony) and the `ov-*` equivalents.
- `applyRoleVisibility()` now also hides/shows nav items and the approval bell per role, not just
  page-level controls.

Validation:

- ID audit: all 143 required element IDs verified present in the final `index.html` (automated
  check with PowerShell regex pass).
- JS balance: brace count delta = 0; backtick count even (no unclosed template literals).
- No unreplaced CSS/JS/HTML placeholder markers; single `<style>` and `<script>` block.
- All original API endpoints and auth flow verified unchanged (no backend edits in this release).

## v1.8.2 — Live colony console + operator accounts

Schema bumped to **v9**. The web console becomes a fully operable control plane, and the shared
API token is replaced by real password-based operator accounts with roles.

Added:

- **Operator accounts + roles (replaces token-only web auth)**: the console is now secured by
  password login, not a shared API key. First run shows a one-time setup screen that creates the
  initial **administrator**; thereafter everyone signs in with a username/password and receives an
  in-memory **session** (12-hour sliding expiry, dropped on restart). Two roles:
  - **Administrator** — full control, including user management.
  - **Mission Coordinator** — may *only* send missions to the Queen and read the event logs (plus
    live status to watch them); everything else is denied at the API and hidden in the UI.

  Passwords are salted **PBKDF2-SHA256** (120k iterations), verified in constant time. New `users`
  table (schema v9, migration `user_accounts`); endpoints `GET /auth/status`, `POST /auth/{setup,
  login,logout}`, `GET /auth/me`, and `GET/POST/PATCH/DELETE /users`. A 👥 Users management page
  lets admins create accounts, assign roles, reset passwords, enable/disable, and delete. Changing a
  user's password, role, or status immediately revokes their active sessions, and the last remaining
  administrator can never be demoted, disabled, or deleted. CLI recovery: `--add-user <u> <p> [role]`
  and `--set-password <u> <p>`.
- **`ANTHILL_API_TOKEN` is now optional** (and no longer required to boot). If set (≥ 32 chars) it
  acts as a programmatic **admin** bearer for scripts/CI; otherwise authentication is purely account-based.
- **Live, actionable console** (same deep-navy/yellow theme):
  - **Editable settings** — Ollama host/model, per-role model routes, capability gates, limits, and
    autonomy budgets are all editable from the page and persisted to `config.json`
    (`GET/POST /settings`). Whitelisted keys only; hard security boundaries stay file-controlled.
  - **Movable / renamable / recolorable ants** — drag to arrange the anthill, double-click to rename,
    set per-caste accent colours and model routes from a dedicated **Ant Configuration** page. Layout
    persists server-side (`GET/PUT /ui/state`, stored in `.anthill/ui_state.json`).
  - **Filterable Event Log** — by ant, event type, severity (errors/alarms), and time window, plus
    text search (`GET /events/json`).
  - **Pheromone Memory page** — strength table with success/failure/net counts and one-click pruning
    of weak and failure-dominant trails (`GET /pheromones/json`, `POST /pheromones/prune`).

Validation:

- `dotnet test`: **52/52** pass. Self-test **15/15** on a fresh database (migration ledger at 9 entries).
- Live HTTP smoke test: first-run setup, login, role gating (coordinator blocked from `/users`,
  `/settings`, `/pheromones/json`), session revocation on password change, logout, last-admin
  protection, and CLI password recovery all verified.

## v1.8.1 — spec-ingestion + autonomy Phases 0–1

Schema bumped to **v8**. Autonomy is fail-closed and off by default.

Added:

- **Long-input / specification-ingestion handling**: mission goals over `long_input_threshold`
  are split into bounded, non-critical section-analysis tasks (run in parallel), a synthesis
  task, then verification. A failed section degrades the mission to Partial instead of aborting
  it. New `Task.Critical` flag generalises fault-tolerant fan-in. Config: `spec_ingestion_enabled`,
  `long_input_threshold`, `max_section_chars`, `max_section_tasks`.
- **Autonomy Phase 0 (rails)**: `objectives` backlog + `autonomy_runs` audit-trail tables,
  domain models, durable kill switch (`.anthill/STOP` + in-process flag), rate `BudgetGuard`,
  and the run-outcome circuit breaker. Config: `autonomy_enabled`, `autonomy_poll_seconds`,
  `autonomy_max_missions_per_hour`, `autonomy_max_missions_per_day`, `autonomy_max_consecutive_failures`.
- **Autonomy Phase 1 (Director loop, MVP)**: `ColonyDirector` works the backlog one mission at
  a time (charter-as-goal; the LLM Strategist is Phase 2), queue-for-review writes only.
  `--autonomous` boot flag and a control plane: `GET /autonomy/status`, `POST /autonomy/{start,stop}`,
  `GET /autonomy/runs`, and `/objectives` CRUD.

Fixed:

- Self-test now passes 15/15 on a **fresh** database: system/sentinel missions are seeded so
  event/message probes satisfy the `events → missions` foreign key on a clean install.

## v1.8.0 — .NET / native C++ hybrid migration

Language and platform migration of the v1.7.1 scheduler-hardening checkpoint from Python to
an idiomatic .NET 8 (C#) solution with a native C++20 compute kernel. Behaviour is preserved;
the harness gains a service-grade runtime, encryption at rest, and a caching speed layer.

Added:

- Native C++20 compute kernel (`native/anthill_kernel`) exposing a C ABI for pheromone
  reinforcement/decay, mission scoring, and dependency-graph cycle detection, bound via P/Invoke
  (`Native/NativeKernel.cs`) with a bit-identical managed fallback when the library is absent.
- AES-256-GCM field encryption for sensitive columns at rest (`Security/FieldCipher.cs`),
  keyed from `ANTHILL_ENCRYPTION_KEY` or an auto-generated owner-only workspace key file.
- `IMemoryCache` read-through speed layer over hot memory reads with generation-based invalidation.
- xUnit test suite: scheduler regressions, security regressions, and native-kernel equivalence.
- `Anthill.Cli` (`anthill`) with `--mission`, `--api`, `--selftest`, `--status`, `--config`, `--routes`.
- Reworked, animated colony console UI (Queen core, branching task tunnels, pheromone trails, live events).

Changed:

- `anthill/runtime.py` (7,177 lines) decomposed into a structured `Anthill.Core` class library;
  the FastAPI app became an ASP.NET Core minimal-API host (`Anthill.Api`).
- Pheromone trail strength updates remain exact (`round(strength + delta, 4)` clamped); mission
  scoring and cycle detection now run through the native kernel.
- Database access moved to `Microsoft.Data.Sqlite` with WAL, busy timeout, foreign keys, fully
  parameterised statements, and indexes on the hottest lookups.
- Version bumped to **1.8.0** to mark the language migration (schema version unchanged at 7).

Preserved (v1.6.4 / v1.7.x security + scheduler invariants):

- API token strength validation and constant-time comparison at boot.
- Failed-auth rate limiter (counts failures only); mission submission rate limiting.
- Security headers on every response; no public docs/OpenAPI endpoints.
- SSRF/local/private/non-http(s) URL filtering before agents or source records see a URL.
- Prompt-injection system-boundary prefix on every agent prompt.
- SQLite file hardening and automatic pre-mission DB backup.
- Tool gates (shell, file writing, patch application, web search) fail closed by default.
- Scheduler ownership of dependency validation, ready/blocked/skipped transitions, bounded
  retries, duplicate-id safety, lifecycle metadata, and metadata-first `task-graph-v2` export.
- Default graph export excludes full result summaries; `read_graph_results` gates the full export.

Validation:

- Native kernel: builds with g++/CMake; behaviour verified against the Python contract.
- `dotnet build` / `dotnet test`: scheduler, security, and native-kernel suites.
- `anthill --selftest`: framework self-test harness (15 checks).

## v1.7.1 (Python, prior baseline)

Scheduler hardening checkpoint. See the original `anthill_v1_7_1_codex_handoff/CHANGELOG.md`
for the full Python history through v1.6.4.
