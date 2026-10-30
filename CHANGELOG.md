# Changelog

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
