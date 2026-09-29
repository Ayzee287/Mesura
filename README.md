# FPS Utility (internal name)

A Windows utility for PC gamers that finds configuration problems costing FPS, explains them,
guides the fix, and measures before and after. That is the MVP, and it is read-only.

The long-term direction is a performance engineering and tuning tool for gaming PCs. It is set out in
[`docs/product-vision.md`](docs/product-vision.md): understand the system, improve it deliberately,
prove the result, keep it stable. It is a direction, not a plan. Nothing beyond the MVP is built or
authorised, and none of it has been validated with users.

**Status: PERSONAL MVP DEVELOPMENT APPROVED · COMMERCIAL VALIDATION OPEN · COMMERCIAL LAUNCH
BLOCKED UNTIL VALIDATED.** Milestones 1 to 3A are implemented: `FpsUtility.Cli scan --json` writes a raw,
platform-neutral machine snapshot, `normalize` turns one into facts, and `check` runs the first two of the eight
checks on it (1, refresh below the highest offered; 2, a monitor on the integrated GPU). Nothing is Confirmed until an
owner-labelled fixture covers it, and checks 3–8, explanations and the UI are not built.

| Here | What it is |
|---|---|
| `src/FpsUtility.Core` | the snapshot model and its JSON, scrubbing, the platform check, the normaliser and facts, the check-result model and checks 1 and 2 (plain `net10.0`, no OS calls) |
| `src/FpsUtility.Platform.Windows` | the Windows collectors: every OS call lives here |
| `src/FpsUtility.Cli` | `scan`, `capture-fixture`, `normalize`, `check` (see `CLAUDE.md`) |
| `tests/FpsUtility.Core.Tests` | serialisation, determinism, model rules, the platform check, scrubbing, facts, checks, fixtures |
| `tests/FpsUtility.Platform.Tests` | collector mapping and path rules on fake inputs, one real collection (Windows only) |
| `docs/architecture.md` | the MVP technical architecture and its decisions (§17), and its long-term compatibility review (§18) |
| `docs/product-vision.md` | the long-term product direction (D015): a direction, not a roadmap commitment |
| `CLAUDE.md` | project facts, the gate and the commands |

Build with `dotnet build`; test with `dotnet test`. The full planned layout is in
`docs/architecture.md` §3.1.

Sibling folders in the same workspace:
- `..\vault\`: the product's Obsidian vault, holding decisions, validation and product knowledge.
- `..\research\`: temporary evidence. It is not versioned.
