# <PROJECT NUMBER> — <machine> — <source PLC> → Siemens TIA Portal

<!-- PROJECT BLOCK: fill this in for the new job. Everything from "Working rules" down applies as-is. -->

You are working on the controls migration of a **<machine type>** (<OEM>, model <x>, customer <y>).
Original controller: **<e.g. Allen-Bradley SLC 5/05, RSLogix 500>**. Target: **<e.g. S7-1516-3, TIA Portal Vxx>**,
HMI **<panel / PC, WinCC Basic/Comfort/RT Advanced/Unified>**. The user is the controls engineer doing
the port. Behaviour must be preserved: this is a platform port, not a re-spec.

## Read first
1. `docs/SOURCE_REGISTER.md`: every source, its classification, and what has been verified.
2. `docs/OPEN_ISSUES.md`: conflicts between sources and unresolved questions.
3. `docs/LESSONS_LEARNED.md`: technical gotchas from earlier migrations. Check it before designing anything.
4. `docs/hardware/`: device-type hardware-config reference (G120, IPC HMI, Festo CTEU-PN) with per-project checklists.
5. <machine overview / pain-points docs, once written>

## Where things are
| What | Path |
|---|---|
| This workspace (specs, original PLC backup, deliverables) | `<path>` |
| Shared project folder (OEM docs, archives, schematics) | `<path>` |
| Live TIA project (**do not open or edit**, see below) | `<path>\<project>.apNN` |
| Test copies of the TIA project | `<path>\<TestCopies>\` |
| Latest TIA export (blocks XML, HMI export) | `<path>` |
| TIA Portal Tool (Openness export/import, verifier, lint) | `<path to TIA_Portal_Tool>` (portable exe in its root) |

## Source-of-truth rule (applies to you and every agent)
**Before reviewing any document, ask the user whether it is a Source of Truth or WIP/ad hoc**, then record
the answer in `docs/SOURCE_REGISTER.md`. Do not open, summarise or draw facts from an unclassified document.
Subagents cannot ask the user: if a needed document is not in the register, they stop and report back.

Typical classification (confirm per job):
- **Behaviour:** the original PLC program export (ladder text export plus symbol/comment file).
- **Intent:** the functional and design specs (FRS/DDS or equivalent).
- **Interface:** the DCS/SCADA register map, at the version the user names. Old versions are not truth.
- **I/O:** the electrical schematics, not an I/O list typed from them.
- **HMI layout/text:** the original HMI (screens, language exports) if the job is to mirror it.
- **The TIA project under development:** WIP, checked against the behaviour source.

When sources disagree, **don't pick a winner silently**. Log it in `OPEN_ISSUES.md` and tell the user.
A user correction of a source ("V21 is the truth, not V20") goes into the register immediately.

## Working rules (learned the hard way, keep them)

### Hands off the live project
- **Never open (GUI or Openness) or change the live TIA project.** Work on a folder copy, made while TIA has
  the project closed, and say the exact path of the copy any finding comes from. Copies keep the same
  `.apNN` file name, so name them by folder ("the copy in TestCopies\Copy3").
- Deliver changes as files for the user to apply: block XML for the tool's Import Folder, SCL external
  sources, xlsx for TIA's own importers, watch-table text. A one-time exception the user grants ("apply it
  tonight") covers that occasion only. Back up the project folder first and test on a copy first.
- Never edit the original PLC export in place.

### Structure and style
- **No new blocks without asking.** Put ported or new logic as networks in the existing blocks that mirror
  the original program files (input moves in INPUT, scaling in SCALING…). If a new FC/FB/DB seems
  necessary, ask first. The user will say how they want it.
- **Keep code simple.** Use plain conversions, one line per signal, e.g. `reg[0] := REAL_TO_INT(x);`. Add clamps or
  guards only where a value can realistically go out of range or NaN (wire break, divide by a zero
  setpoint), and say why in one line.
- Preserve the original scan/call order and the command-word layering (mode commands → output transfer →
  OUTPUT). Hardware addresses live only in the INPUT/OUTPUT blocks.
- Keep the original symbol names where the HMI, docs or DCS reference them. Comment every network with its
  origin (`// LADn Rm`).
- **Match the original's units and behaviour exactly.** Timer bases, minutes vs seconds, counter resets and
  compare directions all count. Allowed differences are the ones the user approves: hardware,
  non-portable constructs, the setpoint object pattern.

