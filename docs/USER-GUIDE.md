# ConstructionCrew User Guide

This guide walks through what the app does today, in the order you meet it. It
is written for the Boss: the person who owns the projects and hands out the
work, not the person who wrote the code.

There are two walkthroughs, because the two ways to start are different:

- **Sections 1-7: an existing project.** The code is already cloned somewhere
  and already tracked in your vault.
- **Section 8: a brand-new project.** No folder, no git repo, nothing in your
  vault. The app creates the folder and runs `git init`. The Foreman does the
  first-time setup (license, README, first commit) on its first real dispatch.

The scenario for the first walkthrough:

> I am a new Boss. I want to hire a Foreman to work on my **Lighthouse**
> project, so it can build me a CSV export feature. Lighthouse is a real repo I
> already have cloned at `~/code/lighthouse`, and I already track it in my
> Obsidian vault at `~/vault`, where it has a `Notes/Lighthouse/` folder and a
> `Plans/Lighthouse/` folder.

Two things every prompt in the app depends on:

- **The Jobsite's repo path is the code repo, not the vault.** For Lighthouse
  that is `~/code/lighthouse`. The Foreman works there. It cuts branches and
  opens the PR there.
- **The Vault folders are vault-relative.** For Lighthouse those are
  `Notes/Lighthouse` and `Plans/Lighthouse`. Not full paths, never anything
  outside the vault. That is the only place in the vault the Foreman may write.

---

## The cast

| Role            | Who it is                                                        | Where it works                             |
| --------------- | ----------------------------------------------------------------- | ------------------------------------------ |
| **Boss**        | You, at the keyboard                                              | The ConstructionCrew window                |
| **GC**          | One agent, hired automatically, that you talk to directly         | Your vault                                 |
| **Foreman**     | One or more agents per Jobsite, hired by you                      | That Jobsite's repo                        |
| **Worker**      | A throwaway helper a Foreman spawns for one piece of work         | Its own git worktree                       |
| **Home Office** | ConstructionCrew itself, exposed to the agents as a set of tools  | In-process, on `http://127.0.0.1:5199/mcp` |

You talk to GC, or to one Foreman directly when you choose to. GC talks to the
Foremen. Foremen talk to their Workers. Nobody talks to you unless you ask.

---

## Before you start

You need at least one coding CLI installed and already logged in: `claude`,
`codex`, or `copilot`. ConstructionCrew never logs you in. It runs the CLI you
already have. If you use Claude Code, run `claude login` once in a normal
terminal first.

Two installed engines is better than one. The review steps want a *different*
engine to review the first one's work. With only one installed, every review is
weaker and the Foreman is told to say so.

Start the app from the ConstructionCrew repo:

```bash
dotnet build ConstructionCrew.slnx
dotnet run --project src/ConstructionCrew.App
```

> **For the guided setup, skip the README's "copy
> `config/foremen.yaml.example` to `config/foremen.yaml`" step.** First-run
> setup triggers only when `config/foremen.yaml` does not exist. Copying the
> example file first creates a hand-written roster and skips the wizard. If you
> copied it already, you are not stuck: `/settings` offers vault setup, and
> every startup repairs a hand-written GC's tool policy and vault write scope
> and tells you what it repaired.

---

## 1. First launch

On a fresh install there is no roster file, so the app runs first-run setup
before the dashboard appears. You get a plain sequence of prompts, not the
full-screen view.

**What it asks, in order:**

1. **Vault: where does the crew's knowledge live?** Two choices:
   - *point at an existing Vault*: type the path to a directory that already
     exists.
   - *scaffold a new Vault*: type a path and the app copies a starter vault
     into it (`HOME.md`, `CLAUDE.md`, `Notes/`, `Plans/`, a small graph
     ontology, and `AI/ConstructionCrew/crew-preferences.md`). It never
     overwrites a file that is already there.

   For our scenario, choose *point at an existing Vault* and enter `~/vault`.

2. It then tells you whether it recognized the layout. "Recognized vault
   layout" means it found `HOME.md`, `CLAUDE.md`, `Notes/`, `Plans/` and `AI/`
   at the root. On a recognized vault, `/hire` derives a Foreman's vault
   folders itself and asks you to confirm them. An unrecognized vault works
   too; you type the folders in by hand.

3. **Engine: which CLI backs the GC?** If only one CLI is installed it is
   picked for you with no prompt.

4. **Display name for the GC**: optional. Blank calls it "GC". The name `GC`
   itself is reserved and cannot be changed.

5. A confirmation panel, then **Hire this GC?**

**What gets written:**

- `config/foremen.yaml`: your roster, with the GC entry. GC's working directory
  is the *vault*, and the ConstructionCrew repo is added as a readable extra
  directory. GC's vault write scope is `Notes/GC`, `Notes` and `Plans`. It
  needs the last two to write workorders under `Plans/<Jobsite>/<Feature>/` and
  delivery notes under `Notes/<Jobsite>/Deliveries/`.
- `appsettings.json`: gains `Vault.Root` pointing at the vault you chose.
  Anything else already in that file, such as the port, is preserved.
