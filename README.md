# FPS Utility (internal name)

A Windows utility for PC gamers that finds configuration problems costing FPS, explains them,
guides the fix, and measures before and after. It is read-only.

**Status: PERSONAL MVP DEVELOPMENT APPROVED · COMMERCIAL VALIDATION OPEN · COMMERCIAL LAUNCH
BLOCKED UNTIL VALIDATED.** Milestone 1 is implemented: `FpsUtility.Cli scan --json` writes a raw,
platform-neutral machine snapshot. There are no diagnostic checks yet.

| Here | What it is |
|---|---|
| `src/FpsUtility.Core` | the snapshot model and its JSON, scrubbing, the platform check (plain `net10.0`, no OS calls) |
| `src/FpsUtility.Platform.Windows` | the Windows collectors: every OS call lives here |
| `src/FpsUtility.Cli` | `scan --json [--out <path>]` |
| `tests/FpsUtility.Core.Tests` | serialisation, determinism, model rules, the platform check, scrubbing |
| `docs/architecture.md` | the MVP technical architecture and its decisions (§17) |
| `CLAUDE.md` | project facts, the gate and the commands |

Build with `dotnet build`; test with `dotnet test`. The full planned layout is in
`docs/architecture.md` §3.1.

Sibling folders in the same workspace:
- `..\vault\`: the product's Obsidian vault, holding decisions, validation and product knowledge.
- `..\research\`: temporary evidence. It is not versioned.
