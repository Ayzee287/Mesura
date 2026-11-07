# Exact composition of the two 0.6.0 profiles

Written 2026-11-04 after the owner's final decisions on the profile composition (section 1). Generated from `src/Mesura.Core/Profiles/profiles.json` and `windows.catalog.json`, so it is the composition the code holds; a test
(`ProfileDecisionsTests`) pins the parts decided here. Profile ids (`recommended-gaming` = Mesura Reference, `maximum-performance`) and the Profile Planner are unchanged. Nothing was applied to a real PC.

## 1. The decisions this composition carries

1. **AMD Anti-Lag**: only the original Anti-Lag, level 0, only for a Radeon GPU that offers it; Anti-Lag+ and Anti-Lag Next are excluded in code (a request that finds level 1 or an unknown level is refused, no level setter exists). The side effect, the driver turning Radeon Chill off (and Boost, which depends on it), is recorded before the write and put back on undo, and shown beside the entry. In Maximum performance only.
2. **Hardware-accelerated GPU scheduling (HAGS)**: not applied by default anywhere. The rule: on by default only with a confirmed positive performance effect; absent, ambiguous or unconfirmed means not by default. The research (`docs/gpu-hags-research.md`, second pass 2026-11-04) found no controlled measurement of a gain and mixed frame-time results, so the entry is **optional (unticked)** in Maximum performance and is not in Mesura Reference.
3. **Network adapter presets**: the five per-maker presets are default entries in **both** profiles. Each acts only on a physical adapter of its own maker and kind, only on settings the adapter's driver declares with the wanted values (checked against the live driver before every write), with a preview, the values from before recorded, an independent read-back and a rollback; an adapter no preset is for, or whose driver declares none of the settings, is left alone and the plan says why. No gain in frame rate or latency is claimed: none was measured.

Nothing else in either profile was changed by these decisions.

## 2. What the three words mean on a PC

- **Default**: applied with the profile unless it is already in place, its group is switched off, or the user unticks it. Applied only when its condition holds.
- **Conditional**: the "Applied only when" column. A condition that does not hold makes the entry **not supported** on that PC: it is listed under "not on this PC" with the reason, and nothing is written. A condition that could not be read makes it **can't tell**, and it is left alone.
- **Optional (unticked)**: offered beside the profile and never applied unless the user ticks it.

## 3. Entries

### Mesura Reference (`recommended-gaming`)

