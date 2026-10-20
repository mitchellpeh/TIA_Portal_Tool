# Hardware configuration reference

Device-type notes for the TIA hardware configuration: how to set the device up, how the PLC talks to it, known
gotchas, and what to verify on every project. These files describe device types only. A project's own device
list, names, addresses, ratings and status go in that project's docs, never here.

| Device type | File |
|---|---|
| SINAMICS G120 family drives on PROFINET | [G120.md](G120.md) |
| SIMATIC IPC running WinCC Runtime Advanced | [IPC_HMI.md](IPC_HMI.md) |
| Festo CTEU-PN valve terminal bus node | [FESTO_CTEU-PN.md](FESTO_CTEU-PN.md) |

Each file has the sections **TIA configuration**, **PLC interface**, **Gotchas**, **Per-project checklist** and
**Open items**. Open items are about the reference itself (a pattern not yet proven on hardware). When one is
proven, close it with the date and how it was verified.
