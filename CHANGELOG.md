# Changelog

## 0.8.0 (2026-11-25)

Reliability and hardware awareness. No new settings, no change to what Mesura Reference or Maximum performance apply, nothing new in the catalog.

- **Runs survive a crash.** Before Mesura changes a Windows setting, it writes the setting's previous value to the run's journal on the disk (`history\<run>\journal.jsonl`, flushed before the change). If Mesura stops in the middle of a run (a crash, a power cut, a killed process or a closed console), the next start finds the journal, reads the PC as it is now, and turns what it finds into an Activity entry marked **Interrupted**, with Undo: changes still in place, and changes that hold another value now or can't be read, are listed; changes that were never made are left out. **Nothing is undone automatically**; you decide. Undo still restores a setting only where it holds the value the run wrote, so a later change of yours is left alone, and an undo that was itself interrupted can simply be run again. The window does this at start; the command line before `windows apply`, `windows revert`, `profile run --apply` and `history`. A journal that can't be written stops the change it describes, and the operation puts back what it had already changed.
- **History, kept files and AMD change records are written whole.** Each is written to a temporary file, flushed to the disk and then moved over the old one, so a power cut leaves the old file or the new one, never half of one. This matters most for a setup on Optimization, which rewrites its record after every step.
- **Ryzen X3D with two core dies is recognised.** Mesura reads the processor's cache layout from Windows (`GetLogicalProcessorInformationEx`); two L3 caches of clearly different size on an AMD processor mean a dual-CCD X3D (7900X3D, 7950X3D, 9900X3D, 9950X3D), where AMD's driver parks one die for games. The model name counts only when the layout can't be read, and an unreadable processor stays "unknown". `mesura hardware` shows the L3 caches and the answer with its reason. **No profile uses it yet**, and it has not been checked on a real dual-CCD X3D processor.
- **Fixed: Maximum performance could lose "cores unparked".** On a PC whose active plan already had core parking off (High performance, for example), the entry was planned as "already set"; the run then switched to the Maximum Mesura plan, which starts from Balanced, and core parking was back at Balanced's value. Such a power entry is now applied after the plan switch.
- **No more endless "Reading this PC…".** The BIOS & Firmware page and Optimization's system and hardware readings wait at most 30 seconds for Windows; past that the page says so, goes on, and shows the reading when it arrives.
- **Internal:** every place in Mesura that applies or undoes Windows settings must hand over its journal; calling the engine without one does not compile in Mesura's own programs.

