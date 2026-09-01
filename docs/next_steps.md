# Next Steps

Answers to the architectural questions, followed by concrete work items.

---

## Q1 — Why doesn't GC's response stream live to the main panel?

**Short answer:** the current code buffers everything. `CliProcessRunner` uses CliWrap's `ExecuteBufferedAsync`, which holds stdout/stderr in memory until the process exits, then hands back one big string. `ClaudeCodeProvider` adds `--output-format json`, which tells the Claude CLI to emit a single JSON result envelope at the end. So neither the transport nor the format gives us anything to render until the turn is done.

**Why it doesn't have to be that way:**

The Claude CLI has `--output-format stream-json`. That mode emits one JSON object per line, to stdout, as the model responds — each line is a typed event (`content_block_delta`, `message_delta`, etc.). CliWrap supports `PipeTarget.ToDelegate` for streaming: instead of buffering, it calls a callback on each line as it arrives. Wiring the two together gives real-time chunks that can be forwarded to the events channel and accumulated in the transcript mid-turn.

**What changes:**

1. `CliProcessRunner` — add a second method (or extend `RunAsync`) that accepts a line-by-line callback and uses `PipeTarget.ToDelegate` instead of `ExecuteBufferedAsync`.
2. `ClaudeCodeProvider` — switch `--output-format json` to `--output-format stream-json`; parse the streaming event shape instead of the single-result envelope.
3. `JobRegistry` / event pipeline — instead of one `JobTransition.Completed` with the full result, emit partial `ContentDelta` events that the live loop accumulates in the active transcript.
4. `Dashboard` — no changes needed; the transcript already renders dynamically every refresh.

Codex has its own streaming shape (it's different from Claude's). The same pattern applies — swap the buffered call for a streaming one and parse the events as they arrive.

---

## Q2 — Why can't each agent have a dedicated output panel, with my input funneled to whoever I'm driving?

**Short answer:** stdin is currently `PipeSource.Null` — every agent process starts with stdin closed. They're fire-and-forget jobs: you send a task, a subprocess runs until done, and the result comes back. There's no open channel to send more input mid-turn.

**Why it doesn't have to be that way:**

A persistent interactive model is achievable. Instead of spawning a new process per task, you keep one long-running process per agent and communicate via open stdin/stdout pipes. The main panel becomes GC's dedicated output pane; the watch panel becomes the driven foreman's output pane; the footer input line funnels text to whoever has the drive.

**What changes:**

1. `ICliAgent` / `LiveAgentRegistry` — add a persistent-process mode: a process that stays alive, reading from a piped stdin and writing streaming output. The `ICliAgent.SendAsync(line)` method writes a line to that process's stdin.
2. `CliProcessRunner` — add a `StartPersistent(invocation, onOutputLine, ct)` path that keeps stdin open and streams stdout to a callback.
3. `ClaudeCodeProvider` / `CodexProvider` — each CLI already supports conversational/interactive use; the flags that put them into persistent-chat mode need to be identified and wired.
4. `DashboardState` — the GC output accumulates in `Transcript` exactly as today; the driven foreman's output accumulates in its per-foreman transcript (already exists). The panels already render these. The only new thing is the panels update on partial output rather than waiting for job completion.
5. Input routing — the footer input already knows who you're driving (`state.DrivenForeman`). The only change is writing to the foreman's stdin pipe instead of calling `jobRegistry.StartJob`.

This is the largest architectural change of the four. The job-registry model (pending/running/completed records) is still useful for tracking work, but the execution model shifts from "subprocess per task" to "persistent process per foreman."

---

## Q3 — Replace the Tasks pane with a richer Jobs pane

**Current situation:** `/tasks` renders a kanban board (doing / parked / done / failed) with foreman name, task excerpt, and summary. `/job` is a separate command that shows one job's full detail interactively. Neither is in the live dashboard on its own tab.

**The proposal:**

Remove the `/tasks` tab and replace it with a **Jobs** tab that shows a live detail list — all jobs, current status, elapsed time, the last activity line for running jobs, and the result summary for finished ones. The monitor tab (`/monitor`) already shows crew state; the jobs tab should show task state. The two together give the full picture without needing a separate `/job` command.

**What changes:**

