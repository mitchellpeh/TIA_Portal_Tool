# Test scripts

How the SLC converter and the GUI are tested. Run them after every change; output goes to `tools/tests/out/`
(gitignored).

| Script | What it checks | Time |
|---|---|---|
| `convert-only.sh <export.SLC> [outDir] [--1200]` | Converts with the console converter (no TIA Portal); prints the summary and the items that need attention | seconds |
| `tia-test.sh <export.SLC> <projectName> [--release] [--1200] [--no-verify]` | Builds the app, creates a fresh TIA project, imports, compiles and verifies every converted rung against the SLC source | 5–20 min |
| `gui-check.ps1 [-Exe] [-OutDir]` | Opens every window, uses each Main menu button, screenshots each window and checks it fits the screen | ~15 s |
| `gui-convert.ps1 -Slc <export.SLC> [-OutDir] [-Wait]` | Runs Convert Only through the GUI, screenshots the result and checks the report opens (closing only the Calc/Excel it started) | ~20 s |
| `output-log-check.ps1 [-Exe] [-OutDir]` | Feeds the Output pane the lines of a run (firewall not approved, TIA starting, warnings, errors) and screenshots it pulsing and after; checks the colours and the waiting-for-you pulse without TIA Portal | ~5 s |
| `report-preview.ps1 -Xlsx <report.xlsx>` | Renders a report's cover sheet as Apache OpenOffice Calc shows it (PDF, plus PNG if Python has pymupdf) | ~15 s |

The `.sh` scripts run in Git Bash; the `.ps1` scripts with `powershell -ExecutionPolicy Bypass -File tools\tests\<script>`.
The GUI scripts use the Debug build (`dotnet build` in `TiaPortalTool`) unless `-Exe` is given.

## TIA Portal runs

TIA Portal's Openness firewall approves a program by its file hash, so every new build of TiaPortalTool.exe would
need approving again. `tia-test.sh` therefore runs through a stable host, `tools/runner/OpennessRunner.exe`, which
loads whichever app build it is given and runs its headless commands (`TiaPortalTool/Testing/HeadlessCommands.cs`).
Build it once and approve it in the firewall prompt (about 2.5 minutes after TIA starts, then a UAC prompt for
"Yes to all"); don't rebuild it without a reason:

```
dotnet build tools/OpennessRunner -c Release
```

The runner can also be used directly:

```
tools/runner/OpennessRunner.exe <TiaPortalTool.exe> slc-import --slc <file> --out <dir> (--new <dir> <name> [--replace] | --existing <project.ap20>) [--1200] [--verify]
tools/runner/OpennessRunner.exe <TiaPortalTool.exe> export-blocks <project.ap20> <outDir>
tools/runner/OpennessRunner.exe <TiaPortalTool.exe> verify-folder <export.SLC> <exportedBlocksDir>
tools/runner/OpennessRunner.exe <TiaPortalTool.exe> scl-export ...   (recompiles the SLC Support helpers, see SlcHelperSources.cs)
```

Test projects go in `Documents\Automation\SlcTests` (override with `SLC_TEST_PROJECTS`).

## Reference programs

Test programs are not kept in this repository. Use your own RSLogix 500 ASCII exports (`.SLC`, with the `.SY6` /
`.SY5` / `.EAS` symbol file beside them), ideally one small program with few comments (e.g. a MicroLogix) and one large
SLC 5/05 program with full symbols. Note each program's converted/verified rung counts after a good run: a drop in
either count, or any verification mismatch, is a regression.
