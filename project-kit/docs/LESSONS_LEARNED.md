# Lessons learned: legacy PLC → TIA Portal migrations

These are generic lessons from an earlier SLC 5/05 → S7-1516 + WinCC RT Advanced migration. Each one cost
time at least once. Check the relevant section before designing, porting, reviewing or importing.
Project-specific facts don't belong here; they go in the project's own docs and memory.

## 1. Hand-port defects to look for

A machine converter makes few of these mistakes; a hand port makes them easily. Run `verify-export` and
`lint-export` (TIA Portal Tool) on every fresh export, then read the networks they flag.

| Pattern | What goes wrong | Check |
|---|---|---|
| One-shot edge memory in FC **Temp** | Edge never/always fires; Temp is garbage each call | Every P/N contact and R_TRIG needs its own static/global bit |
| Literal timer **PT** next to a `MOVE` into `.PT` | The literal wins; HMI setpoint ignored (e.g. step timers at 1 s) | Box PT wired to the setpoint object's output |
| `.TT` ported as `.IN` | Timing bit true before/after the timer runs | `.TT` = `IN AND NOT Q` |
| RTO ported as TON | Retentive timer resets on every pause | `TONR`, with reset on the original's RES |
| A timer that resets itself | Timeout never expires | Compare with the original's reset rung |
| OTL/OTU turned into plain coils | Latch drops the next scan | Set/Reset |
| Inverted subtraction / compare | e.g. decay = current − start instead of start − current: a test that can never fail | Operand order against the original, rung by rung |
| Inverted clear condition (`IF x` vs `IF NOT x`) | Commands cleared exactly when they're needed | Original's XIC/XIO on the clear rung |
| Contact added in front of an unconditional OTU/reset | Bit never clears (e.g. HMI pushbutton stuck) | Unconditional in the original → unconditional in TIA |
| Ring buffer `F[N]` replaced by a single tag | "Average" = sample / N or a running sum | Array + index, or a proper filter |
| Counter: literal PV, `R` wired FALSE, reset by writing PV | Never resets, preset not adjustable | PV ← setpoint (or own `.PV` if logic writes it), R ← original reset, reset `CV` |
| Network = copy of the next rung | Two identical networks, original rung missing | Rung-by-rung check, not network count |
| Named alarm/status bits **read but never written** | Alarm never raised (E-stop alarm missing = safety gap) | Read-but-never-written scan over all DB members |
| Input moves never ported | Analog PV reads 0; a PID winds its output to 100 % | Every raw input copy in the original's INPUT file |
| Placeholders left in (`SP := own output`, AlwaysFALSE/AFI contacts from checkout) | Loop or output silently dead | Grep for AlwaysFALSE, AFI, self-assignments |
| A block called twice / its status block never called | Logic runs twice per scan; messages/timer PTs never set | Call list against the original JSR order |
| Init FC never called, DB NonRetain with no start values | Recipe/step tables empty after every restart | Start values in the DB, Retain like the original data files |
| HMI edit buffers written back every scan | Overwrites any bulk table load | A bulk load must also refresh the buffers |
| DCS/SCADA map writing straight into internal mode/enable bits | Remote overwrites local HMI selections every scan | Port the original's remote/local gating |

Network numbering is usually rung + 1, but titles drift after inserted rungs. Match networks to rungs by
**logic**, not by title.

## 2. Analog, scaling and counting
- SLC analog counts (e.g. 3277..16383 for 4–20 mA) ≠ S7 (0..27648). Re-derive every scaling min/max. Don't
  copy the SLC values. An output scaled to the old range drives the valve to only part of its stroke.
- Scaling with min = max = 0, or an average size of 0, gives 0/0 = **NaN**, which propagates everywhere. Check
  start values.
- `DINT_TO_INT` on a counter rolls over into negative spikes.
- PID: use `PID_Compact` in engineering units, Ti min → s, and check its input limits against the PV range (the default
  0..120 trips on raw counts). Wire HMI tuning values or say they're unused. Plan a retune.
- High-speed input (STI + rollover math in the original): use a TM Count/HSC technology object, read
  `MeasuredValue` (Frequency) in a cyclic OB. Pulses per rev follow from the original calibration. Check the
  TO update time at the lowest speed that matters, e.g. a "stopped" threshold.

## 3. DCS / SCADA interface
- Use the register map version the user names. Re-check each time they mention a new revision.
- **Modbus discrete words were byte-swapped:** sheet row r (a..p) ↔ S7 `Word.%X(r XOR 8)`. Prove it with one
  live bit before rebuilding a map.
- Packing tags "in sheet row order" is not the same as packing them at the sheet's register addresses. Build maps
  from the address column.
- Alarm arrays sent to the DCS must be in the original's bit order, not the new project's alarm order.
- Normally-open valves: the output means *close*. Decide once where the sense is inverted (HMI animation,
  DCS bit), and check whether a library faceplate already inverts it, to avoid a double inversion.

## 4. Scan model differences
- An S7-1500 serves HMI reads **during** OB1; an SLC served comms between scans. A value written and then cleared
  within one scan (operator message, countdown) flickers on the HMI. Copy display values to `_HMI` members at
  the end of the scan.