| Group | Operation | Role | Cost shown | Applied only when |
|---|---|---|---|---|
| Telemetry | `privacy.diagnostic-data-required` | default | yes | Windows Pro or higher |
| Telemetry | `telemetry.ceip-policy-off` | default |  | Windows Pro or higher |
| Telemetry | `telemetry.application-telemetry-off` | default |  | Windows Pro or higher |
| Telemetry | `telemetry.inventory-collector-off` | default | yes | Windows Pro or higher |
| Telemetry | `telemetry.limit-diagnostic-logs` | default |  | Windows Pro or higher; Windows build 22000+ |
| Telemetry | `telemetry.limit-dump-collection` | default | yes | Windows Pro or higher; Windows build 22000+ |
| Telemetry | `telemetry.onesettings-downloads-off` | optional (unticked) | yes | Windows Pro or higher; Windows build 22000+ |
| Privacy and promotions | `privacy.no-advertising-id` | default |  | always (if not already in place) |
| Privacy and promotions | `privacy.no-tailored-experiences` | default |  | always (if not already in place) |
| Privacy and promotions | `privacy.no-language-list` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-feedback-prompts` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-tips-and-suggestions` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-setup-nags` | default |  | always (if not already in place) |
| Privacy and promotions | `lockscreen.no-spotlight-ads` | default |  | always (if not already in place) |
| Privacy and promotions | `start.no-recommendations` | default |  | Windows build 22621+ |
| Privacy and promotions | `start.no-account-notifications` | default |  | Windows build 22621+ |
| Privacy and promotions | `suggestions.no-third-party-in-spotlight` | default |  | Windows Pro or higher |
| Privacy and promotions | `search.no-highlights-policy` | default |  | Windows Pro or higher; Windows build 19042+ |
| Windows | `repair.ncsi-active-probing` | default |  | always (if not already in place) |
| Windows | `repair.threaded-dpc` | default |  | always (if not already in place) |
| Power management | `power.mesura-balanced-plan` | default |  | always (if not already in place) |
| Network | `network.preset-realtek-ethernet` | default | yes | adapter: ethernet; a physical adapter of Realtek whose driver lists each setting |
| Network | `network.preset-intel-ethernet` | default | yes | adapter: ethernet; a physical adapter of Intel whose driver lists each setting |
| Network | `network.preset-killer-ethernet` | default | yes | adapter: ethernet; a physical adapter of Killer whose driver lists each setting |
| Network | `network.preset-intel-wifi` | default | yes | adapter: wifi; a physical adapter of Intel/Killer/Rivet whose driver lists each setting |
| Network | `network.preset-realtek-wifi` | default | yes | adapter: wifi; a physical adapter of Realtek whose driver lists each setting |
| Gaming and latency | `gaming.game-mode-on` | default |  | always (if not already in place) |
| Gaming and latency | `input.accessibility-shortcuts-off` | default |  | Windows must report support (accessibilityHotkeys) |

27 default and 1 optional entries.

### Maximum performance (`maximum-performance`)

| Group | Operation | Role | Cost shown | Applied only when |
|---|---|---|---|---|
| Telemetry | `privacy.diagnostic-data-required` | default | yes | Windows Pro or higher |
| Telemetry | `telemetry.ceip-policy-off` | default |  | Windows Pro or higher |
| Telemetry | `telemetry.application-telemetry-off` | default |  | Windows Pro or higher |
| Telemetry | `telemetry.inventory-collector-off` | default | yes | Windows Pro or higher |
| Telemetry | `telemetry.limit-diagnostic-logs` | default |  | Windows Pro or higher; Windows build 22000+ |
| Telemetry | `telemetry.limit-dump-collection` | default | yes | Windows Pro or higher; Windows build 22000+ |
| Telemetry | `telemetry.onesettings-downloads-off` | optional (unticked) | yes | Windows Pro or higher; Windows build 22000+ |
| Privacy and promotions | `privacy.no-advertising-id` | default |  | always (if not already in place) |
| Privacy and promotions | `privacy.no-tailored-experiences` | default |  | always (if not already in place) |
| Privacy and promotions | `privacy.no-language-list` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-feedback-prompts` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-tips-and-suggestions` | default |  | always (if not already in place) |
| Privacy and promotions | `notifications.no-setup-nags` | default |  | always (if not already in place) |
| Privacy and promotions | `lockscreen.no-spotlight-ads` | default |  | always (if not already in place) |
| Privacy and promotions | `start.no-recommendations` | default |  | Windows build 22621+ |
| Privacy and promotions | `start.no-account-notifications` | default |  | Windows build 22621+ |
| Privacy and promotions | `suggestions.no-third-party-in-spotlight` | default |  | Windows Pro or higher |
| Privacy and promotions | `search.no-highlights-policy` | default |  | Windows Pro or higher; Windows build 19042+ |
| Windows | `repair.ncsi-active-probing` | default |  | always (if not already in place) |
| Windows | `repair.threaded-dpc` | default |  | always (if not already in place) |
| Windows | `taskbar.widgets-policy-off` | default | yes | Windows Pro or higher; Windows build 22000+ |
| Windows | `edge.no-startup-boost` | default | yes | always (if not already in place) |
| Windows | `edge.no-background-mode` | default | yes | always (if not already in place) |
| Windows | `dwm.window-animations-off` | default | yes | Windows Pro or higher |
| Windows | `visual.transparency-off` | optional (unticked) |  | always (if not already in place) |
| Power management | `power.mesura-maximum-plan` | default | yes | always (if not already in place) |
| Power management | `power.usb-keep-awake` | default | yes | always (if not already in place) |
| Power management | `power.disk-never-off` | default | yes | always (if not already in place) |
| Power management | `power.pcie-aspm-off` | default | yes | always (if not already in place) |
| Processor | `cpu.core-parking-off` | default | yes | always (if not already in place) |
| Processor | `power.cpu-epp-performance-ac` | default | yes | always (if not already in place) |
| Processor | `power.cpu-ramp-up-fast-ac` | default | yes | Windows build 22000+ |
| Processor | `cpu.hybrid-prefer-performance-cores` | default | yes | a hybrid processor; Windows build 22000+ |
| Processor | `power.power-throttling-off` | default | yes | Windows Pro or higher; Windows build 19041+ |
| Memory | `memory.compression-off` | default | yes | 16 GB of memory or more; Windows build 14393+ |
| Memory | `memory.keep-kernel-in-ram` | default | yes | 16 GB of memory or more |
| Graphics | `gpu.amd-anti-lag-on` | default | yes | GPU: amd; the AMD driver offers Anti-Lag at the original level |
| Graphics | `gpu.hardware-scheduling-on` | optional (unticked) | yes | GPU: any; Windows build 19041+; Windows must report support (gpuScheduling) |
| Storage | `system.ntfs-last-access-off` | default | yes | Windows must report support (ntfsLastAccess) |
| Storage | `system.ntfs-8dot3-off` | default | yes | always (if not already in place) |
| Storage | `power.nvme-stay-active-ac` | default | yes | drive: nvme |
| Storage | `power.sata-link-power-off-ac` | default | yes | drive: sata |
| Network | `updates.no-p2p-delivery` | default |  | Windows Pro or higher |
| Network | `network.preset-realtek-ethernet` | default | yes | adapter: ethernet; a physical adapter of Realtek whose driver lists each setting |
| Network | `network.preset-intel-ethernet` | default | yes | adapter: ethernet; a physical adapter of Intel whose driver lists each setting |
| Network | `network.preset-killer-ethernet` | default | yes | adapter: ethernet; a physical adapter of Killer whose driver lists each setting |
| Network | `network.preset-intel-wifi` | default | yes | adapter: wifi; a physical adapter of Intel/Killer/Rivet whose driver lists each setting |
| Network | `network.preset-realtek-wifi` | default | yes | adapter: wifi; a physical adapter of Realtek whose driver lists each setting |
| Gaming and latency | `gaming.game-mode-on` | default |  | always (if not already in place) |
| Gaming and latency | `input.accessibility-shortcuts-off` | default |  | Windows must report support (accessibilityHotkeys) |
| Gaming and latency | `gaming.game-recording-policy-off` | default | yes | Windows Pro or higher |
| Gaming and latency | `input.pointer-precision-off` | default | yes | Windows must report support (pointerAcceleration) |
| Gaming and latency | `gaming.background-recording-off` | optional (unticked) |  | always (if not already in place) |
| Gaming and latency | `gaming.controller-game-bar-off` | optional (unticked) |  | always (if not already in place) |

49 default and 5 optional entries.

## 4. How the new entries read on different PCs

Planned by the real planner; the first row is the development laptop (a read-only plan, `mesura windows plan --profile maximum-performance`), the others are the stand-in PCs of `ProfileDecisionsTests`.

| PC | Anti-Lag (Maximum) | Network presets (both profiles) | GPU scheduling (Maximum, optional) |
|---|---|---|---|
| Laptop, Intel Wi-Fi AX201, Intel graphics (real read) | not supported: `hardware.gpu.amd` | Intel and Killer Wi-Fi: **to apply**; Realtek Wi-Fi: not supported (no Realtek Wi-Fi adapter); the three Ethernet presets: not supported (`hardware.network.ethernet`) | not supported here (Windows reports no support) |
| Desktop, Radeon that offers Anti-Lag, Anti-Lag off | **to apply** | by the adapters found | offered, unticked |
| Radeon with Anti-Lag already on | already applied | | |
| Radeon with Anti-Lag Next selected (level 1) | can't tell, left alone: "Anti-Lag Next is the selected level" | | |
| NVIDIA or Intel graphics | not supported: `hardware.gpu.amd` | | |
| AMD driver without ADLX, or ADLX would not start | not supported / can't tell, with the reason | | |
| Desktop with a Realtek Ethernet card only | | Realtek Ethernet: **to apply**; Intel and Killer Ethernet: not supported (no such adapter); the Wi-Fi presets: not supported (`hardware.network.wifi`) | |
| Ethernet card of the right maker whose driver declares none of the settings | | not supported: "offers none of these settings with the wanted values" | |
| Adapters could not be read | | can't tell, left alone | |

## 5. What is and is not verified (final build f8e633a, 2026-11-04)

- **Composition**: pinned by `ProfileDecisionsTests` (ids, roles, conditions, costs, the catalog presets equal to the profiles, a saved setup profile naming both levels), and by the Windows Sandbox profile run on the final build
  (`docs/sandbox-results/final-0.6.0/profiles-e2e-profiles.txt`, 78 PASS, 0 FAIL): the five presets are entries of each level and none is planned or Applied without a physical adapter of its maker (the Sandbox has only a Hyper-V
  adapter), Anti-Lag is an entry of Maximum only and is not planned without an AMD adapter (reason `hardware.gpu.amd`), HAGS is optional and not applied.
- **Anti-Lag**: the writer (record first, write, an independent session reads it, rollback) ran a full cycle on a real RX 9070 XT, ADLX 1.5.0.124, on 2026-11-04 (`docs/hardware-test-plan-amd-radeon.md`). The profile engine's route (read, write, record, read again, undo from
  the writer's record) is covered with a stand-in driver, and from the window through a real child process that dies or hangs. **The profile apply of this operation on a Radeon PC was not run.**
- **Network presets**: the keyword names and value lists come from real driver INFs of Windows 11 build 26100 and, for Intel Wi-Fi, from a real read of an AX201. **Only the Intel Wi-Fi preset was written on a real adapter (the development laptop, Intel AX201, 2026-11-05: applied, read back by Windows, undone; no measurable latency effect on Wi-Fi); no other maker's preset was.** The engine's adapter path ran against stand-ins
  (virtual and VPN adapters, GUID addressing, two cards of one maker, a driver that keeps nothing, an adapter that stays down) and read-only against the laptop's adapters. The kit that tests it on a real adapter is built and not run (`docs/network-real-adapter-test.md`).
- **Effects**: no frame rate, frame time or latency effect of any of these entries was measured.
- **Not in any profile on purpose**: SmartAccess Memory (the driver refuses it on the one card tried), FreeSync (unsupported on both monitors tried), Chill, Boost, tuning values.