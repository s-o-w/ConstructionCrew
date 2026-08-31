# ConstructionCrew

A cross-platform, single-input-line .NET TUI that dispatches work to local CLI coding agents (Claude Code, Codex CLI and GitHub Copilot CLI today) through a construction-site metaphor:

**Boss** (whoever is running it) → **GC** (dispatcher, talks to the Boss directly) → **Foremen** (each working one **Jobsite**, a real repo, backed by a configurable CLI) → **Workers** (ephemeral sub-agents a Foreman spawns for a single piece of work) → **Tools** (the CLIs themselves).

A Jobsite may carry **more than one Foreman**. The picker offers every Jobsite, already-claimed ones included, and GC can dispatch a different workorder to each. Nothing tracks the reverse direction: which Foremen a Jobsite has is read off each Foreman's own `jobsiteName`.

No Semantic Kernel, no Agent Framework. Every brain is an external CLI agent process, so there is no in-process LLM call for a framework to wrap function-calling around. The shared control surface is a small MCP server ConstructionCrew hosts itself, the "Home Office". It serves over HTTP, not stdio, because GC and every Foreman need the control plane at once. It wires into each hired CLI's native MCP support.

**New here? Read [docs/USER-GUIDE.md](docs/USER-GUIDE.md).** It walks from first launch through a merged PR, including a second pass that starts a brand-new project from an empty folder.

## What's here today

**The dashboard.** Redrawn per Boss turn with Spectre.Console: a roster sidebar (each Jobsite's Foremen shown in that Jobsite's chosen border color), a tab strip, and one input line. Views switch by slash command at the same prompt, not by raw keys. Tabs are `/chat`, `/tasks` (a doing/parked/done/failed board off real job state), `/monitor` (a live table of who is working, one row per crew member plus one per in-flight Worker), and `/memory` (a modal vault browser scoped to the union of every hired crew member's vault folders, so unrelated notes are unreachable). `/view <path>` renders a crew-written `.md`/`.txt`/`.yaml`/`.yml` file as paged console output. The reachable set is a containment test against the Vault root and repo root, not a pattern filter.

**`/hire`** runs an Identity → Jobsite → Engine → Briefing wizard. It assigns the new Foreman to an existing Jobsite or creates one (name, repo path, description, default branch, build and test commands, a backlog URL, vault write scope, a border color), picks its CLI engine from those registered *and* on PATH, renders its instructions file from the briefing, and optionally dispatches the sitewalk right away.

**Brand-new projects.** The repo path may be a folder that does not exist yet: the wizard creates it and runs a bare `git init`. The first workorder then stands the project up. It asks the Boss which license through the GC, writes `README.md`/`LICENSE`/`.gitignore`, and makes the first commit, all before anything branches a Worker worktree off a repo with zero commits.

**`/fire`** removes a Foreman from this tool's own config and live state. Two invariants are directly tested: it never touches the Jobsite's repo working tree or anything in the Vault, and it **never removes the Jobsite**. Another Foreman may be assigned to it, and its config and `Notes/<Jobsite>`/`Plans/<Jobsite>` content may still be wanted. The one exception is `git worktree remove`/`prune`, which only rewrites `.git/worktrees/` bookkeeping for worktrees ConstructionCrew itself opened.

**`/foreman <Name>`** shows a crew member's details, plus the fields that are safe to edit after hire (and the Jobsite's own fields). Name, Role, WorkingDirectory and InstructionsFilePath are locked.

**`/drive <Foreman>`** routes subsequent Boss input to that Foreman's own persistent conversation instead of GC's, with a read-only git panel beside the chat. Routing only: no PTY, no terminal attach. `/exit` leaves the Foreman; `/exit` while not driving quits.

**`/settings`** offers Vault setup if none is configured, re-probes PATH for CLIs installed since startup, rewrites the Home Office MCP wiring, and shows what is wired. Discovery is cached to `state/tools.json`.