- A command bit cleared in the same scan by a block that runs earlier than its consumer is never seen. An example is ack-all
  cleared by alarm management before the drive block reads it. Check call order whenever you change who clears a bit.

## 5. Alarms (WinCC classic / RT Advanced)
- A discrete alarm's **PLC acknowledgement bit must be in the same tag as its trigger bit**. A separate tag is
  rejected ("acknowledgment tag of discrete alarm invalid"). One working layout uses Int registers with 8 alarms per
  word: trigger = bit i, PLC ack = bit i+8.
- Trigger tags of type Array of Bool with more than 16 bits fail. Use Word/Int registers packed with SHL.
- Check that **every** PLC alarm/warning array has HMI discrete alarms. A whole warnings array was once never shown.
- Alarm text index ≠ bit number. Keep a cross-check table (index → text → trigger logic) and check text
  against logic: swapped left/right, duplicates, "SPARE" alarms that are actually set.
- To mirror the original's acknowledge behaviour (e.g. ack = unlatch with a 2 s mask, Ack All, warnings
  cleared while a fault is active), read the original's alarm rungs first. Don't assume the WinCC default ack.
- Classic discrete alarms aren't in the Openness HMI export. Alarm classes and alarm-editor xlsx imports are
  manual GUI steps. An extra column in the alarm import xlsx can wipe the existing texts, so import only the
  editor's own columns.

## 6. WinCC classic import rules (each of these crashed an import or failed a compile)
- Ellipse `RadiusX = floor(Width/2)`. Line `Width/Height = |End − Start|` on integer coordinates.
- An IO field format pattern with decimals (`999.9`) on a non-Real tag is a compile error. Use ShiftDecimalPoint for
  scaled Int tags.
- Real HMI tags need `Coding = IEEE754Float`. Bool HMI tags allow only range 0/1.
- Screen links to screens that don't exist in the target are compile errors. Re-point or strip them.
- Faceplate library types must exist before screens that use them. Faceplate internals aren't exported.
- Windows drops a trailing `.` from folder names, so a block group "x." exported to folder "x". The tool handles
  this now; older builds created a duplicate group.
- "Compilation failed. Please contact SIEMENS customer support" can be transient. Replay the same package on a
  copy before debugging it.

## 7. Texts and translations
- For bulk text changes, use **Project texts** export/import (xlsx, source/target culture columns) in the GUI. Leave
  "Import source language" unticked when only target texts change.
- If the job is to mirror the original HMI, its language exports are the truth. Copy its translations even where
  they look odd, and flag suspected errors (inverted On/Off meanings) instead of "fixing" them silently.
- Match strings after normalising line breaks, whitespace, case and punctuation (about 85–90 % match directly).
- Text lists, operator messages and alarm texts each live in different places. Check all three.

## 8. Openness and tooling
- Openness = .NET Framework 4.8, one `Siemens.Engineering.dll` version per process, firewall approval per exe hash.
- PowerShell-hosted Openness can hit a StackOverflow. Compile a small C# helper with `csc` instead.
- The HMI device may sit in `project.DeviceGroups`, not `project.Devices`.
- A tool command that deletes and recreates blocks loses their group, so don't use it on real projects.
- Removing DB members in the same run as a tag-table export gave "disposed object". Leave unused members for a later run.
- SaveAs into a subfolder of the open project fails ("storage medium no longer available").
- SCL external source generated from XML: the converter must keep named call parameters. It once dropped
  `Parameter Name=` and broke a block. Fail loudly on any element it can't translate.
- An external source regenerates the block and may drop it into the top-level group. Move it back or use XML.
- PowerShell `*>` logs are UTF-16, so decode them before grepping.

## 9. Drives (SINAMICS G120 family, PROFINET)
Full device notes, including telegram 352 for motor amps: `hardware/G120.md`.
- Read the drive's **online parameter export** before trusting drawings. The actual CU/PM type differed from the drawing once.
- Standard telegram 1 speed is clamped at 200 % of `p2000` (32767/16384). Raising `p1082` (max speed) alone
  doesn't help. `p2000` and the SinaSpeed `RefSpeed` must go up too.
- Ramp times are referenced to `p1082`, so raising `p1082` makes the ramps steeper (V/f Imax alarms). Scale
  `p1120/p1121` with it, and check that the change was saved in the active data set.
- **Check the motor connection against the motor data in the drive.** A 230/460 V motor connected for 460 V but
  commissioned with 230 V / 2× current data ran at half flux. Symptoms: speed short under load, slow
  acceleration, motor ID fault (magnetising reactance out of range), overload current set too high.
- Startdrive has no parameter-value import. Deliver a step-by-step entry sheet (CSV) instead.
- Use standard Siemens blocks (SinaSpeed) for telegram 1 drives. Decide the fault-reset (`AckError`) source per drive and
  check it against call order (section 4).

## 10. Commissioning
- "Output on in the PLC, no LED at the valve island" was the safety circuit dropping valve power. If the E-stop
  alarm is missing (section 1), the PLC can't tell you that.
- A short on 24 V can damage a technology card even after the supply is restored (the card reports missing L+
  while the terminals measure 24 V). Measure, then suspect the card.
- Remove checkout workarounds (AFIs, disabled calls, AlwaysFALSE contacts) explicitly. Keep a to-do list of them.