### Deliverables (the deliverables folder is the only place work lands)
- **Everything you produce goes in `deliverables\`.** Scratch work goes in the session scratchpad. Never write
  into the TIA project, its export folders, the original program backup or the TIA Portal Tool repo.
- **One folder per piece of work: `deliverables\YYYY-MM-DD_TOPIC\`** (date = when it was started, TOPIC in
  caps, e.g. `2026-01-15_ALARM_ACK`). Typical layout:

  | Path | What |
  |---|---|
  | `README.md` | What, why, how to apply (step by step), what's still manual, rollback, and the status block |
  | `build.py` | Regenerates everything below from the base. Rerun it rather than hand-patching outputs |
  | `base_blocks_xml\`, `base_hmi\` | The export the build started from (name its date/source in the README) |
  | `work_*\` | Edited copies the build produces |
  | `orig\` | Unedited sources, when a file is modified, for diff and rollback |
  | `import\`, `import_hmi\` | Exactly what the user runs (Import Folder / Import HMI), nothing else in them |
  | `data\*.csv`, `changelog.tsv` | Evidence and the per-edit change list |
  | `logs\` | Test-import and compile logs from the copy |
- **README status block** at the top, one dated line per event: built (from which base), tested on which
  copy (PLC/HMI errors and warnings), applied by the user (or not), superseded by what.
- **Revisions don't overwrite:** a changed approach becomes `build_v2.py` / `import_v2\` beside the original,
  with the README saying which one is current. Follow-up work after a package was applied gets a **new dated
  folder** that links back to the earlier one.
- **Exception:** a living plan or tracking document the user asks to keep current is updated in place (with a
  status table at its top), not copied into new dated folders.
- **Memory and hand-offs point at the deliverable path** instead of repeating its contents. Before re-deriving
  anything, check `deliverables\` for earlier work on the same topic.
- State the status of every deliverable plainly: built / tested on a copy / compiled / applied by the user.
- **Test every import package on a project copy before handing it over** (headless `import-folder
  --no-save`, or a real import on a copy). Read the whole generated SCL before handing it over.
- **After any test import, rebuild the package** (or restore `hmi_manifest.json`) before giving it to the
  user. The tool writes the test target's hashes into the package, and the next run reports
  "0 imported, N unchanged": a silent skip that still says SUCCESS. Treat that line as a failure.
- The user prefers tool-driven updates (Import Folder / external sources / xlsx) over hand-editing ladder.

### Watch tables
- A watch table deliverable is **only what goes in the TIA watch table Name column**: one tag per line,
  CRLF, `//` lines as section headers. No address, data type, format, comment column or tabs.
- Sequence watch tables are organised by step: `// ==== STEP n - <description> ====`, followed by every tag
  that step uses (tags repeat across steps).

### Simulation
- Force **raw field inputs** only. Never force outputs or status bits that an input-transition block derives
  every scan: the forced value fights the recompute and breaks the sequence. Make raw feedback react to the
  real command output (`IF OP_x THEN fbk := TRUE ELSE fbk := FALSE`, always with the ELSE).
- A stateful sim FB (ramp, plant model) must be called once per scan. Gate by mode instead of adding a
  second call. Combine boolean forces with OR instead of a second assignment.

### Reading exports
- **Check export timestamps first.** The exporter never deletes old files, so a renamed or deleted block's
  XML stays behind. Ignore files much older than the newest one in the folder.
- Reimport/Override imports the *exported* XML, so any edit made in TIA after the export is reverted.
  Export immediately before building a package.

### Communication
- Concise status summaries; confirm direction in small steps before big builds.
- Cite sources in anything written: `[FRS §x]`, `[DDS §x]`, `[LADn Rm]`. Mark a fact *verified* only if
  you checked the source of truth in this session.
- Before ending a session, leave notes in memory (what was done, applied or not, what's next) so the next
  day starts from them.

## Tools
- PDFs: `pdftotext -layout` (MSYS `/mingw64/bin/pdftotext`). State diagrams and interlock matrices extract
  badly, so read those pages as images.
- Python 3 (`python`, with pymupdf and openpyxl) and PowerShell/.NET for parsing. There is no Excel: xlsx opens in
  OpenOffice Calc, so generated workbooks must look right there.
- **Use the TIA Portal Tool's `public` branch** (the release) for docs, the kit and the tool itself. Other
  branches in the tool folder are tool development work, not for project sessions.
- **TIA Portal Tool** headless commands always run through the Openness-approved runner:
  `<tool>\tools\runner\OpennessRunner.exe <tool>\TiaPortalTool.exe <command> ...`. Running
  `TiaPortalTool.exe` with arguments directly only opens the GUI. Useful commands (depending on the
  tool version): `export-blocks`, `hmi-export`, `hmi-import`, `import-folder <project> <folder> [--no-save]`,
  `export-projecttexts`, `verify-folder`, `verify-export` (rung check of a hand port against the original),
  `lint-export` (Temp edge memory, literal timer presets, double coils…). A new tool build needs the
  Openness firewall approval again (the prompt comes about 2.5 min after TIA starts).
- A headless TIA instance can run on a copy while the user has the live project open in their own TIA.
  Watch the RAM: two instances plus a large project can run out of memory.

## Agents (`.claude/agents/`)
| Agent | Use for |
|---|---|
| `machine-process-expert` | Read-only answers about what the machine does and why, with citations. |
| `plc-to-tia-converter` | Translating specific ladder files/rungs into TIA LAD/SCL with the porting patterns. |
| `tia-portal-dev` | Building TIA PLC content: DBs, UDTs, FCs/FBs, OBs, PID, HSC, comms, import packages. |
| `wincc-hmi-dev` | HMI screens, tags, alarms, text lists and translations, delivered as import packages. |
| `sim-test-engineer` | Sim forcing layer, watch tables, FAT test cases. |
| `migration-reviewer` | Read-only QA: compares TIA blocks with the original program and the specs. |
