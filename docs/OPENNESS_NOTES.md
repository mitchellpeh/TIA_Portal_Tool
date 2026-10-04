# Siemens Openness — Reference Notes

## What it actually is (layperson version)

TIA Portal is Siemens' engineering software for programming PLCs (the
industrial computers that run factories/machines). Normally a human sits in
the TIA Portal GUI, builds a project, writes logic, and downloads it to a
PLC.

**Openness** is Siemens' name for a .NET API/SDK that lets *code* drive TIA
Portal instead of a human clicking around. It's not a network protocol and
not a separate server — it's a set of .NET assemblies (DLLs) that your own
app loads in-process. Once loaded, your code can:

- Launch a new (optionally invisible/headless) TIA Portal session, or attach
  to one that's already open
- Walk an object model: `Project` → `Devices` → `DeviceItems` → software
  containers → `Blocks` / tag tables
- Read/write block logic, tags, comments, hardware config
- Compile and download to a real or simulated PLC

Think of it as **VBA/COM automation for Office, but for TIA Portal** — or
the **Eclipse plugin API** if you've done IDE tooling. You reference an
assembly, instantiate an entry-point object (`TiaPortal`), and drive the
whole application through that object graph.

## Cross-brand terminology bridge

| If you're used to...                          | The TIA Portal / Openness equivalent |
|------------------------------------------------|----------------------------------------|
| Rockwell **Logix Designer SDK**                | Openness (`Siemens.Engineering.dll`) |
| Rockwell **L5X** project export format         | Siemens `.apXX` project file (binary, version-stamped) |
| Beckhoff **TwinCAT ADS** / TcXaeShell automation | Openness object model |
| Generic "PLC project file"                     | `.apXX` (XX = TIA Portal major version that created it) |
| "Tag database" / "tag table" (Rockwell/Beckhoff) | `PlcTagTable` under `PlcSoftware` in the Openness object model |
| "Routine" / "program" comments                 | Block comments, accessed via block's `Comment` properties |
| SDK "runtime version" vs "project format version" | Openness PublicAPI assembly version vs. TIA Portal project's `.apXX` version — **these must be matched**, see below |

## The versioning gotcha (the thing that bit us)

Every TIA Portal install (V14, V15, V15.1, V16, V17, V18, V19...) ships its
own copy of `Siemens.Engineering.dll` under:

```
C:\Program Files\Siemens\Automation\Portal V{N}\PublicAPI\V{M}\Siemens.Engineering.dll
```