- `AI/ConstructionCrew/Instructions/GC.md` (in the Vault): GC's instructions,
  rendered fresh from `AI/ConstructionCrew/Templates/gc-instructions.md` (also
  in the Vault, seeded from the repo's own `config/scaffold/` copy the first
  time a vault does not have one). Neither file ships as a live copy in the
  repo. The app re-checks GC's instructions file at *every* startup, not just
  the first, and writes it again if it has gone missing. You never need to
  delete it to get a current copy.

**Also ensured at every startup, for whatever vault is configured:**
`AI/ConstructionCrew/crew-preferences.md` and both instructions templates under
`AI/ConstructionCrew/Templates/`. GC and every Foreman are told to read
crew-preferences.md and use it as the tiebreaker whenever two options are
equally good, so the app makes sure it exists even on a vault you pointed at
rather than scaffolded. It ships with commented-out examples; fill in the
reviewer-engine and convention sections when you have opinions. The templates
are yours to edit too, in the vault, where the rest of your notes live. A later
start never overwrites your edit.

**A roster hired before instructions lived in the Vault** migrates
automatically the next time you start the app, or on demand via `/migrate`.
Each crew member's rendered instructions file, and any briefing sidecar, moves
from the old `config/instructions/` in the repo to
`AI/ConstructionCrew/Instructions/` in the Vault, and `config/foremen.yaml` is
rewritten to match. A vault missing its `AI/` folder counts as unrecognized,
the same as one missing `Notes/` or `Plans/`, so `/hire` asks for paths instead
of deriving them.

Then the dashboard appears: a roster sidebar on the left showing GC, a tab
strip (chat, tasks, hire, memory, monitor), a footer listing the commands, and
a `Boss>` prompt at the bottom. Type anything that is not a slash command and
it goes to GC.

### Configuring a vault later

If you skipped setup, or hand-wrote your roster, type `/settings`. With no
vault configured it offers *"Configure a Vault now?"* and runs the same
existing-or-scaffold flow. It also repoints GC's working directory at the vault
if it was pointed somewhere else, and keeps the ConstructionCrew repo readable.

With a vault already configured, `/settings` shows you the path and will not
change it. That is deliberate. To move the vault, edit `appsettings.json`
directly.

`/settings` always re-scans for installed CLIs and shows a table: which
providers are implemented, which are on your PATH, and which are wired
up to the Home Office. Use it after installing a new CLI so it becomes
hireable without a restart. The re-scan also re-stamps everybody's Home Office
wiring, so nothing points at a stale config path.

---

## 2. Hiring a Foreman for Lighthouse

GC is already hired. Now you need somebody who works in the Lighthouse repo.
Type:

```
/hire
```

With no vault configured, `/hire` refuses and sends you to `/settings` first.
Otherwise you get this sequence:

1. **Name this Foreman.** A person-name works well: `Casey`. `GC` is refused,
   and so is a name already on the roster.

2. **Workspace: pick a jobsite, or add a new one.** On your first hire the only
   option is *"+ add a new jobsite"*. More than one Foreman can share a
   Jobsite, and GC can dispatch a different workorder to each, so an
   already-claimed Jobsite is still offered. It is labeled with who is already
   there (`Lighthouse (already assigned: Casey)`), not hidden.

   Adding a new one asks, in this order:

   - **Jobsite name**: `Lighthouse`. The workorder path has to match this name
     exactly later, so pick the spelling you mean. Type `cancel` at this prompt
     or the next one to back out of hiring entirely.
   - **Repo path**: `~/code/lighthouse`. This is the code repo, *not* the
     vault. An existing directory is taken as-is. A path that does not exist is
     offered for creation. That is the brand-new-project path, covered in
     section 8.
   - **Repo URL**: optional, blank to skip. Fill it in if you can: the Foreman
     needs a remote to push a branch and open a PR.
   - **Default branch**: blank means `main`.
   - **Build command**: blank if you do not know yet. Example:
     `go build ./...`
   - **Test command**: same. Example: `go test ./...`
   - **Description**: free text, one line per line, blank line to finish. This
     goes into the Foreman's instructions verbatim, so write a sentence or two
     about what the project is. Example: `A Go service that ingests sensor
     readings and serves them over HTTP.`
   - **Color**: a border color so you can tell Jobsites apart in the roster.
     "surprise me (random)" is fine.
   - **Vault folders**: on a recognized vault, the app shows the default it
     derived from the Jobsite name (`Notes/Lighthouse, Plans/Lighthouse`) and
     asks *"Use these?"*. Say yes for Lighthouse. Say no and you type them in
     yourself, one per line. On an unrecognized vault it asks with no default.
     Always vault-relative (`Notes/Lighthouse`), never absolute.

   > **Worth knowing:** `Notes/<Jobsite>` and `Plans/<Jobsite>` is a sensible
   > default, not a rule. Plenty of real projects live somewhere else in a
   > vault: a personal project might sit at `Personal/Projects/<Name>/`. That
   > is what the "Use these?" prompt is for. Say no and give the real paths.

   Right after the repo path is settled, the app checks whether that directory
   is already a git repository and runs `git init -b <default branch>` if it is
   not. On an existing clone this is a no-op you never see.

3. **Engine: which CLI backs this Foreman?** Pick from the installed list. Pick
   a *different* engine than another Foreman: the review step later wants a
   second engine to review the first one's work.

