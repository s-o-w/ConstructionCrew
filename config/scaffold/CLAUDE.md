# CLAUDE.md

Instructions for any agent working in this vault. A CLI auto-loads this file
when the vault root is its working directory, so everything here is standing
context for every session.

## What this vault is

A personal knowledge vault: a living wiki, not a journal. It holds project
documentation, an evergreen knowledge layer, and a week-by-week work log.

## Navigation

- **Entry point:** `HOME.md`
- **Projects:** `Notes/<Project>/` holds the project node plus its
  `Vision.md` / `Architecture.md` / `Roadmap-and-Opportunities.md` / `Status.md`
  quartet.
- **Permanent notes:** flat in `Notes/`. One concept per file, named for the
  concept, not the project.
- **Agent-executable plans:** `Plans/<Project>/<Feature>/`. Not in the graph.
- **Weekly logs:** `Journal/YYYY-Www.md`. Not in the graph.
- **Graph layer:** `AI/graph/` (ontology, vocabularies, generated
  `build/schema.ttl` and `build/data.ttl`).
- **Vault meta:** `VAULT-META/` (templates and conventions).

## The vault is canon

All persistent state lives inside this directory. Anything that must survive a
session -- notes, planning docs, standing context -- belongs here, not in a
tool's own scratch directory outside it.

## Frontmatter

Every new non-config markdown file gets YAML frontmatter.

**Required:** `type`, as a wiki link naming a class under
`AI/graph/Ontologies/VaultMeta/Classes/` -- for example
`type: "[[PermanentNote]]"`, not the bare string `type: permanent-note`. Every
graph-participating note also needs `id: urn:uuid:<guid>`, minted once. Schema
notes under `AI/graph/Ontologies/` and `AI/graph/Vocabularies/` mint from file
name and carry no `id`.

**Recommended:** `description` (one line), `tags` (list), `audience` (wiki link
into `AI/graph/Vocabularies/InformationClassification/`), `maturity` (wiki link
into `AI/graph/Vocabularies/NoteMaturity/`), `touchesProject` (wiki link or list
into the relevant project node(s)), `authoredBy` (plain string naming who wrote
the note).

**Do NOT add frontmatter to:** `Journal/`, `CLAUDE.md`, or `VAULT-META/` files.

## Linking

- Use Obsidian `[[wikilinks]]`. Do not convert them to standard markdown links.
- Back-link at the top of a new file: `← [[PARENT-INDEX]]`.
- Naming: `CamelCase.md` or `DASH-SEPARATED.md`. Weekly logs `YYYY-Www.md`.

## Retiring a note

There is no archive folder. `maturity: "[[Superseded]]"` retires a note in
place: it stays where it is, keeps its `id`, and stays queryable in the graph,
but is not current truth and must not be cited or built on.

## Status tags

`✓` Complete  `🔄` Active  `⏸` Paused  `📋` Planned  `❌` Abandoned
