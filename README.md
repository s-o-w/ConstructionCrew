# ConstructionCrew

A cross-platform, keyboard-driven .NET TUI that dispatches work to local CLI
coding agents (Claude Code today; Codex CLI, GitHub Copilot CLI, Gemini CLI
planned) through a construction-site metaphor:

**Boss** (you) → **GC** (dispatcher, talks to you directly) → **Foremen**
(each assigned to exactly one **Jobsite** -- a real repo -- and backed by a
configurable CLI) → **Workers** (ephemeral sub-agents a Foreman spawns for a
single piece of work) → **Tools** (the CLIs themselves).

No Semantic Kernel, no Agent Framework: every "brain" here is a real external
CLI agent process, not a hosted chat-completion endpoint, so there's no
in-process LLM call for an agent framework to wrap function-calling around.
The missing piece -- a shared control surface those independent CLI processes
can all reach -- is a small MCP server ConstructionCrew hosts itself (the
"Home Office"), wired into every hired CLI's own native MCP support.

## What's here today

- A redrawn-per-turn Spectre.Console dashboard: a roster sidebar (with each
  Jobsite's Foreman shown in that jobsite's chosen border color), a tab strip,
  and a chat/tasks/hire view. Not raw-keyboard-driven -- switching views is a
  slash command at the same input line.
- `/hire` -- an Identity → Jobsite → Engine → Briefing wizard. Assigns the new
  Foreman to an existing Jobsite or lets you create one (name, an *existing*
  local repo clone, a description, a border color), picks its CLI engine from
  whatever's actually registered, and writes its instructions file from your
  briefing text.
- `/fire` -- removes a Foreman and its Jobsite from this tool's own config.
  **Hard invariant:** never touches the Foreman's working directory or the
  Jobsite's repo path on disk -- only `foremen.yaml`/`jobsites.yaml`/the
  generated instructions file. Directly tested, not just reviewed.
- `/tasks` -- a live doing/done/failed board sourced from real job state.
- `dispatch_task` / `spawn_worker` / `ask_foreman` / `list_foremen` /
  `list_jobsites` / `get_job_status` -- the MCP tools the Home Office exposes.
  A Foreman is continuation-aware (the same `--continue` mechanism the GC uses
  with you) so it remembers its own prior turns and can answer a Worker's
  `ask_foreman` question coherently, not just run one-shot.

## Running it

```bash
dotnet build ConstructionCrew.slnx
dotnet run --project src/ConstructionCrew.App
```

**Do not manually `cp config/foremen.yaml.example config/foremen.yaml` before
your first run.** Guided first-run setup only triggers when `config/foremen.yaml`
does not exist yet -- copying the example file first creates a hand-written
roster, skips the wizard entirely, and drops you at a prompt with a GC that was
never pointed at a Vault. `foremen.yaml.example` is a reference showing the
shape first-run setup itself writes, not a manual setup step.

On a genuinely fresh clone (no `config/foremen.yaml`), the app runs guided
first-run setup instead: point it at an existing Vault or let it scaffold a new
one, then hire the GC. This writes `config/foremen.yaml`, `appsettings.json`
(the Vault path), and `config/instructions/GC.md`.

(`foremen.yaml`/`jobsites.yaml` are git-ignored -- personal to whoever's
running the tool, never committed. `jobsites.yaml` doesn't need any setup; it's
created automatically the first time you add a Jobsite.)

You'll land in a prompt talking to the GC. Type `/help` for commands, `exit`
to quit.

**Note:** `ConstructionCrew.App.csproj` sets `<UseAppHost>false</UseAppHost>` on
purpose. A freshly-built, unsigned native `.exe` that spawns child processes and
opens a local socket gets blocked outright by endpoint security on some locked-
down Windows machines ("Access is denied" starting the apphost, with a block
toast). Without an apphost, `dotnet run`/`dotnet <dll>` loads the DLL through
`dotnet.exe` itself, which is already trusted. Don't remove this setting, and
don't try to run a generated `ConstructionCrew.App.exe` directly.

First run needs `claude` already authenticated in a real terminal
(`claude login`) -- ConstructionCrew never automates that.

## Layout

```
config/foremen.yaml.example    # Reference only -- what first-run setup writes; don't copy manually
config/jobsites.yaml.example   # Reference only -- jobsites.yaml self-creates
config/foremen.yaml            # Your hired Foremen + the GC (git-ignored)
config/jobsites.yaml           # Your Jobsites (git-ignored)
config/instructions/GC.md      # The one generic instructions file every install needs
config/instructions/*.md       # Per-Foreman instructions, written by /hire (git-ignored, except GC.md)
config/generated/              # MCP config ConstructionCrew writes at startup (git-ignored)
sandbox/                       # Scratch working directory (git-ignored)
state/                         # Runtime state (git-ignored)
src/ConstructionCrew.Core/         # Domain models + interfaces, no external deps
src/ConstructionCrew.Providers/    # One class per CLI tool (CliWrap-based)
src/ConstructionCrew.HomeOffice/   # The MCP control-plane server ("Home Office")
src/ConstructionCrew.Memory/       # Shared-memory abstraction (MemPalace lands here later)
src/ConstructionCrew.Config/       # YAML config loading/writing
src/ConstructionCrew.App/          # Spectre.Console TUI + composition root
tests/ConstructionCrew.Tests/      # Fakes only, no real process spawns
```

## License

Apache-2.0 — see [LICENSE](LICENSE).