**Home Office MCP tools.** `dispatch_task`, `spawn_worker`, `ask_foreman`, `ask_gc`, `list_foremen`, `list_jobsites`, `get_job_status`, `file_sitrep`, `open_worktree`, `merge_worker_branch`, `close_worktree`, `build_graph`, `query_graph`. GC and each Foreman are continuation-aware (the same `--continue`-style mechanism the GC uses with the Boss), so a Foreman remembers its own prior turns and answers a Worker's `ask_foreman` question in context. Every call carries the job id the task text opened with. A wrong or missing id is rejected outright.

**Workers get their own git worktree**, on their own branch cut from the Foreman's feature branch, under `state/worktrees/<Jobsite>/worker-<id>`. Several Workers can run at once without clobbering each other. A Foreman merges each finished Worker's branch (`merge_worker_branch`) *before* closing its worktree (`close_worktree`), because closing deletes the branch.

**The adversarial review workflow.** The crew's standing workflow, run on every workorder: read and verify the workorder against the code, stand the repo up if it has no commits, cut the feature branch, write `PLAN.md`, have a Worker on a **different engine** review the plan for defects, fold every finding in as accepted or rejected, repeat up to three rounds, stop at a human gate for the Boss's go-ahead, implement, build and test, review the actual diff on a different engine again, open exactly one PR, report. It lives in the instructions template as literal prose, not in C# and not in a skill. Every CLI reads its instructions file as a system prompt, so Claude Code, Codex and Copilot all get the same workflow.

**One workorder per Foreman at a time.** A second dispatched while one is open is rejected by the Home Office. The slot is released by the `pr-opened` sitrep, not by a separate call.