Known limits in 0.8.0:
- An **AMD Anti-Lag** change is saved in its own record before the driver is touched, but is added to the run's journal only after the write. If Mesura stops exactly then, the change is not in the Interrupted entry: `mesura gpu amd changes` lists it and `mesura gpu amd undo <id> --apply` puts it back.
- An interrupted **app-settings import** keeps the backups of the files it replaced (in the run's history folder), but gets no Activity entry or Undo: copy a backup back by hand if needed. Each file is still replaced whole.
- If an interrupted setup on Optimization is continued, the changes found after the interruption are a separate Activity entry beside the setup's own.

## 0.7.0 (2026-11-07; Stable)

- **Advanced settings that earlier research held back for risk, now there as individual choices (in no profile).** Two new collections: "Security protections off (advanced)" (memory integrity, virtualization-based security, Spectre and Meltdown mitigations, "don't start the hypervisor", SmartScreen, the download mark) and "Less in the background (advanced)" (Store apps in the background, app diagnostics, location, automatic Store updates, driver search on Windows Update, OneDrive sync, the telemetry service, printing, the menu delay). Each states its cost; the figures quoted (roughly 0 to 8 percent for VBS and memory integrity) are third-party tests, not Mesura measurements, and no gain is claimed for the others. The 14 generic network-adapter settings and their collection are released too. Not added: Smart App Control (irreversible), DEP/ASLR/CFG/SEHOP/ELAM, the Wi-Fi/Bluetooth/VPN/restore-point service groups (D052). New: the boot option `hypervisorlaunchtype` in the allowlist.
- **Settings that are not here are told in three true ways, and the five network presets are one group.** A setting that cannot be applied reads "Not for this PC" (no Ethernet port, a Wi-Fi card from another maker, an edition, a battery), "Not supported" (the part is there and its driver or hardware says no) or an error, never in the words of another; the short reason is on the card, the engine's sentence is under Technical in the details. A missing Ethernet port, an adapter of another maker, a driver that offers none of the settings and a graphics driver that says no are now four different reasons (they all used to read "the component isn't on this PC"), in both languages. The five per-maker network presets are one group in All settings, "Network adapter presets, by maker · Fit this PC: 1 of 5", each with its own card, status and reason. Nothing they set, their conditions, their apply or undo changed. `docs/unavailable-settings-0.7.md`.
- **Small fixes from the visual and keyboard audit.** On Russian at the minimum width the dock showed "Не выб…" beside "Применить выбранное": the button now reads "Применить" (as on Optimization), the summary is whole. The New menu on Backups could not be reached from the keyboard: it now takes the focus when opened, Up/Down move between its two entries, Esc closes it and the focus returns to New. On the Backups tabs the arrow keys now open the tab they move to. Contrast of the text colours was computed (lowest 5.7:1 on the brightest surface, 4.5:1 is the limit); hover and focus rings were looked at in the real window.
- **Discord and Vencord, more checks in Windows Sandbox** (real Discord and Vencord, no account): after a (simulated) Discord update, a newer folder with Discord's own archive and no loader, Vencord reads "not installed", Discord still "installed", installing Vencord again patches the new folder and a second install says it is already there; and in the real window, cancelling the Restore confirmation writes nothing, Restore writes the saved values and Undo puts both files back byte for byte with no temporary file left. The real sign-in and Discord's own updater stay manual checks.
- **Mesura starts as administrator by default.** Opening Mesura asks Windows for administrator rights straight away (one Windows prompt, no choice dialog in front of it); declining keeps Mesura running without them, says so, and does not ask again. A choice made on purpose is kept ("never ask", or "ask each time", saved in Settings or by "Remember my choice"); the older switch turned off still means never. The decision is one tested function (`StartupPolicy`): an already elevated process, the elevation restart's own copy, `--no-elevate`, and the tray at sign-in never ask, so there is no loop and no prompt at logon. A copy in a folder any program can change (per-user install, build folder) still never elevates, by the security rule of 2026-10-30, and says so once. The command line, the installer and the shortcuts are unchanged. `docs/admin-start-0.7.md` has the design and the manual check for a real PC.
- **Backups: Profiles and Configurations are one section** (More menu: "Backups" / "Резервные копии"). Two tabs, "PC setup" / "Копия ПК" (what the Profiles page was: apps, their settings, Windows settings, the NVIDIA choice; always a plan and a confirmation first) and "App settings" / "Настройки приложений" (what Configurations was: the saved settings file, the per-app cards, Mesura's defaults, Undo, the cursor bonus). One **Open a backup…** reads the file's own kind and lands on the right tab (a file that is neither says so and changes nothing), and **New** offers "This PC's setup" and "App settings only". Nothing of how either tab applies, previews or undoes changed, and no file format, stored value or CLI verb changed: 0.6 files open as before. On screen the word "profile" is now only for NVIDIA, memory and Windows user profiles; a saved PC setup is a "PC setup" / "копия ПК" (its files stay `*.profile.json`). `docs/profiles-configurations-research.md`.
- **A little more room between the logo-M and "esura"** in the Welcome title.
- **A bonus: Windows 11 light cursors** (Configurations, Bonus; `mesura bonus cursors [status|apply|restore] [--apply]`). A free pack by Jepri Creations carried inside Mesura, offered on its own and part of no profile. Apply copies 17 files into Mesura's folder, points your cursor roles at them, reads every value back and saves the cursors you had; one click puts them back exactly, and a role you changed in the meantime is left alone. Per user only; nothing machine-wide. 24 checks on a real registry in Windows Sandbox. The pack came without a licence file: THIRD-PARTY-NOTICES says so.
- **The background is always on.** The Settings switch is gone; the picture still goes still when Windows animations are off, in high contrast, and is reduced in a remote session or without graphics acceleration. The mint haze in the lower corner (mint means "verified") is now the silver-blue of the key light.
- **No ghost contour on buttons.** A click used to leave an accent halo or rim on a button (templates lit it from keyboard focus, which a click also sets); the keyboard ring now shows only when you navigate with the keyboard. A test pins it.
- **Fewer words.** The start page has the mark in its title and no ticked promises; System no longer shows power, uptime and type (the saved and copied report still has them); the 7-Zip note is gone from Configurations; About no longer says "no telemetry, no account" (its list of Mesura's own requests stays); the navigation says BIOS, not firmware.
- **"Apply".** The Optimization button is "Apply" (the window that shows everything first is unchanged), which also leaves room for the line beside it.
- **Optimization groups what is not for this PC by reason** ("Windows Home does not have this (3)") instead of one line per setting. A failed run is never in that list.
- **Apps says why it cannot check an app.** Every "Can't check" has a reason (winget missing, winget not started for that app, winget's error, an answer without the app, no check exists) as a tooltip, and the session log keeps winget's exit code or the launcher's words. On the development PC three runs of all 21 apps gave no "Can't check"; the elevated per-user case, the likeliest cause, is not reproduced here.
- **Updates.** "Nothing published yet" no longer carries an HTTP code or a retry hint. Settings has a "What's new" card with short bilingual notes carried in the build (`whats-new.json`), so it works offline.
- **The mark is the M of "Welcome to Mesura"** (the logo itself stands in the line as the letter, on the baseline, as tall as the capitals; the title is still read out in full).
- **No band above the dock.** Where a page runs into the dock it used to fade into a dark rectangle that did not match the lit field under the dock, which showed as a layer. The page now fades out with an opacity mask onto the one field behind everything, so the dock floats over a single background.
- **Autoruns has its own icon, and AnyDesk joins the Apps list** (Utility, winget `AnyDesk.AnyDesk`, requested by the owner). Both icons come from the vendors' own executables, checked against the SHA-256 of the winget manifests and never run. RustDesk is still not added.
- **Undo of an app-settings import is all or nothing too, and refuses a damaged backup.** If the second file cannot be put back, the first returns to the state it had before the undo; a backup the file format cannot read (cut short, emptied) is never put over a working file, and nothing is restored then. The Vencord detector has tests for every layout (not installed, stub, unpatched, patched with a file or folder loader, a Discord update that adds a newer unpatched folder, versions compared as numbers).
- **"Not for this PC" and "Not supported" are told apart on All settings.** A component, edition, battery or kind of PC that is not here reads "Not for this PC" (as on Optimization and in profile steps); "Not supported" stays for a card or driver that says no. A real failure is still "Error".
- **`mesura apps list` says why an app can't be checked**, and `tools\tester\Check-ElevatedAppDetection.ps1` compares a plain and an elevated run of it (one UAC prompt, read only) for the owner's real-PC check.
- **Discord, Vencord and Autoruns checked end to end in Windows Sandbox** (winget brought in from Microsoft's own packages; no account, Discord never signed in; `docs/sandbox-results/discord-vencord/report.txt`: 23 PASS, 0 FAIL, nothing found to fix). Discord 1.0.9267 is not a stub: `app-*` exists right after the winget install and Discord starts by itself; Vencord installs and the detector reads it from the `app-*` folder; an import while Discord runs is skipped, into a missing file is reported as waiting and creates nothing, into a damaged file is reported and leaves it byte for byte; a real restore brought back a changed value in both files (read from the files), kept a key the bundle does not carry, made the backups and left no temporary file; the second import wrote nothing; a Discord without its `app-*` folder reads Vencord as not installed; installing again says "already installed". Autoruns: installs, reads installed only after, installs again as "already installed". This is Sandbox, not a real PC; not covered: signed-in state, a Discord update that removes the patch, undo of an import from the app.
- **Wording pass, EN and RU.** Checked mechanically (every key in both languages with the same placeholders, endings, quotes, repeated words, untranslated strings: no finding) and by reading Settings, Navigation, Apps, Optimization and Configurations side by side, and every page at the minimum window size in both languages (nothing clipped or overlapping). Fixed: the start-up hint said "ask which ... or always do" (now "ask you, never restart as administrator, or always restart as administrator"), the privacy line pointed to "Settings" while on it (now "under Updates above"), "Windows отклонила" (now "отклонил"), the bonus state "Changed since" (now "Changed afterwards").
- **Per-user apps are checked directly.** Asking winget whether an app is installed no longer goes through the launch as the signed-in user that installing a per-user app needs (shell token, Secondary Logon): the same user, elevated or not, sees the same installs. Before, a failure of that launch made Discord, Spotify and Vencord all read "Can't check" while Mesura ran as administrator. Installing and upgrading are unchanged. Not reproduced on a real elevated PC.
- **App settings are imported all or nothing.** If the second file of an app (Steam has two) cannot be replaced, the first is put back from its backup, nothing stays recorded as written, and a half-written temporary file is not left beside the app's file.
- **Default browser.** Apps shows, when an installed browser of the list (Brave, Chrome) is not the one that opens web links, "Web links open in Microsoft Edge" and a "Make Brave the default" button. Windows protects that choice from programs, so the button opens its page of Settings for the app (`ms-settings:defaultapps?registeredAppUser=…`), and Mesura reads the choice again when the window is activated and says so once when it has changed. Mesura never writes the choice and never forges its hash.
- **The M in the Welcome title** is the first letter of the word Mesura in the mark's own colours (no separate symbol).
- **Autoruns** (Microsoft Sysinternals, portable) joins the Apps list under Utility.
- **Tools.** `tools\shoot-app.ps1` can shoot the start page and no longer trips on the Russian "Ещё" label; the Sandbox scenario `bonus-cursors`.

## 0.6.0 (Stable; built locally, not published; docs/profiles-0.6.md, docs/release-0.6.md)

- **Profiles know the PC.** Mesura reads the hardware (kind of PC, processor, memory, graphics, drives, network adapters) and plans each level for it: per group (Telemetry, Privacy and promotions, Windows, Power management,
  Processor, Memory, Graphics, Storage, Network, Gaming and latency) what would be applied, what is already set, what waits for a restart, what the level's own power plan already covers, and what is not for this PC,
  with the reason. Each group has a switch and every setting can be unticked. Nothing the profile cannot apply is shown as a switch.
- **Telemetry is off in every level**, as its own group apart from privacy and promotions: required diagnostic data only, the improvement, application-telemetry and inventory programs off, no extra logs or large dumps.
- **Maximum performance applies everything that applies to the PC**: the power plan, processor (cores unparked, energy hint, ramp-up, performance cores, power throttling), memory, hardware-accelerated GPU scheduling,
  NTFS, drive and link power states, Edge, widgets, window animations, Game Bar recording and mouse acceleration. More heat, noise and idle power are accepted; the cost of each entry is shown beside it. What stays
  unticked is held back only because its stored value is known from community sources.
- **Laptops are not limited.** The processor, drive and PCI Express entries that write only the mains side of the power plan apply on a laptop (the battery side is never touched). Power throttling is off in Maximum performance on a laptop too;
  that shortens the time on battery, and the entry says so.
- **`mesura hardware [--json]`** shows what Mesura knows about the PC, and **`mesura windows plan [--profile <level>]`** shows a level planned for it; both read-only. **`mesura windows apply --profile <level> [--group-off a,b]
  [--entry-off x,y] [--entry-on x,y]`** applies exactly what the plan lists (previewing unless `--apply`), the same operations the window applies. `--preset` still applies a preset's whole list whatever the hardware.
- **GPU and network entries in the profiles (the owner's decisions of 2026-11-04, `docs/profile-composition-0.6.0.md`).** Maximum performance turns on the original Radeon Anti-Lag (level 0) on a Radeon that offers it (Anti-Lag+ and Anti-Lag Next are never touched; the driver turns Chill off while it is on, and Mesura puts it back on undo). Hardware-accelerated GPU scheduling is no longer applied by default (independent tests show no gain on average); it is offered unticked. Both profiles apply the per-maker network adapter preset that matches each adapter found (Realtek, Intel or Killer Ethernet; Intel, Killer or Realtek Wi-Fi), only on settings the adapter's driver declares, with preview, recorded before-values, an independent read-back and undo; an adapter or setting that is not supported is shown with the reason. No frame-rate or latency gain is claimed: none was measured. The Anti-Lag write has run a full cycle on one real card; adapter writes have run on no real adapter.
- **A crash fixed in the AMD writer** found on the first real write: Mesura kept `amdadlx64.dll` loaded after terminating ADLX and the second initialisation in the same process crashed it; each session now loads and unloads the DLL, as AMD's own helper does.
- **No MAC address or serial number is read from a network adapter.** Driver properties that hold an address or a serial number (`NetworkAddress` and the like) are left out of every adapter reading, so a snapshot kept on disk and the inventory printed by `mesura network --json` carry none.
- **Accessible names on the Welcome page.** The items of the start page were read out by a screen reader as the type name `Mesura.App.WelcomeDoor`; they carry the door's name now. `tools\check-ui-names.ps1` scans the start page too.
- **The Intel Wi-Fi preset has run on a real adapter** (the development laptop, Intel AX201): applied, read back by Windows' own cmdlets, undone, read back, the adapters equal to the snapshot taken before; four A/B cycles of latency to the router showed no measurable difference on Wi-Fi (`docs/real-pc-results/network-laptop-2026-11-05`). No other maker's preset has been written on a real adapter. `tools\tester\Test-NetworkPresetEffect.ps1` is the A/B script.
- **A kit to test the network presets on a real adapter** (`tools\tester\make-network-kit.ps1`, `Test-NetworkPreset.ps1`): preview, a supervised apply and undo of one preset with the values read back by Windows' own cmdlets. It exists because no network adapter has been written on a real PC; the owner runs it.
- **The owner's check kit** (`Collect Mesura stable check.cmd`) now also records `mesura hardware` and both levels' plans, read only.

## 0.5.5 (the first Stable; reliability, performance and hardware work, docs/reliability-0.5.md, docs/release-0.5.5.md)

- **NVIDIA setup steps.** The profile backup before a driver install, the check after it and the import of the NVIDIA profile are reliable on a PC whose NVIDIA Profile Inspector sits in a per-user winget folder (below). The
  driver itself stays the guided step: NVCleanstall opens with your configuration and you click Install there; the automated driver step is built and tested but is held for the next update. Two NVIDIA profiles exist:
  the main one (unchanged) and an extra gaming profile.
- **NVIDIA Profile Inspector just works, wherever winget put it.** Reading profiles (the backup) uses your own rights; writing them runs a copy of the tool that Mesura has verified against the project's published
  release in a folder only administrators can change. A build nobody listed is checked against its GitHub release automatically; an old copy is updated by the setup. Nothing has to be moved or reinstalled.
- **Installer.** Opening the installer for the very version that is already installed (it cannot make a second copy) now says so and says how to switch between "for me" and "for all users".
- **Quieter messages.** The note that explains why administrator mode is not offered from a per-user copy no longer ends with an English technical reason in the Russian interface.
- **Two Mesura power plans.** "Balanced Mesura" is Windows' Balanced without its brakes on mains power (boost policy 100, USB selective suspend off, USB 3 link power saving at minimum, drives off after 20 minutes
  instead of 30 seconds, and without a battery PCI Express link savings at moderate; nothing is pinned). "Maximum Performance Mesura" asks for speed: minimum processor state 100, energy preference 0, boost 100, no throttle
  states, shallow idle states, PCI Express and USB link power saving off, drives never switched off, with the efficiency-core values only on a hybrid processor. Each is a copy of Windows' Balanced plan under its own GUID,
  written on mains power only: the battery side stays Balanced's, so a laptop away from its charger behaves as before. A value is written only where this PC registers it. No Windows plan is changed or deleted; undo activates
  the previous plan and deletes the copy. The Recommended profile (Mesura Reference) includes Balanced Mesura and the Maximum performance profile activates Maximum Performance Mesura instead of Windows' High performance; each is also a separate action. Every value and its reason are in docs/power-plan-0.5.md.
- **Every card says how sure it is.** "Applied" only where the state was read back from the part of Windows that owns it (driver, power plan, service manager, TCP stack, package list) or confirmed by an independent check; "Set" where only the stored registry value was read back; "Restart needed", "Sign-out needed" or "Explorer restart needed" for a set value that waits for that (remembered across sessions until Windows restarts); "Not supported" and "Error" instead of "Not for this PC" and "Failed". GPU scheduling, NTFS last access, the accessibility hotkeys and pointer acceleration carry an independent reading (the graphics kernel, fsutil, and what the signed-in session is really using); a card that cannot use GPU scheduling reads "Not supported". The details name how each setting is confirmed.
- **Graphics.** Every adapter is listed with its maker; an AMD card or the graphics inside a Ryzen is named with its driver and pointed at AMD's own software. A card NVIDIA has moved to security-update drivers is told so.
  The claim that DLSS Frame Generation needs GPU scheduling is gone.
- **Installer.** The wizard asks who Mesura is for: "For all users of this PC (recommended)" (Program Files; Windows asks for administrator approval) is selected, "Only for me" is the other choice. If a copy of the other kind is
  already installed, the page says so, and Settings > Installed copies removes it after you confirm (never the copy you are running). Updates and the command line are unchanged.
- **Words.** Undo is "Откатить", the Activity page is "Действия", optional is "Необязательные", the baseline is "Эталон Mesura"; cores and threads take their Russian forms; the privacy line no longer says the NVIDIA lookup is Mesura's only own request (it also checks GitHub for updates).

## 0.4.4

- **The background is rethought.** The five moving shapes of 0.4.3 are gone. The picture is now a dark room with a soft light in it, after the calm of the photographer's site that was the reference: a very large, very faint key light that wanders over about forty seconds, a violet
  light in the upper corner that breathes, a faint mint haze low in the other corner, a pale diagonal beam that crosses the window in about a minute, twenty-six motes of dust at three depths that rise slowly and fade in and out, a vignette and a very fine fixed grain
  that hides the steps of a dark gradient. Nothing has an edge and nothing is more than a few percent over the ground.
- **The pointer is nearly out of it.** It shifts the lights by at most about three percent of the window with an inertia of over a second, lifts a light by a few percent when near it, and nudges the nearest motes a few pixels. Nothing follows it and nothing bends; with the pointer still the picture moves
  by itself as much as with it. (Measured between frames: the sweep of the pointer changes the picture about as much as four seconds of the picture's own movement.)
- **Modes.** Full: all of the above (26 motes), 15 frames a second at rest, 24 while the lights settle after the pointer. Reduced is a smaller picture, not a slowed one: the three lights moving a little and breathing slowly, no beam, no dust, no reaction to the pointer, 5 frames a second. Off is the same
  lights standing still (it was an empty window), no loop at all. A window that is not the active one is drawn twice a second. Windows' high-contrast themes still get a plain field.
- Measured on the one laptop (1920 x 1290 window, idle): Off 0 %, Reduced about 0.8 %, Full about 1.6 % of the CPU.

## 0.4.3

Version numbers have no suffix from here on (0.4.3, 0.4.4, ...).

- **Welcome page.** Mesura now opens on a quiet page of its own: a mark, one line about what it is, three promises (every change is shown first, what Mesura changes is recorded so Activity can undo it, no account and no
  telemetry), three doors (Optimization, Apps, All settings) and one line of the rest (Debloat, Firmware, System, Activity). It shows no state of the PC and nothing that changes. The logo goes back to it from anywhere; no dock item
  is selected while it is open. Optimization is still its own page.
- **Living background.** The light that followed the pointer is replaced by five soft shapes that drift on paths and at speeds of their own, push each other apart and flatten where they meet, and give way to the pointer (it
  pushes and deforms them, it does not drag them along). Off draws nothing; Reduced is fainter, slower and drawn six times a second; Full is drawn twelve times a second at rest and at the display rate while the pointer is
  moving them. The shapes follow a resized window. Settings > Background says so.
- **Search.** It looks everywhere (every category and level, the guides too) and shows "Search results". It matches what is on the card: name, description, category and the English name; no longer the internal id, which made a
  letter such as "p" match almost everything. One letter is looked for at the start of a word in the name only; two letters at the start of a word; three or more anywhere. The hint, the caret and the typed text now start at the same
  point (the box showed the hint a few pixels to the right of the caret).
- **Cards.** All settings and Debloat cards in a row are as tall as the tallest of that row, the status, switch and divider sit in the same place, no text is cut, and the number of columns follows the width (one to three).
  The category rail is wider so a long Russian category name no longer breaks inside a word.
- **App cards.** No hover description any more (it was the card's tooltip); the mechanism behind it is removed. Confirmations and dialogs are unchanged.
- **Levels.** The level "Advanced" is gone: its one entry, "Classic right-click menu (Windows 11)", is Optional, with the same risk and the same note ("a community workaround Microsoft doesn't document"), and it is still in no
  preset. The filter is All levels, Recommended, Optional.
- **Tools.** `tools\shoot-scaled.ps1` can also capture the Welcome page.

## 0.4.2

- **Apps: one choice for the whole install.** Install opens one dialog with three cards: **Mesura** (its prepared settings; today Brave and Spotify have them), **Custom** (your saved configuration; off, with the reason, while you have
  none) and **Standard** (install only). Each card says how many of the chosen apps it covers; an app it does not cover is installed as it is, which is not an error. The row "After install, set up with" on the page is gone.
- **Apps: the state is the action.** An installed app with a newer version shows **Update** in its state pill and the pill is the button (the separate Update button is gone); Update all stays in the dock.
- **Apps: less on a card.** No install-folder button (an app installs where its installer or winget decides; folders chosen before are ignored and stay in the settings file) and no Configurations link (Configurations is in the More menu).
- **No blank tooltips.** A tooltip with no text (an app card without a note) used to open as an empty square; none opens now, anywhere in the app.
- **Icons** for Medal (its brand mark on the dark disc of its favicon: the installer link in winget is dead, so its own icon could not be taken from the executable) and Reg Organizer (its executable's own icon, replacing the company mark).
- **Installer.** A welcome page that says what Mesura is, then a page with Install; no licence, feature or folder pages. The install model is unchanged (per user without UAC, or all users through the standard UAC prompt).
- **High contrast** now works: Windows' high-contrast themes recolour Mesura (before, nothing changed; the design tokens are loaded first, `Tokens.xaml`). Verified on the four real themes.
- **Window.** Opens inside the work area; the smallest size is 720 x 420; below 940 DIP the dock takes two rows so the page's action stays visible. Checked at a real 200 % Windows scale.
- **Texts.** The catalog's detail texts no longer carry audit notes (PC-000, "I found"); the twelve longest were shortened.
- **Tools.** `tools\shoot-scaled.ps1` (real display scale), `tools\shoot-contrast.ps1` (real high-contrast colours), `tools\e2e-installer-wizard.ps1` (the wizard, driven for real as a throwaway user), `tools\e2e-user-ops.ps1` (per-user operations, no
  administrator rights), `tools\e2e-apps-install-choice.ps1` (the install dialog, one real install); `tools\e2e-admin-ops.ps1` gained six machine-wide mechanisms.

## Earlier work now part of 0.4.2 (written while it was "Unreleased")

- **Logo:** one letter M with a violet-azure-mint glow along its outline and nothing behind it (no tile), crisp at 16 to 64 px; still the button that goes home.
- **Contrast under the pointer glow** is back above 4.5 : 1 for body and secondary text and for danger colours, and 4.2 : 1 for captions (captions and danger were lifted a little).
- **Apps:** one tile height for every app (a long Russian link no longer makes a card taller), no description text on the tile (it is the tooltip), Vencord named plainly in Configurations.
- **Light behind cards:** the glass of a card now carries a dark scrim, so the pointer light can be as bright as before without lowering the contrast of captions (4.6 : 1 or more in every lighting mode, measured).
- **Texts:** shorter administrator-rights messages, key hint, profile note and dock hints.
- **Apps: update.** An installed app that Mesura updates (7-Zip, Notepad++, OBS Studio, Visual Studio Code, and Git, VLC, Firefox for the command line) shows an **Update** button when winget has a newer version,
  and the dock has **Update all (n)**. The confirmation lists each app with both versions; the result says what really happened, because Mesura asks winget again afterwards whether an update is still offered
  (an installer that exits cleanly and leaves the old version is reported as not updated). `mesura apps updates` and `mesura apps upgrade (--id | --all) [--apply]` do the same from the terminal. The run is in Activity.
  Apps that update themselves (browsers, chat clients, game stores) or install through a stub get no update button on purpose.
- **Apps: uninstall.** Only for the three apps whose uninstall was run for real and checked (Notepad++, VLC, OBS Studio), and only as administrator: an MSI uninstall without it fails (installer code 1603), so
  without administrator rights the button is disabled and says why. After winget reports success Mesura asks whether the app is really gone. Firefox is not offered: winget says "successfully uninstalled" and the
  app stays. `mesura apps uninstall --id <app> [--apply]` does the same (`--apply` refuses to start without administrator rights).
- **Updates that go wrong halfway.** If the installer's client is killed or crashes while Windows Installer goes on with the install (seen for real), the updater no longer records a finished update as failed: it waits for the installer's
  own log to end, reads what is installed and records that (the app said "the update didn't finish" while the new version was installed). The updater itself no longer stops silently on an unexpected error (a result is always recorded and
  Mesura is always started again), a second updater (Install pressed twice) does nothing instead of overwriting the first one's result, and after a **machine-wide update Mesura starts again as the signed-in user, unelevated**: it used to inherit the
  updater's administrator rights (and, when another account's password approved the prompt, that account).
- **Window size.** The smallest window is 900 x 500 (was 1020 x 660), so it fits a 1080p screen at 200 % scale; checked down to 800 x 500, below which the dock's main button starts to be cut off.
- **Screen readers.** A new result line (update, install, apply, failure) is a live region: the window raises a UI Automation LiveRegionChanged event with the result as its name, so Narrator announces it without being asked.
- **Tools.** `tools\e2e-update.ps1` also runs machine-wide updates (`-AllUsers`), a download cut once by the server (`resume`) and an installer killed midway (`killed`); `tools\e2e-apps-update.ps1`, `tools\e2e-apps-uninstall.ps1`, `tools\e2e-announce.ps1`, `tools\e2e-admin-ops.ps1`
  (services, hibernation, per-interface NetBIOS, a DISM feature) are real checks that read the result back independently. `tools\publish.ps1` never gives a build the file version of the one it replaces (equal file versions made an upgrade keep the old files).

## 0.4.1-rc1 (release candidate, not published)

Fixes from the first real use of 0.4.0-rc1, and the next round of the redesign.

- **Starts on a Russian Windows 10 again.** 0.4.0-rc1 stopped at start on a PC whose Windows language is Russian: the language files were read before their reading options existed, so the comments in them
  were rejected. The options are created on demand, a language file that cannot be read now falls back to English (and says so in the start-up log), and the error dialogs no longer depend on the
  language component that failed.
- **System Restore.** A restore point that cannot be made ("Provider load failure" on stripped Windows images) is tried a second way (the System Restore API) and, if that fails too, reported in your language
  as a warning of its own, apart from the result of the run, with Windows' own message under Technical details. Nothing says a point was made when it was not.
- **Mesura Reference** replaces the old "Recommended" level and the image-specific follow-up list: the same 18 documented settings in three parts you can also apply one by one (performance basics, privacy basics,
  fewer promotions). Performance and privacy are no longer mixed in one list; Maximum performance is Mesura Reference plus the performance extras.
- **Logo and navigation.** A new, brighter mark that is also the way home; a Back button (and Alt+Left, and the mouse's Back button) that first closes an open details drawer and then returns to the page before;
  cards with a chevron open by a click on the card; Quick select is drawn as a control.
- **Light.** Off is a plain dark field with no glow; Reduced is a thin static light; Full is the whole effect, with a second light that trails the pointer. A Windows high-contrast theme switches the light off and uses
  the system colours.
- **Apps.** Vencord is an app of its own; Medal, Reg Organizer and Visual Studio Code were added; the cards lost their descriptions (they are tooltips) and the folder button is drawn only where the installer takes a folder.
- **Installer.** The wizard has Mesura's pictures and no licence page.
- **For the owner's desktop.** `mesura firmware [--json]` prints what the BIOS & Firmware page reads and concludes, so the page can be checked on a PC where the window cannot be opened from here.
- **Tools.** `tools\sign-release.ps1` and the `-Sign` switches of `publish.ps1` and `build-installer.ps1` Authenticode-sign the executables and installers with a certificate you own; the update signature is unchanged.
## 0.4.0-rc1 (release candidate, not published)

A new look, a shorter and more honest list of settings, a BIOS & Firmware page, and a start-up choice about administrator rights.

- **New interface ("Graphite").** A neutral near-black window with one pale accent (green only means verified or done), a bottom command dock instead of the sidebar (the pages,
  a More menu for Configurations, Profiles and Settings, the page's own actions and, always at the right, the selection and the Apply button), and one interactive light that follows the pointer across
  the whole window instead of every card lighting up alone. New type (Onest, JetBrains Mono, Spectral; Latin and Cyrillic). The light is turned down on weak graphics and in remote sessions, off when Windows'
  animations are off, and Settings > Lighting sets it by hand. "PC setup" is now **Optimization**; the page headers lost their captions; System was rebuilt (headline cards, columns by width, long names wrap);
  Apps cards are one proportion; Activation shows "Windows 11" or "Windows 10", one expressive status word, and a link to Microsoft's own explanation.
- **A shorter catalog (251 -> 148 settings).** Every setting was re-read against what it changes, what backs the claim, what it costs and whether Windows already does it.
  103 were removed (44 only change how Windows looks, 16 have no proven benefit and a visible cost, 15 risk more than they give, 13 duplicate another, 9 have an undocumented effect, 4 are covered by new features,
  2 are already Windows' default). The rest are in eight groups: real performance, gaming, power, components, privacy, maintenance, convenience, experimental. Hardware and edition requirements
  (battery, Modern Standby, Pro or higher, 16 GB of memory, hybrid processor) are checked before an entry is offered. **Undo still works for what an earlier version applied**, including settings 0.4 no longer ships.
- **Recommended and Maximum performance rebuilt.** Recommended is 18 settings that remove advertising, profiling and nagging, with no power, service, security or driver change and no frame-rate promise. Maximum
  performance adds nine documented background and desktop power settings (the High performance plan and PCIe link power saving only on a desktop) and lists eight optional ones unticked, with the trade-offs on the card.
- **BIOS & Firmware.** Reads the board, BIOS, boot mode, Secure Boot, TPM, virtualization, memory (speed against what the module is rated for, channels), Resizable BAR and the Intel microcode, and
  says which few settings are worth a look on this PC, why, how risky, and the menu steps for the board's maker in English and Russian. It never flashes, writes firmware variables or changes a BIOS setting; its one action is
  Windows' own "restart to firmware" after a confirmation, and after the restart it shows what changed.
- **Apps.** Telegram (Telegram's own installer) and AyuGram (the AyuGram project's portable client; detected even when installed outside winget) were added; Vencord is shown under Discord, with its own state, instead of as a separate app. An install folder can be chosen for the apps whose installers take one (Steam, OBS Studio, 7-Zip, Notepad++, Telegram, AyuGram); for the others the button says the installer decides.
- **Administrator rights.** When Mesura is installed for all users it asks at start whether to continue normally or restart as administrator, and can remember the answer (Settings). A per-user install still never offers
  administrator mode, and there is no switch to override that.
- **Fonts and notices.** The three typefaces and their licences are in `THIRD-PARTY-NOTICES.md`; the Telegram and AyuGram marks are listed with the other app marks.

## 0.3.0-rc2 (release candidate, not published)

Technical reliability: what runs with administrator rights, and what happens when an install or update fails. No new features, no design changes.

- **Administrator mode is not offered from a folder any program can change.** A per-user install lives in your profile, where any program you run can replace `Mesura.exe`; run elevated,
  such a program would get administrator rights the next time you approved the prompt. Mesura now checks the folder's access list: from Program Files (an all-users install) nothing changes;
  from your own folder it starts without the administrator prompt, and there is no switch to override that. Every place that used to say "restart as administrator" says instead why this install has no administrator rights and how to get
  full access (install for all users): the sidebar chip opens an explanation, Settings shows the reason, and the Setup, Tweaks, Activation and NVIDIA messages and the "Needs administrator" tags are worded for it (`docs/security.md`). Functions that don't need administrator rights are unaffected.
- **Undo can only undo what the operation changes.** The history record Undo reads is a file in your data folder; an elevated Undo now restores only the registry values, services and tasks the
  operation declares in the built-in catalog, and refuses (changing nothing) a record that names anything else. As administrator, Mesura also uses the built-in catalog instead of an edited copy.
- **NVIDIA tools.** NVIDIA Profile Inspector and NVCleanstall are not started with administrator rights from a folder that programs running as you can change; the message says to install them for all users.
- **Machine-wide updates.** The elevated step of an all-users update is the installed command line in Program Files (not a copy in your profile): it verifies the signature, copies the package into a folder
  only administrators can write while hashing it, and runs the installer from there. The version, folder and language come from the installer's registration, not from arguments.
  An expired manifest is refused at apply time as well.
- **Failed update, then uninstall.** After an update that failed and was rolled back, uninstalling used to leave the program folder, the registration and the Start menu entry behind (Windows Installer kept
  the product half-registered). The installer now installs the new files first and removes the old product after (`afterInstallExecute`), the file version rises with every build, and the check
  `toolse2e-installer.ps1` proves install, reinstall, uninstall, upgrade, a failed update and a failed first install leave nothing behind.
- **Language of updates.** The installer records its language; an update downloads the installer in the same language (the manifest lists both), and an English package installed over a Russian one now replaces it
  instead of installing beside it. The language you chose inside Mesura was never affected.
- **Signing key.** `mesura-release keycheck` proves a key file is intact and is the one the app trusts without printing it; `docs/signing-key.md` covers backup, restore and rotation.
- **Tools.** `toolse2e-installer.ps1`, `toolserify-published.ps1` (checks a published Preview/Stable release the way the app would), `toolse2e-update.ps1 -RealSource` (the update flow against the real source once a Preview release exists).


## 0.3.0 (release candidate, not published)

A new interface, an installer with signed automatic updates, and a System page.

- **New interface ("Glass").** One shell for every page: a sidebar with the pages in three groups (Tune, Software, Insight) over soft ambient light, and one page frame
  (eyebrow, title, actions, content on the same grid everywhere). Panels are glass: translucent washes with a lit rim, so the light behind them shows through. A glow follows the
  pointer over the surfaces you can act on and stays off the rest. New palette (ink-blue field, indigo-violet accent, quiet status colours), contrast checked (body text 8:1 or more, smallest
  text 5:1), the green dot is gone, the bottom dock and the separate action bar are replaced by the sidebar and one floating action bar. The sidebar folds to icons in a narrow window.
  See `docs/redesign-2026-11.md`.
- **System page.** The computer and Windows at a glance: processor, memory (in use), graphics and driver, drives and free space, displays, power, firmware and boot mode, Windows build.
  Read-only, no serial numbers or addresses; what cannot be read says "Unavailable". Copy or save the report as text.
- **Installer.** A Windows Installer package (English and Russian wizard): choose the folder (spaces and Cyrillic are fine), optional desktop shortcut, per-user by default without
  administrator rights, all users with `ALLUSERS=1`. Uninstall leaves your settings, history and profiles. `docs/installing.md`.
- **Updates.** Mesura checks when it starts (off in Settings), shows a card when something is newer, and installs on your click: download with progress, verification against a
  signed manifest (ECDSA P-256) and the SHA-256, then an updater closes the app, runs the installer (transactional: a failure restores the previous version) and starts Mesura again.
  Stable and Preview channels; skip a version or be reminded tomorrow. `docs/updates.md`. `mesura update check|status`.
- **Migration.** An unzipped copy can be upgraded in place by the installer; the start-up entry and Start menu shortcut it had are pointed at the installed copy.
- **Data folder.** `MESURA_DATA_DIR` moves everything that changes (settings, history, logs, staged updates).
- **Tests.** New tests (updates: policy, signatures, downloads with resume and corruption, the updater against a fake installer; the System report).
  `tools\shoot-app.ps1`, `tools\update-test-server.ps1`, `tools\make-release.ps1`, `tools\build-installer.ps1`.

## 0.2.0 (release candidate, not published)

A larger catalog with sources and a second review, a Debloat page, a redesigned window, and a fix for the start-up failure some testers saw.

- **Start-up fix.** The single-file build unpacked its native libraries into the user's TEMP at start; where TEMP was unusable the program ended
  before Mesura's own code ran and left nothing but a Windows event. The natives now ship beside `Mesura.exe`. Mesura writes
  `%LOCALAPPDATA%\Mesura\logs\mesura-startup.log` from its first line, says in a window which files are missing, and the release folder carries
  `Diagnose Mesura.cmd`, which collects what a tester needs to send (it reads, and changes nothing). See `docs/compat-matrix.md`.
- **Catalog.** About 250 Windows settings (from 99). Added from Microsoft's own references: telemetry and diagnostics policies, Edge policies,
  sync/search/suggestion policies, network discovery protocols, per-adapter settings, AC power and scheduler settings, memory and storage, shell and
  update behaviour. Each carries its sources and what is known about its benefit. Recommended is 23 settings (was 14); Maximum performance is 44
  with 7 unticked extras (was 24 and 7); seven new collections. Settings that could not be substantiated are not offered:
  `docs/catalog-research-2026-10.md` and its 295-row list of rejected ideas say what and why.
- **Debloat.** Bundled apps (36 entries, 48 packages, removed for your account; Windows' own packages can never be listed; undo registers the app
  again from Windows' staged copy), AI features (Recall, Click to Do, Paint AI, Copilot) and startup apps (turned off with Windows' own flag).
- **Engine.** Binary registry values, settings applied to every matching network adapter, boot-entry, hibernation and memory-agent settings
  through Windows tools, packaged-app removal, a stated risk level and sources per entry (catalog schema 4).
- **Restore point.** Before a run changes settings for the whole PC (as administrator), Mesura makes a Windows restore point; a setting turns it off.
- **Command line.** `mesura windows restore-points` and `windows restore-point`; `windows apply --apply` makes a restore point first (not with
  `--no-restore-point`) when it changes the whole PC as administrator.
- **Testing.** `Test-MesuraOperations.ps1` (for a spare PC or VM; dry run by default, every result read back with Windows' own tools),
  `tools\compat-matrix.ps1` (start-up under hostile TEMP, read-only folders, odd paths) and `tools\e2e-gui.ps1` (the window's apply, check and undo flow
  with real clicks); `docs/for-testers.md` in English and Russian.
- **Window.** A top strip and a floating dock instead of the left rail; every page opens with a title, subtitle and actions; the Tweaks page is a
  grid of cards with a coloured risk label, a status and a switch, with a details drawer.

Known limits: not signed; the removal, boot-entry and restore-point paths were exercised against an in-memory Windows and read-only on one
PC, not applied on a stock Windows; the researched entries had a second, sceptical review (`docs/catalog-verification-2026-10.md`), which is a
review of documents and one PC's configuration, not a test on other PCs.

## 0.1.0 (first public release, not yet published)

First release. What it contains:

- **Setup** with two profiles. *Recommended* (14 settings): less promotion, ad profiling and nagging, nothing you would
  miss. *Maximum performance* (24 settings and 7 unticked extras): the same plus fewer optional background functions and
  the desktop power settings, with the cost of each named. Every setting is documented by Microsoft or was checked
  read-only on a Windows build; community-only settings are never in a profile.
- **All settings**: about a hundred Windows settings with what each writes, its risk and what is known about it. Machine
  policies apply on Pro and higher only; Windows Home shows them as not applicable.
- **Telemetry**: the official levers only in the profiles (Required diagnostic data, advertising ID, tailored
  experiences, feedback prompts, activity history, error reporting, update peer sharing); the telemetry service and the
  data-collection tasks, which Microsoft does not document as a control, are unticked extras.
- **Apps** (winget), **Configurations** (allowlisted app settings), **Profiles** (save a PC, run it on another),
  **Activation** (read the licence state; install and activate a key you own), **Activity** (history with exact undo).
- **NVIDIA**: driver lookup, trimmed install with NVCleanstall, your profiles backed up and put back, and Mesura's own
  driver profile (three documented settings: G-SYNC for full screen and windowed, and maximum-performance power mode on desktops only).
- English and Russian interface; a command line that previews by default.

Known limits: not signed; checked on one Windows 11 25H2 Pro PC with read-only previews; never applied to a clean PC,
Windows Home or Windows 10 by the author; the NVIDIA step has never run on a GeForce card. See the README.