- `{N}` = the TIA Portal install (e.g. `Portal V18`)
- `{M}` = the **API version** that assembly speaks — a single Portal
  install ships multiple API-version folders for backward compatibility
  (e.g. `Portal V18\PublicAPI\` contains `V15.1`, `V16`, `V17`, *and* `V18`
  subfolders, each a distinct `Siemens.Engineering.dll` with its own
  `AssemblyVersion`)

A TIA Portal **project file's extension encodes the version that created
it**: `.ap18` = created/saved in TIA Portal V18 format, `.ap19` = V19, etc.

**Rule of thumb:** to inspect/automate a `.apXX` project, load the
`Siemens.Engineering.dll` from the `PublicAPI\VXX` folder that exactly
matches XX. Loading a mismatched API version (e.g. the old `V14 SP1`
compatibility assembly) will still load *some* type named
`Siemens.Engineering.TiaPortal` successfully — it won't throw — but you get
a much smaller/older surface (no `HardwareCatalog`, `LocalSessions`,
`ProjectServers`, `Authentication`, etc. on V18). This is why a probe that
"succeeds" can still silently be using the wrong DLL — there's no error to
catch, just a smaller API.

### What actually broke (2026-06-24)
[OpennessProjectProbe.cs](../TiaPortalTool/Services/OpennessProjectProbe.cs)
parses the target version out of the project extension with
`extension.Substring(2)` — but `".ap18".Substring(2)` is `"p18"`, not
`"18"` (the prefix is 3 chars: `.`, `a`, `p`). `int.TryParse` silently
failed, so version-aware prioritization never activated and the assembly
list fell back to plain alphabetical order — which happens to put the
oldest `V14 SP1` compatibility DLL first. Fixed to `Substring(3)`.

## Useful object-model landmarks

- `TiaPortal` — entry point. `new TiaPortal(TiaPortalMode.WithoutUserInterface)`
  for headless automation (confirmed working ctor signature:
  `TiaPortal(TiaPortalMode)`).
- `tia.Projects.Open(new FileInfo(path))` → `Project`. **The project file
  must not be open elsewhere** (incl. the TIA Portal GUI) — it's an
  exclusive file lock, not a sharable session.
- **First-run Openness approval**: the very first time an external app
  connects via Openness on a machine, TIA Portal shows a one-time "allow
  this application?" dialog that needs an interactive click. After that
  it's silently approved. This is why headless automation can hang on a
  fresh machine/account — there's no way to pre-approve it programmatically.
- `Project.Devices` → `Device.DeviceItems` → drill into nested `DeviceItems`
  to find the one with a software container.
- **Correct type name** (verified by trial/error — easy to get wrong):
  `Siemens.Engineering.HW.Features.SoftwareContainer`, **not**
  `Siemens.Engineering.HW.SoftwareContainer` (that type doesn't exist).
  `deviceItem.GetService<SoftwareContainer>().Software` gives you the
  `PlcSoftware`.
- `PlcSoftware.BlockGroup` (recursive `.Groups` + `.Blocks`) and
  `.TagTableGroup` (recursive `.Groups` + `.TagTables`).

### Tags — direct property access, no XML needed
`PlcTag.Comment` is a `MultilingualText` with `.Items` (a
`MultilingualTextItemComposition` of `MultilingualTextItem`, each with
`.Language` (enum) and a **settable** `.Text` string). You can read and
write tag comments directly through the object model — no export/import
round trip required for comments alone.

### Blocks — comments are XML-only, not exposed as object-model properties
Confirmed by reflection: `PlcBlock` has **no** `Comment`, `Networks`, or
`CompileUnits` property in the public API. The only way to read or write
block/rung comments is the `Export`/`Import` XML round trip — there is no
shortcut.

Real exported XML structure (from `Main` OB, V18, LAD), confirmed by
exporting a live block with `block.Export(FileInfo, ExportOptions.WithDefaults)`:

```
<SW.Blocks.OB>
  <AttributeList>...Name, ProgrammingLanguage, etc...</AttributeList>
  <ObjectList>
    <MultilingualText CompositionName="Comment">      <!-- block-level comment -->
      <ObjectList>
        <MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>...</Text></AttributeList></MultilingualTextItem>
        <MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>...</Text></AttributeList></MultilingualTextItem>
      </ObjectList>
    </MultilingualText>
    <SW.Blocks.CompileUnit CompositionName="CompileUnits">   <!-- one per rung/network -->
      <AttributeList>
        <NetworkSource><FlgNet xmlns="...FlgNet/v4">
          <Parts>...Call/Part/Access elements = the instructions...</Parts>
          <Wires>...connections between them...</Wires>
        </FlgNet></NetworkSource>
        <ProgrammingLanguage>LAD</ProgrammingLanguage>
      </AttributeList>
      <ObjectList>
        <MultilingualText CompositionName="Comment">...</MultilingualText>  <!-- rung comment -->
        <MultilingualText CompositionName="Title">...</MultilingualText>    <!-- rung title (bold header) -->
      </ObjectList>
    </SW.Blocks.CompileUnit>
    <!-- repeated per rung -->
  </ObjectList>
</SW.Blocks.OB>
```

**Reality check on "instruction comments"**: the individual `<Call>`,
`<Part>`, `<Access>` elements inside `<Parts>` (your contacts, coils, FC/FB
calls) carry **no comment field at all** in this schema. Siemens' Ladder
editor genuinely doesn't support per-instruction commentary — only
block-level and per-rung (Comment + Title). What reads as "instruction
comment" in casual conversation is really the rung Title in TIA Portal's UI.
SCL/STL blocks don't have the per-instruction-comment limitation, since
comments live inside the `StructuredText` source itself — but see the
correction below, this is much harder to get at than it sounds.

**Correction (verified against a real SCL block export): `StructuredText` is
NOT plain text.** It's a tokenized XML tree — every keyword, space run,
newline, and comment is its own element:

```xml
<StructuredText>
  <Token Text="REGION" UId="21" /><Blank Num="1" UId="22" />
  <Text UId="23">MAIN LOOP (ALL INDEXED LOGIC)</Text>
  <NewLine Num="1" UId="24" />
  <LineComment UId="32"><Text UId="33"> Clear HMI registers first</Text></LineComment>
  ...
</StructuredText>
```

Reconstructing readable source means walking `Token`/`Blank`/`NewLine`/`Text`/
`LineComment` (and presumably `BlockComment`/`Access`/`Constant` for real
statements — only partially confirmed) in document order. Writing edits back
means re-tokenizing free-form text into this exact schema — a real
lexer/parser, not a text-node edit. Getting this wrong risks emitting
malformed SCL that breaks (or silently corrupts) on reimport.

`PlcBlock` implements `Siemens.Engineering.SW.ExternalSources.IGenerateSource`
— Siemens' "External Source Files" feature, which generates a genuine
plain-text `.scl` file from a block and (per the feature's normal TIA Portal
workflow) has a counterpart that compiles a plain-text source file back into
blocks. This is almost certainly the right tool for SCL editing instead of
touching the token XML — **not yet verified**, follow up before building SCL
reimport.

Current implementation status: `BlockXmlReader.Detokenize` (in
`TiaPortalTool/Services/BlockXmlReader.cs`) does the token walk above for a
**read-only preview only** — shown in the Excel workbook for SCL/STL blocks,
clearly marked as not reimportable. There is no text-to-token writer. LAD/FBD
block/rung comments (Title + Comment `MultilingualText`) are the only thing
round-tripped through the Excel export/reimport workflow. SCL blocks *can* be
changed by importing a complete, already-tokenized SimaticML file through
Import Folder (`FolderImportService`), since that path never edits the token
tree itself.

Reimport path (LAD/FBD only): edit the exported XML's `Text` nodes inside the
relevant `MultilingualTextItem`/`AttributeList`, then
`PlcBlockComposition.Import(FileInfo, ImportOptions.Override)` for every
changed block, then compile the **whole PLC program once** via
`plcSoftware.GetService<ICompilable>().Compile()` (`PlcCompiler.CompileAll`),
then `project.Save()` only if that compile has no errors.

Why not compile each imported block on its own: it breaks on call order. A block
compiled before something it calls fails with "Block <X> that is accessed has
not been compiled", even though both blocks are fine. Compiling the PLC
software as one unit, which is what TIA's own "Compile All" does, resolves the
call graph itself. Compiler messages nest (`CompilerResultMessage.Messages`), so
the real per-network errors only show up if you walk them recursively.

## Gotcha: the Openness firewall prompts again after every rebuild

Before an app can drive TIA Portal, TIA shows an access prompt ("Openness
firewall"). If nobody answers it, the app just waits, and the prompt can open
behind other windows. TIA has to start up before it can show the prompt, so it
can take a couple of minutes to appear (about 2.5 minutes on the V20 dev
machine). Answering "Yes to all" also triggers a Windows admin (UAC) prompt,
because the approval is written to HKLM. Approvals are stored per exe under
`HKLM\SOFTWARE\Siemens\Automation\Openness\<version>\Whitelist\<exe name>\Entry*`
as `Path`, `DateModified` and `FileHash` (base64 SHA-256 of the exe). Both the
path and the hash must match, so **every rebuild, and every copy in a new
folder, has to be approved again.**

`OpennessFirewall.IsApproved` checks those entries, so the app warns before
connecting when a prompt is coming, and Diagnostics reports it. The app also
warns if starting TIA takes more than 3 minutes (most likely an unanswered
prompt) or opening the project takes more than 2 minutes (usually just a large
project).

## Gotcha: `Siemens.Engineering.Contract.dll` lives in a sibling folder

Constructing `TiaPortal` can fail with a confusing
`MissingMethodException`/`FileLoadException` naming
`Siemens.Engineering.Contract, Version=...`. That dependency assembly is
**not** next to `Siemens.Engineering.dll` in `PublicAPI\VXX\` — it lives in
`Portal VXX\Bin\PublicAPI\Siemens.Engineering.Contract.dll`, a sibling
folder. .NET's default assembly probing for `Assembly.LoadFrom` dependencies
never looks there, so it fails (or worse, resolves a wrong version from
somewhere else on the probing path) unless you register an
`AppDomain.CurrentDomain.AssemblyResolve` handler that explicitly checks
`Portal VXX\Bin\PublicAPI\` for the requested assembly by simple name. This
is standard practice in Siemens' own Openness sample code — see
`TiaSessionService.Open` in
[Services/TiaSessionService.cs](../TiaPortalTool/Services/TiaSessionService.cs)
for the implementation.

Related: `Assembly.LoadFrom` loads into the default load context, which only
allows **one** assembly per simple name for the lifetime of the process. Once
any `Siemens.Engineering.dll` (any version) has been loaded, every other
version's `LoadFrom` call fails with "Assembly with same name is already
loaded" — there's no recovering from a wrong first pick within one process.
This is why getting the version-matching logic right (see the versioning
section above) matters more than it might seem: a wrong first attempt poisons
every fallback for the rest of that session. It's also why **Diagnostics never
calls `Assembly.LoadFrom`**: it reads versions with
`AssemblyName.GetAssemblyName`, which inspects the file without loading it.

## Bigger gotcha: Openness requires .NET Framework, not .NET Core/.NET 5+

After fixing the Contract-assembly resolution above, constructing `TiaPortal`
failed differently:

```
Could not load type 'System.Runtime.Remoting.Lifetime.ISponsor' from assembly 'mscorlib...'
```

`System.Runtime.Remoting` (classic .NET Remoting) was **removed entirely in
.NET Core/.NET 5+** — it doesn't exist in CoreCLR at all, there's no
workaround or polyfill. `Siemens.Engineering.dll` uses .NET Remoting
internally to talk to the actual TIA Portal engineering process across
process boundaries, so this isn't fixable from our side.

This is also why a quick test from Windows PowerShell 5.1 (opening the project,
exporting `Main`) worked with no extra plumbing: **Windows PowerShell 5.1 runs
on full .NET Framework**, which still implements Remoting. This app originally
targeted `net8.0-windows` (.NET 8 / CoreCLR), which is why it failed where
PowerShell succeeded.

**Resolved: the app targets `net48` (.NET Framework 4.8).** That is Siemens'
supported platform for PublicAPI consumers, not a workaround. The SDK-style
`.csproj` with `UseWPF` works under net48, and `dynamic`, `System.Xml.Linq`,
etc. behave the same there. Don't move it back to modern .NET.

## HMI import / export

**Scope:** HMI support in the Import / Export tool: exporting (done, 1.1.0) and
importing (next) HMI **screens, tags and connections** through Openness. The
HMI code sits behind `IHmiExporter` so other HMI types and combinations
(Comfort/Basic panels, Unified panels, Unified PC RT, several HMIs on one PLC)
can be added without a rewrite.

Things to know when extending it:
- Openness has **two separate HMI object models**. Classic WinCC (Basic,
  Comfort, RT Advanced, incl. RT Advanced on an IPC) lives in
  `Siemens.Engineering.Hmi` (`HmiTarget` → `ScreenFolder`, `TagFolder`,
  `Connections`) and exports/imports SimaticML XML much like PLC blocks.
  WinCC Unified (panels and PC RT) lives in `Siemens.Engineering.HmiUnified`
  and is driven mostly through the object model, not XML.
- An IPC shows up as a PC station device; the HMI software sits on the WinCC
  RT application device item under it (e.g. `HMI_RT_1`), not on the device
  itself. A panel is its own station with the runtime item under it.
- Find HMIs by walking every device (incl. nested `DeviceGroups`) and every
  `DeviceItem` recursively, asking each for `GetService<SoftwareContainer>()`;
  check the software type (`HmiTarget` classic, `HmiSoftware` Unified,
  `PlcSoftware` PLC).
- `Projects.Open` fails with "already been opened by user ..." while the
  project is open in TIA. `TiaPortal.GetProcesses()` → match `ProjectPath`
  → `Attach()` reads the open project instead (Dispose just detaches).

**Export (Import / Export 1.1.0, `Services/Hmi`).** `SoftwareLocator` walks every
software container; `HmiSoftwareLocator` picks the HMIs by type name, and
`HmiExportService` hands each to the `IHmiExporter` for its kind (only
`ClassicHmiExporter` so far; Unified is reported and skipped). Tested on an
RT Advanced IPC and a panel; each exports completely (around 100-170 objects)
with 0 warnings in about a minute. Gotchas found on the way:
- `Siemens.Engineering.Hmi.dll` lives in `PublicAPI\VXX` next to
  `Siemens.Engineering.dll`, not in `Bin\PublicAPI`, so the session's
  assembly resolver probes both folders.
- Pass `ExportOptions` to `Export(...)` as `dynamic`. A statically typed
  `object` argument in a dynamic call fails with "The best overloaded method
  match ... has some invalid arguments".
- Slide-in screens have no `Name`; they're named by `SlideinType`
  (Top/Bottom/Left/Right).
- `HmiTarget.Connections` can list 0 connections while every HMI tag refers to
  a connection (e.g. `HMI_Connection`). That's the integrated HMI connection
  made in the network view, which this collection doesn't include. The import
  has to recreate or keep it some other way.
- Screens built from faceplates reference faceplate types in the project
  library; those types must exist before such screens can be imported.
  Screens built from plain objects (buttons, groups, I/O fields) don't have
  that dependency.
- `PlcSoftwareLocator` used to return the first software of any kind; with an
  HMI device listed before the PLC it would have returned the HMI. It now
  filters for `PlcSoftware`.
- ProDiag blocks (e.g. `Default_SupervisionFB`, `ProDiagOB`) can't be exported
  or imported through Openness ("The programming language 'ProDiag' is not
  supported during import and export"). TIA generates them from the
  supervisions, so the export lists them as an expected skip, not a warning.
- Run headless commands through `tools/runner/OpennessRunner.exe <TiaPortalTool.exe> ...`.
  Starting TiaPortalTool.exe with arguments doesn't run them.

## Reference links
- Siemens Openness docs: https://support.industry.siemens.com/cs/document/109479187/simatic-step-7-and-wincc-openess?dti=0&lc=en-WZ
- Siemens Engineering API docs: https://support.industry.siemens.com/cs/document/109773083/siemens-engineering-api?dti=0&lc=en-WZ
- TIA Portal Openness overview: https://support.industry.siemens.com/cs/document/109756078/tia-portal-openness?dti=0&lc=en-WZ
