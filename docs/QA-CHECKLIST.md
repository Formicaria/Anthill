# ANTHILL QA Checklist

> For independent testers. Fill in every **Result** cell with `PASS`, `FAIL`, `DEGRADED`, or
> `SKIPPED` (say why), and add notes — an observed detail beats a bare verdict. File one report
> per environment. Nothing here modifies a repository you care about: use the throwaway
> directories the steps create.

## 0. Tester & environment

| Field | Value |
|---|---|
| Tester name / handle | |
| Date | |
| Anthill version (Settings → the version in the sidebar) | |
| Install shape (Windows installer / Windows zip / Linux binary / Docker / LXC) | |
| OS + version | |
| Model provider(s) configured (Ollama / Claude Code / other) | |
| Node.js installed? (`node --version`) | |
| Git installed? (`git --version`) | |

---

## 0.5 Qualification — run this FIRST

Before anything else, ask the installation whether it can run missions at all:

```
anthill --qualification
```

Safe by construction: it uses a temporary workspace and database and never opens your colony,
projects or repositories. Exit code 0 means qualified.

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 0.5.1 | Run `anthill --qualification` | Every check `PASS` (warnings acceptable when named optional, e.g. git missing); final line `QUALIFIED`; exit code 0 | | |
| 0.5.2 | If NOT QUALIFIED: follow the remediation in each FAIL line, re-run | Turns into QUALIFIED, or file the FAIL lines verbatim in your report | | |
| 0.5.3 | Paste the full qualification output into your report | (It contains no secrets — versions, check names and availability facts only) | | |

## 1. Installation & first run

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 1.1 | Install from `anthill-setup-<version>.exe` (Windows) or unpack the archive | License agreement shown; installs without admin errors; Start Menu + desktop shortcut created | | |
| 1.2 | Desktop/Start-Menu icon | The Formicaria mark (terminal + node legs), **not** a generic Windows icon | | |
| 1.3 | Launch the app | Branded loading screen (dark, ANTHILL wordmark, version in amber), then the console — no blank window, no white flash | | |
| 1.4 | Title bar (Windows) | Dark, matching the app theme — not white | | |
| 1.5 | Watch for stray console windows during normal use (open Files tab, browse, chat) | **Zero** CMD/console windows flash at any point | | |
| 1.6 | Log in / first-run auth | Reaches the console without confusion | | |
| 1.7 | Uninstall → reinstall (optional, end of session) | Add/Remove entry present; data under `%LOCALAPPDATA%\Anthill` survives | | |

## 2. First-thing-people-do: agents & local runtime

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 2.1 | Tools → Integrations → agents page | "Ollama (local models)" is the FIRST card | | |
| 2.2 | (Windows) Click Install on Ollama | winget install runs end to end, or a named, actionable failure — never a raw exit code | | |
| 2.3 | (Linux/Docker) Ollama card | No dead Install button; the exact command is shown instead | | |
| 2.4 | Install a vendor agent you have prerequisites for (e.g. Claude Code with Node present) | Installs into Anthill's own directory; page shows Installed + version after | | |
| 2.5 | Install an agent WITHOUT its prerequisite (e.g. Aider with no Python) | Refusal names the missing prerequisite **for your OS** (winget hint on Windows — never `sudo apt` there) | | |
| 2.6 | Sign in to the agent per its card's auth command, in your own terminal | Anthill never asks for the vendor credential itself | | |

## 3. Projects & working directories

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 3.1 | Create a new project | Appears in Projects; opening it shows the workspace (Chat/Objectives/Schedules/History/Settings tabs) | | |
| 3.2 | + New Conversation from the project page | A conversation is created immediately, opens in Chat, appears in the left tracker labeled with the project name | | |
| 3.3 | Send a message BEFORE setting a working directory | Refused with a clear remedy; your message stays in the composer; the Files pane opens | | |
| 3.4 | Files tab on the new project | Set-directory form shown, **prefilled** with `<workspace root>/projects/<name>-<id>` | | |
| 3.5 | Click Browse | Desktop app: real OS folder dialog. Browser: inline server-side directory browser (Home/roots/navigate/Use this folder) | | |
| 3.6 | Accept the suggested path | Directory is created; empty tree renders; badge reads `no git` (two words, no error wall) | | |
| 3.7 | First chat message after setting the directory | Answered; conversation title becomes your first message; header shows the project name under the title | | |
| 3.8 | Make a second project; set a different directory | The two projects show DIFFERENT trees — no shared directory, no cross-bleed | | |
| 3.9 | Point a project at a folder nested inside some larger git repo | Badge says plain folder and names the enclosing repo; Commit is refused — the enclosing repo's branch is never shown as the project's | | |

