# TIA Portal Tool

A Windows desktop app (WPF, .NET Framework 4.8) that drives Siemens TIA Portal
through Openness. The start-up screen offers:

- **TIA Portal Import / Export**: tag and block comments out to Excel and back,
  HMI screens out to XML, and bulk import of tag tables and blocks from a folder.
- **SLC 500 → TIA Portal**: converts an RSLogix 500 program into a TIA Portal
  project (in progress).
- **Logix 5000 → TIA Portal**: placeholder, on hold.

This repository holds only the tool's source, tests and docs. No customer or
project data (TIA projects, PLC exports, workbooks, HMI exports) is kept here,
and `.gitignore` blocks those file types.

## Import / Export

- **Export**: opens a TIA Portal project headless and writes Excel workbooks:
  - `System.xlsx`: one sheet per PLC tag table (name, data type, address, and a
    comment column per project language), plus blocks that are not in a group.
  - `<Group>.xlsx`: one workbook per top-level block group, one sheet per block,
    with the block comment and each network's title and comment in every
    project language.
  - SCL/STL blocks get a read-only sheet with the network source for reference.
  - `workbooks_manifest.json` and `_blocks_xml/` (the raw Openness block
    exports), which reimport depends on.
  - Optionally compiles the PLC program first. That fixes blocks that refuse to
    export as "inconsistent" after a PLC data type (UDT) change.
  - **HMI** (option): every HMI in the project as SimaticML XML, one file per
    object, in `HMI\<device>\`: `Screens`, `Templates`, `Popups`, `Slideins`
    (folders kept), `Tags` (one file per tag table), `Connections`,
    `TextLists`, `GraphicLists`, `Cycles`, `Scripts`, plus
    `ScreenGlobalElements.xml`, `ScreenOverview.xml` and `hmi_manifest.json`.
    Classic WinCC (Basic/Comfort/RT Advanced, incl. an IPC) for now; a WinCC
    Unified HMI is reported and skipped. HMI import is next.
- **Reimport & Compile**: reads the edited workbooks back, applies tag comments
  and LAD/FBD block/network comments to the project, then compiles the PLC
  program and reports any compiler errors.
- **Import Folder**: imports a folder of new or changed content, compiles the
  PLC program, and saves only if the compile is clean:
  - `<Table>.tags.tsv`: a PLC tag table, created if missing. Tab-separated
    columns `Name`, `DataType`, `Address`, and an optional `en-US` `Comment`;
    lines starting with `#` are ignored. A tag with the same name in that table
    is replaced; one in a different table is skipped. TIA's auto-created
    `Tag_nnn` tags on the same address are removed.
  - `<group>\<subgroup>\*.xml`: SimaticML block exports, imported with
    override. Subfolders map to block groups (the same layout as
    `_blocks_xml/`), and groups are created as needed. Any language works,
    including SCL.
  - `*.xml` PLC data type exports (`SW.Types.*`) are imported into the top
    level of PLC data types, whichever subfolder they're in.
  - Data types are imported first, then tags, then blocks, so each can use
    the ones before it.
- **Diagnostics**: checks the project file and version, which Openness
  assemblies will be tried and in what order, `Siemens TIA Openness` group
  membership, and whether TIA Portal is running. It never loads an Openness
  assembly, so it can't lock the app onto the wrong version.

Edits to SCL/STL sheets in the workbooks are not reimported. To change SCL,
import the block XML with **Import Folder**.

## SLC 500 converter (in progress)

The start-up screen's **SLC 500 → TIA Portal** option converts an RSLogix 500
program into a new or existing TIA Portal project. In RSLogix 500, use File >
Save As, type `.SLC`; the converter reads the `.SLC` and the `.SY6` symbol file
that Save As writes next to it. The data table becomes:

- One standard-access global DB per data file, numbered like the file (B3 →
  DB3 "B3", N7 → DB7 "N7"), one member per element named after the SLC symbol
  (or the address), with the address and description as the comment and the
  data table values as start values. Standard access keeps the SLC memory
  layout so word, range and indirect operations still line up with the bits;
  bits in B files are declared 8-15 then 0-7 so `%DB3.DBW0` equals `B3:0`.
- Timer files as `IEC_TIMER`, counter files as `IEC_COUNTER`, control files
  as `SLC_CONTROL`.
- `I1` (DB1) and `O0` (DB1000): the I/O image as `zz` tags, which are
  placeholders for the user to map to the new hardware.
- `TIME_SP` (DB1001): a `UDT_TIME_SP` setpoint object (hours, minutes,
  seconds) for each timer preset that is entered from the HMI, including
  values staged through another word or a comms module.
- `SLC_Conversion_Report.xlsx`: rack, program files, instruction counts and
  conversion plan, timers, setpoints, zz tags, the full address map, and
  everything that needs manual work.

**Convert and import** then puts the result into TIA Portal: either a new
project (created with the newest TIA Portal installed, with a basic S7-1500
CPU 1516-3 PN/DP or S7-1200 CPU 1215C; swap in the real CPU with Change
device), or an existing project, where blocks and types with the same name are
overwritten and everything else is left alone. A new project is saved even if
the compile has errors; an existing one only after a clean compile.

