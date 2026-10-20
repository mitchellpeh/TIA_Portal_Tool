# SIMATIC IPC with WinCC Runtime Advanced

Applies to SIMATIC panel and box IPCs running classic WinCC Runtime Advanced.

## TIA configuration
- The HMI is a **PC station** with a WinCC RT Advanced application, not a panel device. Openness finds the HMI
  target on a device item under it, and the station may be in `project.DeviceGroups` rather than `project.Devices`.
- The integrated S7 connection isn't listed in `HmiTarget.Connections`. Imports must keep it rather than recreate it.
- Runtime options (Audit Trail, Logging, Recipes, Sm@rt) are separate licences. Check them against the BOM and the
  runtime settings.
- Add every runtime language to the project languages before importing translated texts.
- Screen resolution follows the display. When porting 4:3 screens to a 16:9 IPC, re-flow them instead of stretching.

## PLC interface
- Symbolic tags to optimised DBs are the norm. Absolute addressing works only to standard-access DBs.
- Discrete alarms: the **PLC acknowledge bit must be in the same tag as the trigger**. Use Int/Word registers, and
  no Array of Bool larger than 16 bits as a trigger.
- The S7-1500 serves HMI reads during the cycle. Values written and cleared within one scan flicker, so copy them to
  HMI members at the end of the scan.
- Real tags need `Coding = IEEE754Float`; Bool tags allow only range 0/1.

## Gotchas
- Classic import rules that fail compiles or crash imports: `../LESSONS_LEARNED.md` §6.
- Discrete alarms, alarm classes and alarm view settings aren't in the classic Openness export. Use the GUI or the alarm
  editor's xlsx import, with only the editor's own columns.
- Language-dependent images: either a language-dependent project graphic, or a Loaded-event VBScript that reads the
  runtime LCID into an internal tag driving the Visibility of stacked graphic views.
- Headless TIA next to an open project uses a lot of RAM. Close what isn't needed.
- Library faceplates and duplicate template object names produce many compile warnings. Record the count before and
  after each change.

## Per-project checklist
- [ ] Runtime licences/options match the BOM
- [ ] Project languages and runtime language switch work
- [ ] Alarm classes, ack behaviour and alarm view match the spec
- [ ] User administration / login levels set up as specified
- [ ] Baseline HMI compile warning count recorded

## Open items
- None.