## 4. Git in the Files pane

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 4.1 | Non-repo working directory | Badge `no git`; **Init git** button visible | | |
| 4.2 | Click Init git → confirm | Repo created; badge flips to `⎇ main · clean` immediately (no stale state); Init git disappears | | |
| 4.3 | Empty repo (no commits yet) | Badge shows the real branch name — never a `fatal:` message, never text pushing buttons off screen | | |
| 4.4 | Create a file (+ File) | Browse-style picker over the project tree; navigate, name it, Create here; file opens in the docked editor | | |
| 4.5 | Create a folder (+ Folder) | Same picker, folders only | | |
| 4.6 | Edit + Save a file | Save persists; git badge dirty count rises | | |
| 4.7 | Commit from the pane | Commit lands with your message; badge returns to clean; commit train (clock icon) shows it | | |
| 4.8 | Branch dropdown | Lists branches; switching reads history without checking anything out | | |
| 4.9 | Dir button | Reopens the set-directory form; changing the directory refreshes tree + badge immediately | | |

## 5. Chat, approvals, missions

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 5.1 | Set the approval gate BEFORE opening any conversation | Selector accepts the choice; state line says it will apply to the next conversation; the created conversation carries it | | |
| 5.2 | Change gate on an open conversation | Applies; Skip-all requires a typed confirmation; refusal snaps the selector back | | |
| 5.3 | Simple question in chat (Manual approval) | Streamed answer; no unrequested file changes | | |
| 5.4 | Ask for real work (e.g. "add a README to this project") under Manual approval | Colony proposes a mission / asks for approval — it does NOT act unprompted | | |
| 5.5 | Approve the mission | Work runs; patches appear as review cards; approving applies them; files really change | | |
| 5.6 | Same ask under Skip all approvals | Work happens directly; the colony's changes are auto-committed with your ask as the subject; your own uncommitted edits in the same tree are NOT swept into that commit | | |
| 5.7 | Stop button during a running mission | Work stops; conversation shows stopped; no orphan processes (check task manager / `ps`) | | |
| 5.8 | Restart the app mid-conversation | Conversations, projects, and history all survive; nothing runs twice | | |
| 5.9 | Export a conversation | Markdown file downloads with the transcript and decision log | | |

## 6. Console pages (walk every one)

Mark any page that errors, renders empty when it shouldn't, or shows obviously wrong data.

| Page | Result | Notes |
|---|---|---|
| Colony → Overview (map draws, ants reflect real state) | | |
| Colony → Models & Routing | | |
| Colony → Ant Inspector (click an ant; edit name/color) | | |
| Colony → Automation | | |
| Projects list | | |
| Chat (+ Files pane, + Colony pane, splits drag & persist) | | |
| Tools → Capabilities | | |
| Tools → Integrations (Configure opens inline; Test connection answers) | | |
| Tools → Memory & Signals | | |
| Settings → General | | |
| Settings → Security & Gates | | |
| Settings → Users | | |
| Settings → System (event log populated) | | |
| Settings → Readiness | | |
| Settings → Terminal (a safe command like `git status` runs; **no console window flashes**) | | |
| Homelab deck (if configured) | | |
| Sidebar mark → opens formicaria.us in your real browser | | |

## 7. Ugly-input hardening (try to break it)

| # | Step | Expected | Result | Notes |
|---|---|---|---|---|
| 7.1 | Project named `<script>alert(1)</script> & Co.` | Renders literally everywhere (tracker, header, subtitle) — no popup, no broken layout | | |
| 7.2 | Working directory path with spaces + accents | Everything still works (tree, git, agent runs) | | |
| 7.3 | Drag the chat↔files split to both extremes | Buttons never leave the screen or change size; the path text is what shrinks; controls wrap if truly out of room | | |
| 7.4 | Very long conversation first-message | Tracker row and header ellipsize; nothing overflows | | |
| 7.5 | Kill Ollama (if used) mid-session | Errors name the problem and the remedy; nothing crashes; status dot goes red | | |

## 8. Sign-off

| Field | Value |
|---|---|
| Total PASS / FAIL / DEGRADED / SKIPPED | |
| Worst defect found (one sentence) | |
| Would you hand this build to a non-technical user? (yes / no / with caveats) | |
| Attachments (screenshots, exported logs — redact tokens/paths you consider private) | |

**Filing results:** open a GitHub issue titled `QA <version> — <OS> — <PASS/FAIL count>` with this
file filled in, or hand it back through your agreed channel. One issue per environment.
