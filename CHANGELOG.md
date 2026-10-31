# Changelog

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
