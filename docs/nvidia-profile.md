# Mesura's NVIDIA profile

Mesura's NVIDIA step installs a trimmed driver with NVCleanstall and then imports one driver profile. That profile is
Mesura's own. It was written from NVIDIA's public documentation and is not a copy of anyone else's profile file.

## Where its settings come from

- Setting ids, names and values: NVIDIA's public header
  [`NvApiDriverSettings.h`](https://github.com/NVIDIA/nvapi/blob/main/NvApiDriverSettings.h) (MIT licence). A setting the
  header doesn't list is never in the profile.
- What each setting does: NVIDIA's own help, [Manage 3D Settings (reference)](https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/nv3d/Manage_3D_Settings_(reference).htm)
  and [To set up G-SYNC](https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-us/mergedProjects/Display/To_use_variable_refresh_rates.htm).

## The settings

All three are in the **Base Profile**, which applies to every application. No application profile is created.

| Setting (id) | Value | Why | What it costs |
|---|---|---|---|
| Power management mode (`0x1057EB71`) | Prefer maximum performance | NVIDIA: "Use the GPU only at maximum performance when running most 3D applications." The GPU doesn't spend time raising its clocks when the load changes. | More idle power, heat and fan noise. **Desktops only**: it is left out on a PC that has a battery, or when that can't be told. The gain depends on the game and isn't measured here. |
| VRR requested state (`0x1094F1F7`) | G-SYNC for full screen and windowed | Variable refresh for borderless and windowed games as well as exclusive full screen, which is how most current games run. | Only matters on a G-SYNC or G-SYNC Compatible display. In windowed mode only the window in focus is synchronised, so moving content in another window can look uneven. The driver's default is full screen only. |
| Enable G-SYNC globally (`0x1194F158`) | Full screen and windowed | The matching global switch: the header lists both settings with the same three values and Profile Inspector exports both, so both are set. | The same. |

## Left out on purpose

- **Vertical Sync.** Forcing it on adds latency on a display without variable refresh; the driver's "use the
  application's setting" stays.
- **Maximum pre-rendered frames ("Low Latency Mode").** Left to the driver and the games: forcing one frame helps only
  when the GPU is the limit and can cost frame rate or add stutter when the CPU is, and reports differ on which APIs honour it.
- **Preferred refresh rate.** With G-SYNC on, NVIDIA's help says the control is disabled and the driver uses the highest
  available rate; on a display without variable refresh it would be the only setting that matters, but a duplicate that
  does nothing next to the G-SYNC settings isn't added.
- **Texture filtering, anti-aliasing, anisotropic filtering.** Image quality is the user's choice, and "quality" modes that
  trade picture for speed aren't set for you.
- **Shader cache size.** The header gives the range and a default (`0x4000`) but not the unit.
- **Whisper Mode, NVIDIA Image Scaling's app count, the low-latency frame-rate-limiter state and the "Ultra" latency
  state.** None is in NVIDIA's public header, so an installed driver may not know them, and NVIDIA Profile Inspector then
  skips them without saying so.
- **The preferred OpenGL GPU.** It names one card of one PC.
- **Smooth AFR and other multi-GPU flags.**

## How it is applied

After the driver step (or when none was needed) Mesura imports the profile with NVIDIA Profile Inspector's
`-mergeImport`, which adds or changes only these settings and leaves every other profile and setting as it is, then
exports the driver's profiles and compares each imported setting with the driver's value. A setting the driver didn't
keep is reported by name and the step fails; nothing is reported as done that wasn't. Before the driver step, Mesura
exports your own profiles and puts them back after a clean install; the profile file as imported and that backup are kept
in that setup's folder, `%LOCALAPPDATA%\Mesura\Setup\history\<run>`.

## Undoing it

In NVIDIA Profile Inspector, import the backup in that folder, or use *Restore current profile to NVIDIA
defaults* on the Base Profile. Nothing else about the driver changes with the profile.

## Compatibility

Power management mode needs a GeForce 9 series or later GPU (NVIDIA's help). G-SYNC settings need a G-SYNC or G-SYNC
Compatible display. The profile was not tried on hardware for this release (see the limits in the README): it is
checked by the tests against the format NVIDIA Profile Inspector reads, not on a GPU.

## The second profile: gaming (0.5)

Two built-in profiles, chosen on the NVIDIA card (Main / Gaming / Undo gaming extras). The main profile above is the default and is **unchanged** (its SHA-256 `C6D3F93F76A6530B06E0D998ACFE8BD067376C5C3D7A88F77D53F33B45A9DF51` is pinned by a test, computed from
the committed profile before the gaming one existed); nothing is ever imported unless it is the one chosen.

| Profile | Id | Settings |
|---|---|---|
| Main (default) | `mesura-responsive` | Power management mode (prefer maximum, desktops only), VRR requested state and Enable G-SYNC globally (full screen and windowed) |
| Gaming | `mesura-gaming` | the three above, plus the three below |
| Undo gaming extras | `mesura-gaming-off` | only the three below, at the driver's own defaults |

The three added by the gaming profile, every id and value from NVIDIA's public `NvApiDriverSettings.h` (MIT, read 2026-11-01):

| Setting | id | Value | Why | What it costs |
|---|---|---|---|---|
| Vertical Sync | `0x00A879CF` | `VSYNCMODE_FORCEOFF` `0x08416747` (default `VSYNCMODE_PASSIVE` `0x60925292`) | a frame is shown as soon as it is ready: the lowest delay the driver can give | tearing when the frame rate is not matched to the refresh rate; on a variable refresh display the usual advice is sync on with a frame limit |
| Maximum pre-rendered frames | `0x007BA09E` | `1` (default `0`, application controlled; 1 to 255 a number of frames) | the CPU runs one frame ahead of the GPU: less buffering between input and picture | frame rate can drop or stutter when the CPU is the limit; many DirectX 12 and Vulkan games manage their own queue (not verified here), so the effect varies |
| Shader disk cache maximum size | `0x00AC8497` | `0xFFFFFFFF`, the header's maximum (default `0x4000`) | the driver stops throwing compiled shaders away to stay under a limit, so games do not recompile them | unbounded disk use of the shader cache; whether it removes stutter depends on the game |

Not included: **Low Latency Mode** (its id is not in the public header, so it is unverified and nothing is written for it); any shader cache value in megabytes (the header does not state the unit).
Importing a profile changes nothing outside these settings (merge import, all in the Base Profile), and Mesura backs up the user's other NVIDIA profiles before every import. Reversing the gaming profile is the
"Undo gaming extras" choice, which stores the three defaults explicitly; the backup restores everything else. None of this was imported into a driver (no NVIDIA GPU was available); the effect on frame time or
input lag is not measured and not claimed.