1. `Dashboard` — remove `BuildTasks()` and the `/tasks` handler; add `BuildJobs()` that renders a scrollable detail list rather than a kanban board.
2. `BossEvent` / `HandleBossLine` — remove the `/tasks` command; keep `/job` for a focused single-job drill-down if still useful.
3. The `JobRecord` already carries everything needed: `Task`, `Status`, `StartedAt`, elapsed via `ParkedDuration`, `Summary`. No model changes required.

---

## Q4 — Agent-to-agent communication via filesystem hooks

**The idea:** instead of the Boss loop mediating everything (Boss → dispatch → GC → dispatch_task tool → foreman), agents watch folders and react to file changes. Drop a workorder file, a foreman picks it up. A foreman writes a result file, GC picks it up. Agents stay idle until their watched folder changes, then work, then go idle again — no polling, no hub-and-spoke through the Boss.

**Why this is realistic:**

- Claude Code already has a hooks system: you can configure shell commands (or `claude -p` invocations) to fire on events like file edits, file creation, and session completion. The hook config lives in `.claude/settings.json`.
- The vault already has a folder structure that maps cleanly to roles: `Plans/XINFRA/*/` for XINFRA tasks, `AI/SessionNotes/` for output, `Notes/XINFRA/` for reference. Assigning watch folders to roles is already how the vault is organized.
- .NET `FileSystemWatcher` can do the same thing on the ConstructionCrew side: watch a folder, fire a callback when a file appears, route it to the right agent.

**How it would work:**

```
Boss drops workorder.md into Plans/XINFRA/FEATURE_NAME/
    → GC's file watcher fires
    → GC reads the workorder, breaks it into subtasks
    → GC writes task-001.md, task-002.md to Plans/XINFRA/FEATURE_NAME/tasks/
    → Each task file triggers the assigned foreman's watcher
    → Foreman reads task-001.md, does the work, writes result-001.md
    → GC's watcher fires on result-001.md
    → GC reads the result, updates the feature plan, posts a sitrep
```

**What changes:**

1. `HomeOffice` — add a `FileWatcherService` that wraps `FileSystemWatcher`, maps folder patterns to agent names, and calls `jobRegistry.StartJob(agentName, fileContent)` when a match fires.
2. Vault folder → agent mapping — configurable in `appsettings.json` or a dedicated `watchers.yaml`. Each rule: `{ folder, pattern, agent, action }`.
3. Claude Code hooks (agent side) — add hook config to each foreman's instruction file or the shared preferences file. Hooks fire `claude -p "process this: $FILE"` when a matched file appears. The hook config is already supported by the CLI; it just needs to be wired into the foreman's working directory.
4. Workorder format — define a minimal file shape (`# Task\n...\n# Context\n...`) that agents can reliably parse. The vault's existing frontmatter + markdown body is already close.

**The bigger implication:** this moves ConstructionCrew toward a model where the Boss is optional for routine work. The Boss sets up watchers and roles once; agents coordinate through files while the Boss watches the panels. The Boss only intervenes when agents park (waiting for a decision) or produce a sitrep.

---

## Work item summary

| # | Item | Scope | Depends on |
|---|------|-------|-----------|
| 1 | Streaming stdout for GC and foremen | `CliProcessRunner`, `ClaudeCodeProvider`, `CodexProvider`, event pipeline | — |
| 2 | Persistent interactive process per agent | `ICliAgent`, `LiveAgentRegistry`, `CliProcessRunner`, providers | #1 (streaming) |
| 3 | Dedicated GC output panel (main pane) | Already works once #1 lands; panel already exists | #1 |
| 4 | Dedicated foreman output panel (watch pane) | Already works once #1+#2 land | #1, #2 |
| 5 | Remove Tasks tab, add Jobs detail tab | `Dashboard`, `HandleBossLine` | — |
| 6 | FileSystemWatcher → agent dispatch | `HomeOffice`, new `FileWatcherService` | — |
| 7 | Claude Code hook config per foreman | Foreman instruction templates, vault preferences file | #6 |
| 8 | Workorder file format + folder conventions | Vault schema, `HomeOffice` workorder reader | #6, #7 |

Items 1–4 are a chain: streaming unblocks everything else in the live-output direction. Items 5, 6–8 are independent tracks and can start immediately.
