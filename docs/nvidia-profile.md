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