**Sitewalks and sitreps live in the Vault.** A sitewalk is the read-only survey a Foreman runs first on a new Jobsite: read the code, the backlog and the docs, then write findings to `Notes/<Jobsite>/Sitewalk.md`. That file is a running record across every Foreman who has walked the Jobsite. Each Foreman appends under its own heading; an existing file is never overwritten. Sitreps are append-only under `Notes/<Jobsite>/Sitreps/`, in three kinds: `status` (records it), `milestone` (also escalates a one-line summary to GC), and `pr-opened` (frees the workorder slot and fires the Boss's notification). Notes carry `authoredBy: "Foreman:<Name>:<Jobsite>"`. The Foreman name is load-bearing because a Jobsite can have several.

**Instructions live in the Vault, not this repo.** Both templates and every crew member's rendered instructions file sit under `AI/ConstructionCrew/Templates/` and `AI/ConstructionCrew/Instructions/`, so they are editable where the rest of the second brain lives. The template masters ship under `config/scaffold/` and are seeded exactly once into a Vault that lacks them, never overwritten after. An edited template belongs to the Boss. A roster hired before this moved migrates automatically at startup, or on demand via **`/migrate`**. Both call one code path.

**Graph projection.** `build_graph` writes a Vault-LD RDF projection of the Vault to `AI/graph/build/schema.ttl` and `data.ttl`, and `query_graph` runs SPARQL over it. Pure .NET (dotNetRDF), verified by triple-level isomorphism against the Python script it ports.

**Optional Boss notifications.** A shell command template with `{event}`, `{jobId}` and `{foreman}` placeholders (`notify-send "ConstructionCrew: {event} ({foreman})"`, say). Unset means no process is ever spawned.

## Running it

```bash
dotnet build ConstructionCrew.slnx
dotnet run --project src/ConstructionCrew.App
```

**Do not manually `cp config/foremen.yaml.example config/foremen.yaml` before the first run.** Guided first-run setup triggers only when `config/foremen.yaml` does not exist yet. Copying the example first creates a hand-written roster, skips the wizard, and leaves you with a GC that was never pointed at a Vault. The example file shows the shape first-run setup writes; it is not a setup step.

On a fresh clone (no `config/foremen.yaml`), the app runs guided first-run setup: point it at an existing Vault or let it scaffold a new one, then hire the GC. This writes `config/foremen.yaml`, persists the Vault path into `appsettings.json` (merged into whatever is already there), and renders `AI/ConstructionCrew/Instructions/GC.md` in the Vault from `AI/ConstructionCrew/Templates/gc-instructions.md`, itself seeded from this repo's `config/scaffold/` the first time it is needed. Neither GC.md nor a live template ships in this repo.

A Vault is "recognized" when `HOME.md`, `CLAUDE.md`, `Notes/`, `Plans/` and `AI/` are all present. An unrecognized directory is still usable; the app just asks for the write scope instead of deriving it, and says which markers were missing.

(`foremen.yaml`/`jobsites.yaml`/`appsettings.json` are git-ignored, personal to whoever runs the tool. `jobsites.yaml` needs no setup; it is created the first time a Jobsite is added. `appsettings.json.example` shows the shape for reference; first-run setup writes the real file, so there is no need to copy it.)

You land at a prompt talking to the GC. Type `/help` for commands, `/exit` to quit. Anything not starting with `/` is sent to GC (or to the Foreman being driven) as a message and runs in the background, so typing never blocks on an agent turn.

`--vault-root <path>` and `--home-office-port <n>` (alias `--port`) override `appsettings.json`; `CONSTRUCTIONCREW_`-prefixed environment variables sit between the two in precedence. The port override lets a second instance run alongside the first.

**Note:** `ConstructionCrew.App.csproj` sets `<UseAppHost>false</UseAppHost>` on purpose. Endpoint security on some locked-down Windows machines blocks a freshly-built, unsigned native `.exe` that spawns child processes and opens a local socket ("Access is denied" starting the apphost). Without an apphost, `dotnet run`/`dotnet <dll>` loads the DLL through the already-trusted `dotnet.exe`. Do not remove this setting, and do not run a generated `ConstructionCrew.App.exe` directly.

First run needs at least one supported CLI already installed and authenticated in a real terminal (`claude`, `codex` or `copilot`). ConstructionCrew never automates a login. A provider is only offered if it is implemented in code **and** its binary resolves on PATH. `GeminiProvider` is registered on purpose but reports `IsImplemented == false`, so it is filtered out everywhere; its non-interactive flags have never been verified against a real install.

## Layout

```
config/foremen.yaml.example    # Reference only: what first-run setup writes; don't copy manually
config/jobsites.yaml.example   # Reference only: jobsites.yaml self-creates
config/foremen.yaml            # Hired Foremen + the GC (git-ignored)
config/jobsites.yaml           # Jobsites (git-ignored)
config/scaffold/               # Starter vault: both instructions templates, crew-preferences, graph ontology
config/generated/              # MCP config ConstructionCrew writes at startup (git-ignored)
appsettings.json.example       # Reference only: shows the shape; first-run setup writes the real file
appsettings.json               # Home Office port, Vault root, notification command (git-ignored)
docs/USER-GUIDE.md             # Full first-time walkthrough
state/                         # Runtime state: jobs.jsonl, tools.json, worktrees/ (git-ignored)
src/ConstructionCrew.Core/         # Domain models + interfaces, no external deps
src/ConstructionCrew.Providers/    # One class per CLI tool (CliWrap-based) + PATH discovery
src/ConstructionCrew.HomeOffice/   # The MCP control-plane server ("Home Office") and its tools
src/ConstructionCrew.Config/       # YAML config, instructions rendering/migration, vault layout, sitrep/run logs
src/ConstructionCrew.Git/          # Worktree management and read-only workspace inspection
src/ConstructionCrew.Graph/        # Vault-LD RDF projection + SPARQL (dotNetRDF)
src/ConstructionCrew.App/          # Spectre.Console TUI + composition root
tests/ConstructionCrew.Tests/      # Fakes only, no real agent process spawns
```

The live templates and every crew member's rendered instructions file live in the Vault, not this tree. Workorders, plans, run logs, sitewalks, sitreps and delivery notes are Vault content too. See [docs/USER-GUIDE.md](docs/USER-GUIDE.md) for the full map of what lands where.

## License

Apache-2.0. See [LICENSE](LICENSE).
