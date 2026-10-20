# Festo CTEU-PN valve terminal bus node

The CTEU-PN (Festo 2201471) is a PROFINET bus node for Festo valve terminals. It connects through a terminal-specific
electrical interface, e.g. CPV14-GE-PT-8 (1564984) for CPV14 manifolds.

## TIA configuration
- Install the Festo CTEU GSDML before adding the nodes, and use one GSDML version for all of them.
- Each node is its own PROFINET device with its own device name and IP. Label the hardware with both.
- Put the valve terminal's interface module in the node's slot. The output size follows the terminal (e.g. 2 bytes for 16
  coils). Assign `%Q` addresses to match the schematic's I/O assignment.

## PLC interface
- Outputs only: one `%Q` bit per solenoid coil, written only in the OUTPUT block.
- Valve sense comes from the schematics and the original program. For a **normally-open** valve, an energised coil means
  *close*. Decide once where that is inverted for the HMI and any DCS.
- Double-acting valves have two coils. Check that the program actually writes both.
- Map each node's PROFINET module state to its own comms-loss alarm.

## Gotchas
- Valve coil power usually comes through the safety circuit, separately from the node's logic power. **"Output on in
  the PLC, no LED on the terminal" usually means valve power was dropped by the E-stop circuit.** The PLC won't say so
  unless the E-stop and valve power states are ported as alarms.
- A node that loses PROFINET drops or holds all its valves, depending on its fault-mode parameter.
- Copy-pasted comms-loss alarm logic can point several nodes at one alarm index. Check each node sets its own.

## Per-project checklist
- [ ] GSDML installed and identical for all nodes
- [ ] Device names/IPs assigned and labelled
- [ ] `%Q` assignment matches the schematics; NO/NC sense recorded per valve
- [ ] Fault mode on comms loss matches the valves' fail-safe positions in the spec
- [ ] Comms-loss and valve-power/E-stop alarms present

## Open items
- Reading the node's channel diagnostics (coil short/open) into the PLC: not yet worked out.