The ladder converts to one LAD FC per program file (numbered like the file),
one network per rung, with small commented SCL helpers in an "SLC Support"
group (FC9001+) only where LAD can't express an SLC instruction. After an
import, the tool exports the converted blocks back out of TIA Portal and
checks every rung against the SLC original (every output present, and the
same rung condition by truth table); results go to SLC_Verification.xlsx.

Indirect addressing, MVM and a few other instructions still need manual
conversion; those rungs are marked MANUAL CONVERSION REQUIRED. `tools/SlcConvert` is a console runner for
testing the converter: `SlcConvert <export.SLC> <output folder> [--1200]`.

## Requirements

- Windows with TIA Portal and TIA Portal Openness installed, and the current
  user in the `Siemens TIA Openness` Windows group
- .NET SDK (the app targets .NET Framework 4.8)

## TIA Portal compatibility

- **Tested:** TIA Portal V20 (`.ap20` projects).
- **Expected to work, not tested:** V15 through V19. The tool only uses
  Openness calls that have existed since V15, and it reads block XML by element
  name, so differences between XML schema versions don't matter.
- **The project's version must match an installed TIA Portal.** The tool opens
  projects as they are and never upgrades them. To use an older project with a
  newer TIA Portal, upgrade it in the TIA Portal GUI first.
- **V15.1 (`.ap15_1`) is not supported.** The version is read from a two-digit
  `.apNN` extension, so the tool can't pick the right Openness assembly for it.
- **One TIA Portal version per app session.** .NET can only load one
  `Siemens.Engineering.dll` per process, so restart the app to switch between
  projects of different versions.
- **Projects protected by user management (UMAC) probably won't open.** The
  tool doesn't supply login credentials.

## Usage

1. Close the project in the TIA Portal GUI. Openness needs an exclusive lock.
2. Select the project (`*.ap*`) and, optionally, an export folder. The default
   is `Desktop\TIAExport\<project name>`.
3. Choose what to export (tags, blocks, HMI) and click **Export**.
4. Edit the comments in Excel. Don't rename sheets or move the header rows.
5. Click **Reimport & Compile** with the same export folder selected.

While an action runs, the window switches to a run view: a one-line header and
the full-height Output pane. **← Back to actions** returns to the setup view,
where **Show output** in the status bar reopens the last run's log.

The first run of a new build of the exe triggers TIA Portal's Openness access
prompt (a couple of minutes after TIA starts, sometimes behind other windows);
choose **Yes to all**. If something fails to open, run **Diagnostics** first.

**Cancel** (in the status bar while a run is going) stops after the current
step and closes the project without saving. Starting TIA Portal, opening the
project, and compiling can't be interrupted, so the cancel takes effect when
that step finishes. A cancelled export can leave the export folder partly
updated.

Every export, reimport, and folder import also saves its output log to
`<output folder>\logs\`, so a long compile-error list survives the next run.

## Settings

**⚙ Settings** on the start-up screen sets the style of every window:
**Modern** (dark slate and blue) or **Windows 95** (grey 3D controls on teal).
The change applies at once and is remembered in
`%APPDATA%\TiaPortalTool\settings.json`. Colours live in
`TiaPortalTool/Themes/Palette.*.xaml`, generated by `tools/make-palettes.py`;
corners, fonts and button shapes in `Themes/Shapes.*.xaml`.

## Build and run

```powershell
cd TiaPortalTool
dotnet build -c Release
```

A Release build writes a portable `TiaPortalTool.exe` to the repository root.
Every library is embedded in it (via Costura.Fody), so that single file can be
copied to any machine that has TIA Portal Openness and .NET Framework 4.8.
For development, `dotnet build` and `dotnet run` still build to `bin\Debug`.

The app icon is `TiaPortalTool/app.ico`, generated from
`TiaPortalTool/images/logopng.png` (16 to 256 px, whole image). After changing
the logo, run `powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1`.

## Tests

The scripts in [tools/tests](tools/tests/README.md) check the converter (with and
without TIA Portal), the GUI and the reports. Run them after every change.

## Versioning

- Each tool has its own version (`AppVersions.cs`; the SLC converter's is
  `SlcConverter.ToolVersion`), shown on its launcher badge and in its window.
  Bump it in the commit that changes that tool's behaviour or output: patch for
  a fix or wording, minor for a new feature or changed output. A tool goes to
  1.0 when it's released for real use.
- The app version (`<Version>` in `TiaPortalTool.csproj`) changes when a branch
  is merged into `main` as a release, which is tagged `vX.Y.Z`.
- Tests, scripts and docs don't change any version.

## Further reading

- [docs/OPENNESS_NOTES.md](docs/OPENNESS_NOTES.md): notes on how Openness works
  and the quirks this tool works around
- [Siemens TIA Portal Openness documentation](https://support.industry.siemens.com/cs/document/109756078/tia-portal-openness?dti=0&lc=en-WZ)