4. **Briefing: describe this Foreman's role and goal.** Free text, blank line
   to finish. This goes at the very top of the Foreman's instructions, above
   everything else, so it is the strongest thing it reads. Something like:

   ```
   You are Casey, the Foreman for Lighthouse. Lighthouse is a Go service that
   ingests sensor readings. Keep changes small and always add tests.
   ```

   Leave it blank and it defaults to "You are the Casey Foreman."

5. A confirmation panel, then **Spawn this Foreman?**

**What gets written:**

- `config/jobsites.yaml` gains the Lighthouse entry, with the branch and the
  build and test commands you supplied.
- `config/foremen.yaml` gains Casey, pointed at `~/code/lighthouse` as its
  working directory, with the vault added as a readable directory and
  `Notes/Lighthouse` plus `Plans/Lighthouse` as its write scope.
- The vault folders are created on disk right then, and named back to you
  (`Vault folders ready: ...`), so a typo shows up immediately rather than at
  the first write. An entry that resolves outside the vault is refused and
  reported instead of created.
- `AI/ConstructionCrew/Instructions/Casey.md` (in the Vault): the full Foreman
  instructions, rendered from `AI/ConstructionCrew/Templates/foreman-instructions.md`
  (also in the Vault) with your briefing, the Jobsite details, the vault paths,
  and the list of engines available on this machine.
- `AI/ConstructionCrew/Instructions/Casey.briefing.md` (in the Vault): your
  briefing kept verbatim, so the instructions can be re-rendered later without
  asking you for it again.

6. Finally: **Run Casey's sitewalk now?** Say yes. Section 4 covers it. If you
   say no, run it later from `/foreman Casey`.

---

## 3. Checking and fixing what you just hired

```
/foreman Casey
```

A bare `/foreman` gives you a picker. `/foreman GC` works too: GC is a crew
member with a special role.

You get two tables. The first is the Foreman: name, role, display name,
provider, working directory, instructions file, jobsite, extra readable
directories, vault folders, and provider options. The second is that Foreman's
Jobsite: repo path, description, repo URL, color, default branch, build
command, test command, backlog URL, and vault folders.

Some fields are marked **fixed** and are not editable here: name, role, working
directory, instructions file path, and the Jobsite's repo path. Everything else
is keyed off those, and changing them in place would break the roster.

Everything marked **editable** is a menu choice. Pick one, answer the prompt,
and the change is written straight to `foremen.yaml` or `jobsites.yaml` and
applied to the running session.

Below the field list there are two **actions**, and then "done":

- **re-render instructions**: rewrites
  `AI/ConstructionCrew/Instructions/Casey.md` (in the Vault) from the current
  template and the current Jobsite config, reusing the briefing sidecar. Run
  this after changing a build command, a test command or the default branch. It
  then offers to drop Casey's live conversation, because a running agent reads
  its instructions file only on its very first message. Say yes and the new
  instructions take effect on Casey's next turn, at the cost of that
  conversation's history. Say no and the file is updated for the next restart.
- **run sitewalk**: dispatches the sitewalk you declined at hire time. Same job
  id, same board, same reporting. Refused for GC, which has no jobsite to walk,
  and for a Foreman with no jobsite assigned.

**A useful thing to set now, if you have it:**

- `jobsite: backlog` → `https://github.com/your-org/lighthouse/issues` (a
  single URL). The sitewalk reads this as "the backlog". `/hire` also asks
  for it up front now, so this is only needed for a jobsite hired before
  that existed.

When you change a Jobsite field, two things behave differently:

- **The settings themselves** (`jobsites.yaml`/`foremen.yaml`, and the running
  session's own copy) update immediately, no restart needed. Anything the app's
  own logic reads live, such as which branch a new feature branch is cut from,
  sees the new value on its very next use.
- **Casey's own instructions file** does not, until you run *re-render
  instructions*. That file is plain-English prose telling Casey things like
  "your build command is `go build ./...`". Casey knows only what that file
  says; it never checks the live settings itself.

Switching `provider` here resets that crew member's tool policy to the new
CLI's defaults and re-applies its Home Office wiring, so it can still report
and escalate immediately. Anything you hand-tuned under "provider options" is
lost on a provider switch, the same as re-hiring under the new engine.

---

## 4. The sitewalk

If you said yes at the end of `/hire`, Casey is already on a sitewalk. It is an
ordinary dispatched job, so it shows on the board like any other work, and you
can keep typing while it runs.

A sitewalk is a **read-only survey** of the Jobsite. Casey's own instructions
tell it to:

1. Read the code, starting at the repo root: build files, layout, entry point,
   tests. Cite real files and line numbers. An empty directory with no git
   repository is a valid *finding*, not a failure.
2. Read the backlog, whatever the backlog link points at.
3. Read the docs: the repo's README plus anything already in its vault folders.
4. Write findings to `Notes/Lighthouse/Sitewalk.md` in your vault. It writes
   the file fresh if it does not exist yet, or appends a new dated,
   self-attributed section if a Foreman before Casey already walked this
   Jobsite (Lighthouse can have more than one). It never overwrites existing
   content.
5. Report to GC as a **milestone** sitrep. That is what puts it in front of GC
   instead of leaving a file in the vault.
6. Refresh the vault knowledge graph as the closing step. If that fails, Casey
   is told to say so once and stop, not to repair the graph tooling.

It changes no code, opens no branch, and opens no PR. Setting a repository up
is a workorder step, not a sitewalk step.

What you see: Casey shows as **working** in the roster while it runs. When
the milestone lands it arrives in your chat as a message in GC's own
conversation: one line naming Casey, the job id, the first line of the sitrep,
and the path to the full sitrep file. GC's reply goes straight back to Casey.

Read the note with `/view Notes/Lighthouse/Sitewalk.md`, which renders it
properly rather than dumping raw Markdown.

Keeping `Notes/Lighthouse/` current is a standing duty after this. When later
work invalidates something the sitewalk note claims, Casey is told to correct
the note.

---

## 5. Giving GC the vision

This is just typing. At the `Boss>` prompt, in plain English:

```
I want Lighthouse to be able to export sensor readings as a CSV file over HTTP.
Casey knows the repo. Can you get that going?
```

Your line goes to GC as a background job. The prompt comes straight back, and
GC's answer appears in the chat when it lands. A failed turn still gets a line,
so silence never means "quietly died".

**What GC is told to do with that:**

1. Ask you clarifying questions if the request is ambiguous, rather than guess.
2. Call `list_jobsites` and `list_foremen` to see who is on the roster right
   now. It is told never to assume, because you can hire mid-session.
3. Describe its plan in plain language, which Foreman does what, and **wait for
   you to react** before dispatching anything.
4. Once you say go, **write a workorder** into your vault at
   `Plans/Lighthouse/csv-export/WORKORDER.md`, where `csv-export` is a short
   branch-safe slug GC picks. GC can write files: its tool policy grants Write
   and Edit, and `Plans` is in its vault write scope. That file must open with
   YAML frontmatter naming the feature and the jobsite, and those values must
   match the folder names in the path exactly. The Home Office checks the file
   against its own location and rejects any disagreement.
5. **Dispatch it**, passing the absolute path of the file it just wrote. That
   claims Casey's one workorder slot and returns a job id immediately.
6. Tell you what it dispatched and the job id, so you can ask about it later.

Read the workorder yourself with
`/view Plans/Lighthouse/csv-export/WORKORDER.md` before you say go on the plan.
It is the requirements document the whole job is built from, and it is written
for someone who has never seen the codebase, so it should read clearly to you
too.

GC does not write a workorder on its own initiative for a small question. A
dispatch with no workorder path is an ordinary errand: no workorder, no slot
claimed. GC is told to use that only for questions and small tasks.

**The rules the Home Office enforces at dispatch time**, so you know what a
rejection means:

- The workorder file must live at
  `<vault>/Plans/<Jobsite>/<Feature>/WORKORDER.md`: exactly two folders under
  `Plans/`, no more, no less.
- Its frontmatter `feature` and `jobsite` must match those two folder names.
- The workorder's jobsite must be the target Foreman's own jobsite. You cannot
  hand Lighthouse work to a Foreman on another site.
- A Foreman holds **one** workorder at a time. A second one is rejected, naming
  the feature they are already on.
- The feature branch is always `feature/<Feature>`, cut from the workorder's
  `sourceBranch` if it names one, otherwise the Jobsite's default branch,
  otherwise `main`.

---

## 6. What the Foreman does with the workorder

Casey works the same standing workflow on every workorder. You do not have to
drive any of it. Knowing the shape tells you what "still going" looks like.

1. **Read** the workorder and the code it touches, and verify the workorder's
   claims against the actual code. A workorder can be wrong.
2. **Stand the repository up, if it has no commits yet.** Skipped on an
   existing project like Lighthouse, which already has commits. Section 8 is
   where this step does something. It comes this early because step 3 needs a
   commit to cut the feature branch from.
3. **Cut the feature branch**, `feature/csv-export`, from the workorder's
   source branch, before anything spawns a Worker. Every Worker's worktree
   branches from it, starting with step 5's.
4. **Plan**: write `PLAN.md` into the same Plans folder
   (`Plans/Lighthouse/csv-export/`). Exact files, exact symbols, exact success
   criteria.
5. **Adversarial review of the plan**: spawn a Worker on a *different* engine
   to hunt for defects in the plan. The reviewer never edits code.
6. **Fold the findings in**: each one recorded in `PLAN.md` as accepted or
   rejected, never silently dropped.
7. **Repeat 5-6**, up to three rounds, then escalate rather than loop.
8. **Human gate**: report the settled plan to GC and *stop*. No implementation
   code is written until you say go. GC is told to bring the plan to you and
   get an answer. Read it with
   `/view Plans/Lighthouse/csv-export/PLAN.md`.
9. **Implement** on the feature branch cut in step 3. Never on the default
   branch.
10. **Build and test**: both must pass. A failing test is never "unrelated"
    until proven so.
11. **Adversarial review of the diff**: a Worker on a different engine again,
    this time against the actual changes.
12. **One PR** for the feature, and file a `pr-opened` sitrep.
13. **Report** to GC: what shipped, what was reviewed, what was left out.

### Workers and worktrees

When Casey needs parallel hands, it spawns a Worker. A Worker is one-shot: no
memory, no view of Casey's conversation. Everything it needs has to be in the
task text. It can call back to Casey if it gets stuck, which re-invokes Casey
mid-job to answer.

Every Worker gets its **own git worktree and its own branch**, cut from
`feature/csv-export` and named like `feature/csv-export-worker-a1b2c3`. That
lets several Workers run at once without stepping on each other. The worktrees
live under ConstructionCrew's own `state/worktrees/` directory, not inside your
Lighthouse repo.

Three consequences:

- Casey must hold an active workorder before it can spawn a Worker. Without one
  there is no feature branch to cut from, and the attempt is rejected with that
  message.
- A Worker's commits are **not** on the feature branch until Casey merges them.
  Casey is told to merge first, then close the worktree. Closing first deletes
  the branch, and the commits go with it.
- The `feature/csv-export` branch always exists by the time the first Worker is
  spawned: step 3 cuts it before step 5's plan-review Worker runs.

### Escalating and reporting

Casey has exactly two ways to reach you, and both go through GC:

- **Escalate** (`ask_gc`) when it is blocked on a decision only you can make.
  GC's answer comes straight back. If nobody answers within five minutes the
  job is **parked** rather than failed: Casey ends its turn cleanly, and the
  job resumes by itself the moment GC answers, however much later that is. A
  parked Foreman shows as **parked** (magenta) in the roster and sits in its own
  column on the task board. It is not busy and it is not idle: it is waiting on
  you.
- **File a sitrep**, which writes a dated Markdown file into
  `Notes/Lighthouse/Sitreps/` in your vault, append-only, one file per day per
  altitude (`summary` is the short version you read, `detail` the long one).
  Three kinds:
  - `status`: records it. **Notifies nobody.** A file appearing in your vault
    does not reach you.
  - `milestone`: also escalates a one-line summary into GC's conversation, so
    it lands in your chat.
  - `pr-opened`: releases Casey's workorder slot so it can take new work, and
    fires your PR notification.

### Opening the PR

Once every phase is done and build and test both pass, Casey pushes only its
own feature branch, opens exactly one PR against the default branch, adds
GitHub Copilot as a reviewer in a separate follow-up command, and files the
`pr-opened` sitrep. If the Copilot-reviewer step fails, Casey is told to quote
the error and continue. The PR already exists; that is what matters.

Filing that one sitrep is the bookkeeping. There is no separate "release" or
"notify" step; the Home Office does both as side effects.

### Getting a desktop notification

For a real notification when a PR opens or a job parks, add a command to
`appsettings.json`. The example below is for Linux. Substitute whatever raises
a notification on your OS, such as `terminal-notifier` on macOS or `msg` or a
toast helper on Windows:

```json
{
  "Notifications": { "Command": "notify-send 'ConstructionCrew' '{event} {foreman} {jobId}'" }
}
```

`{event}`, `{foreman}` and `{jobId}` are substituted. `{event}` is `pr-opened`
or `parked`. There is no UI for this yet: it is file-only, and with nothing
configured nothing is spawned.

---

## 7. Closing a feature out

When the PR is merged and you have signed off, GC is told to close the feature
out in this order. The order matters because the Plans folder is deleted last.

1. Write (or check) the delivery note at
   `Notes/Lighthouse/Deliveries/csv-export.md`. It records what the job cost:
   which Foreman and which Workers ran, the engine each used, tokens and
   dollars, estimated versus actual hours, and the commit and PR numbers. The
   actual numbers come from `Plans/Lighthouse/csv-export/RUN-LOG.md`, which the
   Home Office maintains itself: one line per completed unit of work, with
   started and finished stamps, actual hours net of parked time, queue time,
   tokens and cost. A number the run log does not have is written as
   "unavailable", never as zero.
2. Link it from the "recently completed" entry in `Notes/Lighthouse/Status.md`.
3. Only then delete `Plans/Lighthouse/csv-export/`. Git history is the record
   of the work; the delivery note is the record of what it cost.

The delivery note lives under `Notes/`, not `Plans/`, so it survives step 3 and
stays in the graph.

---

## 8. Walkthrough two: starting a brand-new project from nothing

This path works end to end.

The scenario:

> I have an idea. I want a small Python command-line tool called **Tidepool**
> that renames photo files from their EXIF capture date. There is no code
> anywhere: no folder at `~/code/tidepool`, no git repo, and nothing about it in
> my vault. I want to hire a Foreman called **Robin** and have it build the
> thing.

### 8.1 Hire against a path that does not exist

```
/hire
```

- **Name this Foreman** → `Robin`
- **Workspace** → *+ add a new jobsite*
- **Jobsite name** → `Tidepool`
- **Repo path** → `~/code/tidepool`

  The prompt says what it accepts: *an existing local clone, or a new folder to
  create it as an empty project*. That path does not exist, so you get:

  ```
  '~/code/tidepool' doesn't exist. Create it as a new, empty project folder? [y/n] (y)
  ```

  Say yes. The folder is created. If creation fails, from a permissions problem
  or a bad path, you are told why and asked again rather than dumped out. At any
  of these free-text prompts you can type `cancel` to abandon hiring.

- **Repo URL** → leave blank for now. You have not created a GitHub repo yet.
  Section 8.5 says what that costs you.
- **Default branch** → blank, so `main`.

  Immediately after the branch is settled, the app runs:

  ```
  Initialized an empty git repository at ~/code/tidepool (branch main).
  ```

  It checks first (`git rev-parse --git-dir`) and initializes only if there is
  no repository, so this is safe on an existing clone and safe if you already
  ran `git init` yourself. It is a *bare* init: a `.git` directory and nothing
  else. No commit, no files.

- **Build command** → blank. You do not know yet, and the Foreman's
  instructions say "ask the Boss before guessing one".
- **Test command** → blank, same.
- **Description** → `A small Python command-line tool that renames photo files
  from their EXIF capture date.` Write this properly. It is the only thing the
  Foreman knows about the project at this point, and the README gets written
  from it.
- **Color** → whatever you like.
- **Vault folders** → the default `Notes/Tidepool, Plans/Tidepool` is shown;
  say yes. Neither folder exists yet. They are created as part of the hire and
  named back to you.

- **Engine** → pick one. With two installed, pick the one you want doing the
  *writing*; the other does the reviewing.
- **Briefing** →

  ```
  You are Robin, the Foreman for Tidepool. Tidepool is a brand-new Python
  command-line tool with no code in it yet. Start small and add tests from the
  first commit.
  ```

- Confirm, then **Spawn this Foreman?** → yes.

### 8.2 Let the sitewalk run anyway

Say yes to **Run Robin's sitewalk now?** even though there is nothing to
survey. Robin's instructions treat "an empty directory with no git repository"
as a valid current-state finding, so `Notes/Tidepool/Sitewalk.md` comes back
saying exactly that, the milestone lands in your chat, and the vault gets a real
starting-state record for the project. Read it with
`/view Notes/Tidepool/Sitewalk.md`.

### 8.3 Give GC the vision

Same as section 5, at the `Boss>` prompt:

```
Tidepool should be a Python CLI: point it at a folder of photos and it renames
each file to its EXIF capture date, like 2026-08-31-143210.jpg. Robin is on it.
Skip a file with no EXIF date rather than guessing. Let's start with just that.
```

GC asks whatever it needs to, describes its plan, waits for you, then writes
`Plans/Tidepool/exif-rename/WORKORDER.md` and dispatches it to Robin with the
absolute path. A brand-new project changes none of that: GC writes the
workorder itself, and the Plans folder already exists from the hire.

Read it before you say go: `/view Plans/Tidepool/exif-rename/WORKORDER.md`.

### 8.4 The first dispatch stands the repository up

Robin runs the same workflow as section 6, but step 2 fires this time. It
happens second, right after Robin reads the workorder, before step 3 cuts the
feature branch and well before the plan-review step spawns a Worker. Step 3
needs a real commit to cut the feature branch from, and a repo with zero
commits has none.

Before its first git command, Robin checks `git rev-parse HEAD`. On Tidepool
that **fails**, because hire time ran only a bare `git init`: there is a `.git`
directory but not a single commit. That failure is the signal. It is the check
used rather than "does `.git` exist" because hiring always initializes, so
`.git` existing proves nothing.

Robin then does this, in order, and is told not to improvise past it:

1. `git init -b main`, if it somehow is not a repository yet. The hire normally
   did this already, and skipping it is harmless.
2. **Ask you about the license, through GC, and wait.** This arrives in your
   chat as an ordinary message in GC's conversation, something like *"Tidepool
   has no git repository yet, so I am initializing one. What license should it
   carry? An SPDX id (e.g. Apache-2.0, MIT), or 'proprietary' for no license
   file at all."* Answer it, `Apache-2.0` for our scenario, and your answer
   goes straight back to Robin. Robin is told not to pick a license itself and
   not to default to one. If you do not answer, the job parks and waits for you.
3. **Write the starter files, and nothing else:**
   - `README.md`: the project name, one paragraph taken from the Jobsite
     description you wrote at hire time, and a Build and a Test section
     carrying the configured commands. You left those blank, so expect Robin to
     ask you about them too, the same way it asked about the license.
   - `LICENSE`: the full text of what you named. Answer `proprietary` or `none`
     and no LICENSE file is written; the README says so instead.
   - `.gitignore`: for the stack the workorder calls for. Python here.
     If the workorder does not settle the stack, Robin asks rather than
     guessing.
4. **Check there is a git identity to commit as, before committing.** A
   brand-new repo has none, and this machine may have no global one configured
   either. Robin checks `git config user.email` first. If it is blank, Robin
   sets a repo-local identity (`ConstructionCrew Foreman` /
   `foreman@constructioncrew.local`) rather than guessing at yours or getting
   stuck. The commit is the crew's, not the Boss's, and the identity says so.
5. `git add -A` and one commit on `main`, message `Initial commit`.
6. If the Jobsite has a repo URL configured, add it as `origin` and push
   `main`.

Nothing else gets scaffolded. No source layout, no CI, no build files beyond
what the workorder asks for.

### 8.5 The one thing to do before the PR step

You left the repo URL blank, so there is no remote. Robin is told to report
that: the PR at step 12 cannot be opened, and **you** have to create the
remote. Robin will not create one for you.

When that report lands:

1. Create the empty repo on GitHub, or wherever, yourself.
2. `/foreman Robin` → `jobsite: repo url` → paste the URL.
3. Set `jobsite: build command` and `jobsite: test command` while you are in
   there, now that Robin has told you what they are.
4. Pick **re-render instructions**, and say yes to dropping Robin's live
   conversation so the new build and test commands reach it.
5. Tell GC to have Robin push `main` and carry on.

Create the GitHub repo before you hire and paste its URL at the **Repo URL**
prompt in 8.1 to skip all of that. That is the smoother order if you know you
want a remote.

### 8.6 From here it is an ordinary project

Once there is a first commit, Tidepool behaves like Lighthouse. Step 2 never
fires again (`git rev-parse HEAD` succeeds from now on), and every later
workorder runs the plain section 6 workflow: read, plan, adversarial review,
your go-ahead, implement on `feature/<slug>`, build, test, review the diff, one
PR, one `pr-opened` sitrep.

---

## 9. Watching, steering, and letting people go

### `/tasks`: the board

Four columns, live from real job state:

- **doing**: pending or running
- **parked**: waiting on you
- **done**: completed
- **failed**

Each card shows the Foreman name and the first 40 characters of the task. It is
a glance, not a report.

### `/monitor`: who is working right now

A live table, one row per crew member plus one row per Worker with a job still
in flight. Columns: who (and their jobsite), kind (GC / Foreman / Worker),
state (working / parked / idle), the task, when it started, and elapsed time.

Two things about it:

- **Elapsed is actual worked time**, not wall clock since dispatch. Time a job
  spent parked waiting on you is subtracted.
- **Worker rows are transient.** A Worker appears the moment it is spawned and
  disappears the moment its job finishes. The view shows live parallel work,
  not history. Use `/tasks` for the history.

A Foreman whose only in-flight work is a Worker's shows as **working**, because
it is not free, but its own task column stays blank: that Worker has a row of
its own further down.

### `/memory`: browsing the vault the crew works in

A modal browser over the vault. The roots it offers are the **union of every
hired crew member's own vault folders**. For our two projects that is
`Notes/GC`, `Notes`, `Plans`, `Notes/Lighthouse`, `Plans/Lighthouse`,
`Notes/Tidepool`, `Plans/Tidepool`. Not the whole vault: your unrelated notes
are out of scope by construction, not by filtering.

Pick a root, then walk down through folders and files. `..` is offered but
stops at the top of a crew folder: try to go higher and it tells you that is
the top of what the crew can see. Picking a file renders it the way `/view`
does. `(back)` returns to the root list, `(done)` leaves.

A folder that no crew member has been given, or that does not exist yet, is not
offered.

### `/view <path>`: reading a crew-written file properly

```
/view Notes/Lighthouse/Sitewalk.md
/view Plans/Tidepool/exif-rename/WORKORDER.md
```

Renders the file as real console output: headings as rules, lists as bullets,
tables as tables, code blocks in boxes, YAML frontmatter in a dim panel,
`[[wikilinks]]` in blue. It pages with *-- more (enter) --* when the file is
longer than the window. Full width and modal, so a table is actually readable.

A relative path is looked for under the vault first, then under the
ConstructionCrew repo. An absolute path is taken as given. It reads only `.md`,
`.txt`, `.yaml` and `.yml`, and only under those two roots. Anything else is
refused, including a path that tries to climb out with `..`.

Reach for this whenever GC or a Foreman tells you it wrote something.

### `/drive Casey`: talking to a Foreman directly

Switches your prompt so everything you type goes to Casey instead of GC. The
prompt changes to `Boss[Casey]>` and the chat pane shows Casey's own
conversation. Beside it is a small read-only panel showing the branch, whether
the working tree is clean, and recent commits from the most recent worktree
Casey or one of its Workers is in.

If Casey is mid-turn when you start driving, you are told so: *"queued behind
…, started 14:32."* Your message queues behind that turn rather than
interrupting it. A turn that has not started yet says so instead of quoting a
start time it does not have.

`/exit` while driving leaves Casey and returns you to GC. `/exit` when you are
not driving quits the app. `/drive GC` returns you to the main chat, since that
is who you already talk to.

Driving is a message relay, not a terminal. You are not attached to a live
process.

### `/fire`: letting someone go

Pick from the list; GC is never offered. If that Foreman has a job running you
are warned and asked to confirm again. A final confirmation panel names the
Jobsite and says plainly that it is kept, not removed.

Firing removes only the Foreman from `foremen.yaml`, deletes its own generated
instructions file, forgets its conversation, and prunes any worktree
bookkeeping ConstructionCrew itself created. If you were driving them, you are
returned to GC.

**The Jobsite is never removed, even if that was its only Foreman.** It stays
in `jobsites.yaml`: hire a new Foreman onto it later and it is right there,
still offered in the picker. Firing never touches the Jobsite's repo on disk:
your code, its branches, and its working tree are untouched. Nothing in your
vault is deleted either. Sitreps, sitewalk notes, plans and delivery notes all
stay, whether that Jobsite ends up with zero Foremen or several.

---

## 10. Where everything lives

Inside the ConstructionCrew repo:

| Path                                     | What it is                                                     |
| ----------------------------------------- | -------------------------------------------------------------- |
| `config/foremen.yaml`                    | Your roster: GC plus every Foreman                             |
| `config/jobsites.yaml`                   | Your Jobsites                                                  |
| `config/scaffold/`                       | The starter vault, incl. `AI/ConstructionCrew/crew-preferences.md` and both instructions templates |
| `config/generated/`                      | Home Office wiring written at startup                          |
| `appsettings.json`                       | Vault root, port, notification command (git-ignored; first-run setup writes it) |
| `appsettings.json.example`                | Reference only, showing the shape; not a file to copy          |
| `state/jobs.jsonl`                       | Every job transition, append-only                              |
| `state/tools.json`                       | Cached CLI discovery (`/settings` refreshes it)                |
| `state/worktrees/`                       | Worker worktrees, per jobsite                                  |

Inside your vault:

| Path                                             | What it is                                    |
| ------------------------------------------------ | ---------------------------------------------- |
| `Plans/<Jobsite>/<Feature>/WORKORDER.md`         | The workorder GC wrote                        |
| `Plans/<Jobsite>/<Feature>/PLAN.md`              | The Foreman's implementation plan             |
| `Plans/<Jobsite>/<Feature>/RUN-LOG.md`           | One line per completed unit of work           |
| `Notes/<Jobsite>/Sitewalk.md`                    | The sitewalk findings                         |
| `Notes/<Jobsite>/Sitreps/YYYY-MM-DD-*.md`        | Sitreps, append-only                          |
| `Notes/<Jobsite>/Deliveries/<Feature>.md`        | What the finished job cost                    |
| `Notes/<Jobsite>/Status.md`                      | Current state, linking the delivery notes     |
| `Notes/GC/Sitreps/`                              | GC's own sitreps                              |
| `AI/ConstructionCrew/crew-preferences.md`        | How you like work done; ensured at every start |
| `AI/graph/build/schema.ttl`, `data.ttl`          | The graph projection `build_graph` writes     |
| `AI/ConstructionCrew/Templates/*.md`             | The GC/Foreman instructions templates. Boss-editable, seeded once, never overwritten |
| `AI/ConstructionCrew/Instructions/GC.md`         | GC's instructions: generated, not shipped, self-heals at start |
| `AI/ConstructionCrew/Instructions/<Name>.md`     | Each Foreman's instructions, written at hire  |
| `AI/ConstructionCrew/Instructions/<Name>.briefing.md` | Your briefing, kept verbatim for re-rendering |

---

## 11. Command reference

| Command                            | What it does                                                                                                                                                      |
| ----------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| *(anything not starting with `/`)* | Sent to GC, or to the Foreman you are driving, as a message. Runs in the background; the reply appears in the chat when it lands.                                  |
| `/chat`                            | Switch back to the chat view.                                                                                                                                     |
| `/tasks`                           | The doing / parked / done / failed board.                                                                                                                          |
| `/monitor`                         | Live table of who is working: one row per crew member, plus a row per in-flight Worker. Elapsed is net of parked time.                                             |
| `/memory`                          | Modal vault browser, scoped to the union of every hired crew member's vault folders. Ends in a rendered file view.                                                 |
| `/view <path>`                     | Render a `.md`/`.txt`/`.yaml`/`.yml` file under the vault or the repo as real console output, paged. Bare `/view` prints usage.                                    |
| `/hire`                            | Hire a Foreman: name, Jobsite (incl. branch, build and test commands, and the vault-folder default to accept or override), engine, briefing, then optional sitewalk. Refuses if no vault is configured. |
| `/fire`                            | Let a Foreman go. Removes only that Foreman from this tool's config; the Jobsite is kept, and the repo and vault are never touched.                                |
| `/foreman <Name>`                  | View a crew member's details and edit the safe fields, plus two actions: **re-render instructions** and **run sitewalk**. A bare `/foreman` gives a picker.         |
| `/preferences [add]`               | Show `AI/ConstructionCrew/crew-preferences.md`, the tiebreaker every crew member reads. `/preferences add` appends one without hand-editing the file.              |
| `/inbox`                           | Pick through messages Foremen sent while you were doing something else. Reading one never disturbs what is on screen in the chat. The footer badges unread ones.   |
| `/drive <Foreman>`                 | Route your typing to that Foreman instead of GC, with a read-only git panel beside the chat.                                                                       |
| `/settings`                        | Offer vault setup if none is configured, then re-scan for installed CLIs, re-stamp Home Office wiring, and show what is wired.                                     |
| `/migrate`                         | Move any crew member's instructions file, and briefing sidecar, still in the old repo-side location into the Vault, and seed missing templates. Runs automatically at every start; this is an on-demand re-trigger. |
| `/help`                            | One line listing the commands.                                                                                                                                    |
| `/exit`                            | Leave the Foreman you are driving; if you are not driving, quit. `exit` and `quit` work the same.                                                                  |

Every tab in the strip is live. An unrecognized `/command` tells you it is not
a command and points you at `/help`.

---

## 12. The short version

1. Start the app. Point it at your vault.
2. `/hire` a Foreman. Give it the real repo path: an existing clone, or a new
   folder you want created. Fill in the branch, build and test commands while
   you are there, and check the vault-folder default before accepting it.
3. Let the sitewalk run, and `/view Notes/<Jobsite>/Sitewalk.md` when it lands.
4. Tell GC what you want, in plain English.
5. Say go when GC brings you a plan. `/view` the workorder before you do.
6. On a brand-new project, answer the license question when it arrives, and
   create the GitHub remote if you did not give one at hire time.
7. Say go again when the Foreman brings you a settled implementation plan.
8. Watch `/monitor` while work is live and `/tasks` for the whole board. Answer
   anything that goes **parked**: that is you being waited on.
9. A `pr-opened` notification means there is a PR to review.
10. After the merge, let GC write the delivery note and clean the Plans folder
    out.
