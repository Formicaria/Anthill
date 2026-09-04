# ANTHILL — Mission Replay

**Status: configuration contract only.** This release declares the settings Mission Replay will be
built against. Nothing reads a vault, parses Markdown, generates a mission, schedules work, touches
memory or moves a pheromone yet.

## What it will be

Mission Replay will let the colony rerun selected historical tasks described in an external source
such as an Obsidian vault: a note you have explicitly approved becomes a replay mission, and that
mission executes through the normal colony path like any other.

## What configuring a vault does today

Nothing. This is the guarantee this release is making, so it is worth stating flatly:

```text
Configuring a vault does not import the vault into ANTHILL memory
and does not directly modify pheromones.
```

Pointing `mission_replay_vault_path` at a directory causes **no** missions to run, **no** files to
be read or written, **no** model calls, **no** background indexing and **no** workspace changes. The
path is validated when replay is switched on, and that is the extent of the filesystem contact.

## Settings

| Key | Type | Default | Environment override |
| --- | --- | --- | --- |
| `mission_replay_enabled` | bool | `false` | `ANTHILL_MISSION_REPLAY_ENABLED` |
| `mission_replay_vault_path` | string | `""` | `ANTHILL_MISSION_REPLAY_VAULT_PATH` |
| `mission_replay_tag` | string | `"anthill/replay"` | `ANTHILL_MISSION_REPLAY_TAG` |
| `mission_replay_learning_enabled` | bool | `false` | `ANTHILL_MISSION_REPLAY_LEARNING_ENABLED` |

All four are file-only: the settings surface cannot change them live. `mission_replay_enabled` gates
a capability that will eventually execute missions, and widening what the console may write without
a restart is a decision for the release that ships the engine, not a side effect of the release that
declares the keys.

`mission_replay_tag` is the tag a note must carry to be considered for replay, written in the note
as `#anthill/replay`. Nothing scans for it yet.

## Validation

Configuration is checked deterministically and reported through the same channel as every other
configuration health finding — startup events and `/config/health` — naming the key at fault.
Following `RuntimeConfigValidator`'s house rule, a misconfiguration **degrades loudly and never
refuses boot**: an operator with a half-configured feature needs a running console that explains the
problem, not a dead process.

| Configuration | Result |
| --- | --- |
| Replay off, no vault | Valid. The filesystem is not probed at all. |
| Replay off, stale or absent vault path | Valid. A path is only required when the feature is on. |
| Replay on, real vault directory | Valid and operable. |
| Replay on, empty `mission_replay_vault_path` | Reported; replay stays inoperable. |
| Replay on, vault missing or not a directory | Reported, naming the resolved path. |
| Replay on, empty `mission_replay_tag` | Reported; an empty tag matches nothing. |
| `mission_replay_learning_enabled` on, replay off | Reported; learning is inert. |

Your configuration file is never rewritten to a default behind your back. An invalid combination is
reported and the feature declines to operate; the values you wrote stay as you wrote them.

## Reading it in code

```csharp
AnthillRuntime.MissionReplay.IsOperable        // enabled AND configured in a way that can be honoured
AnthillRuntime.MissionReplay.VaultPath         // as configured
AnthillRuntime.MissionReplay.ResolvedVaultPath // absolute, or "" when unusable
AnthillRuntime.MissionReplay.ReplayTag
AnthillRuntime.MissionReplay.LearningEffective // false whenever replay itself cannot run
```

The whole group is one immutable value, replaced wholesale when configuration is projected, so it
cannot be edited field-by-field after validation has run. Future replay code gates on `IsOperable`,
never on `Enabled` alone.

## How learning will work

`mission_replay_learning_enabled` will eventually control whether **verified** replay results may
feed the existing pheromone and learning system. It never means an Obsidian note reaches a pheromone
directly. The intended path is:

```text
Obsidian note
  → replay mission
  → normal ANTHILL execution
  → verification
  → eligible result
  → existing ANTHILL learning / pheromones
```

A note reinforces nothing by existing. Only a mission that actually ran and was verified is eligible,
which is the same bar every other learning signal in the colony has to clear.

## Deliberately not built yet

Obsidian Markdown parsing, vault indexing, mission generation, replay execution, replay scheduling,
replay UI, pheromone modification, new memory databases, new agent roles, Forager integration and
background filesystem watchers. Those belong to later releases, and this one leaves ANTHILL behaving
exactly as it did before unless the new configuration is explicitly used.
