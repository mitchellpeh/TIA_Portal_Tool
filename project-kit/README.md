# Project kit: Claude Code setup for a TIA Portal migration project

This folder is a starting setup for a Claude Code workspace on a new TIA Portal project, for example an
SLC/PLC-5/Logix to S7-1500 migration with a WinCC HMI. It holds the working rules, agents and lessons
from an earlier migration that used this tool. A new session reads them from the first prompt, so it
doesn't have to learn them again.

Everything here is generic. It holds no project names, tags, paths or results. Keep it that way: job
facts go in the new project's own files, never back into this folder.

## What's in it

| Path | What it is | Loaded |
|---|---|---|
| `CLAUDE.md` | Project instructions template: fill in the project block; the working rules below it apply as-is | Automatically, every session |
| `.claude/agents/*.md` | Six agents: process expert, converter, TIA developer, HMI developer, sim/test engineer, migration reviewer | Automatically (listed as agent types) |
| `docs/LESSONS_LEARNED.md` | Technical gotchas: porting pitfalls, TIA/WinCC import rules, Openness, drives, alarms | On demand (CLAUDE.md points to it) |
| `docs/hardware/` | Device-type hardware-config reference (G120, IPC HMI, Festo CTEU-PN): setup, PLC interface, gotchas, per-project checklist | On demand |
| `docs/templates/SOURCE_REGISTER.md` | Source-of-truth register, one row per document | Copy to `docs/` and fill in |
| `docs/templates/OPEN_ISSUES.md` | Log of conflicts between sources and unresolved questions | Copy to `docs/` and fill in |

## How to use it

1. Copy the **contents** of this folder into the root of the new project workspace, so that `CLAUDE.md` and
   `.claude\` sit at the workspace root. Then copy `docs\templates\*.md` into `docs\`.
   ```powershell
   Copy-Item -Recurse -Force "<TIA_Portal_Tool>\project-kit\*" "<new workspace>\"
   Copy-Item "<new workspace>\docs\templates\*.md" "<new workspace>\docs\"
   ```
   (`Copy-Item ...\*` includes the hidden-looking `.claude` folder; check it arrived.)
2. Fill in the **Project** block at the top of `CLAUDE.md`: machine, controllers, TIA version, sources and paths.
3. Rewrite each agent's "Project facts" section. The agents work without it, but they're much better with it.
4. Open the workspace in VS Code and start Claude Code there. The first thing the session should do is
   classify the sources with you (see the source-of-truth rule in `CLAUDE.md`).

## What does not carry over

- **Claude's auto memory** is stored per workspace path (`~\.claude\projects\<path>\memory\`). It is
  not copied. The new project starts with an empty memory, and the session builds it as you go.
- **User-level preferences** that apply to every project can also go in `~\.claude\CLAUDE.md`. The rules
  in this kit's `CLAUDE.md` are deliberately project-level, so you can edit them per job.

## Keeping it up to date

When a project teaches a lesson that would help the next job, add it here in generic form (no job names
or tags) on a branch, as with any other change to this repo. Lessons go in `docs/LESSONS_LEARNED.md`,
rules in `CLAUDE.md` and agent behaviour in the agent file.
