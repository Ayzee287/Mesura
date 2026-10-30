# Installing, updating and removing Mesura (0.3)

## Two ways to have Mesura

| | Installer (`Mesura-<version>-x64.msi`) | Portable (`Mesura-<version>-win-x64.zip`) |
|---|---|---|
| Install | wizard: welcome, licence, folder and shortcuts, install | unzip the **whole** folder anywhere and run `Mesura.exe` |
| Registered with Windows | Apps > Installed apps; Start menu entry; optional desktop shortcut | nothing |
| Updates | in the app (Settings > Updates), or by running a newer installer | the app downloads the verified installer and opens it |
| Needs administrator rights | no (default: current user) | no |

Both are self-contained: no .NET or other runtime is needed on the PC. Windows 10 22H2 (build 19045) or later and Windows 11, x64.

## The installer

Download `Mesura-<version>-x64.msi` (English wizard) or `Mesura-<version>-x64-ru.msi` (Russian wizard) and compare its SHA-256 with `SHA256SUMS.txt` of the same release. The installers are
not Authenticode-signed (no certificate yet), so Windows may show a SmartScreen warning: *More info > Run anyway*.

- **Where it installs.** Default: for the current user in `%LOCALAPPDATA%\Programs\Mesura`, no administrator rights, no UAC prompt. The wizard lets you choose the folder
  (spaces and Cyrillic are fine). For every user in `Program Files\Mesura`: run `msiexec /i Mesura-<version>-x64.msi ALLUSERS=1` from an administrator prompt.
  Mesura asks for administrator rights each time it starts (it changes Windows settings), so an all-users install is the hardened choice: a program in
  `Program Files` cannot be replaced by a process that is not elevated. **A per-user install does not ask for administrator rights at start**, because any program you run could replace
  `Mesura.exe` in your profile and be elevated in its place (`docs/security.md`); there is no setting to override this, and the window says so and how to get full access (install for all users). The per-user default matches where Mesura's Settings already expected an installed copy.
- **What it installs.** `Mesura.exe` and its five native libraries, `cli\mesura.exe` (the command line, also the updater), the licence, third-party notices, README and changelog. A Start menu shortcut,
  and a desktop shortcut if ticked. Registration in `HKCU\Software\Mesura` (or `HKLM` for all users): the install folder and version, which the app and the updater read.
- **What it does not touch.** `%LOCALAPPDATA%\Mesura` (settings, history and backups, profiles, edited catalogs, logs, staged updates) is not part of the package: install, upgrade and
  uninstall leave it as it is. Uninstalling removes the program, the shortcuts and the registration only.
- **Silent install:** `msiexec /i Mesura-<version>-x64.msi /qn INSTALLFOLDER="D:\Apps\Mesura" [ALLUSERS=1]`. Add `/l*v install.log` for a log.
- **Repair / remove:** Apps > Installed apps > Mesura, or `msiexec /x Mesura-<version>-x64.msi`.
- **Installing over an older version** (or the same one) upgrades in place and keeps the folder you chose before. The new files are installed first and the old product is removed after
  (`afterInstallExecute`), so a failure leaves the old version in place. The language of the package does not matter (an English installer replaces a Russian one in place; it never installs beside it).
  A package whose first three version numbers are older than the installed ones is refused; the fourth (the release candidate number) is ignored by Windows Installer, so going from rc2 back to rc1 is
  not refused by the installer (the updater never offers it).
- **A failed or cancelled install or update** restores the previous version (or leaves nothing, for a first install). Since rc2 an uninstall after such a failure removes the program folder, the registration and
  the Start menu entry as it should; before rc2 it left them behind. `tools\e2e-installer.ps1` checks this on real packages. Your settings, history and profiles are never part of the package and are never touched.
- **Files Mesura did not install** (a log or scratch file someone put in the program folder) are not removed by the uninstall, as for any Windows Installer product: the folder then stays with only those files in it.

## From a manually unzipped Mesura

Run the installer. If the unzipped copy was in `%LOCALAPPDATA%\Programs\Mesura` it is upgraded in place; if it was somewhere else, the old folder stays and can be deleted by hand. Nothing else is needed:

- Settings, history, profiles and logs are in `%LOCALAPPDATA%\Mesura` and are shared by every copy, so they carry over.
- If the old copy had a **Start menu shortcut** or a **start-in-the-tray entry**, the installed Mesura points them at itself on its first start (it never creates one you did not have).

## Updating

See `docs/updates.md`. In short: Mesura checks when it starts, shows a card in the sidebar, downloads and verifies on your click, then closes, runs the installer and opens again. If the
installer cannot finish, Windows Installer restores the previous version.

## Where things are

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Mesura` (per user) or `%ProgramFiles%\Mesura` (all users) |
| Settings, history, backups | `%LOCALAPPDATA%\Mesura\Setup\` (`app-settings.json`, `history\`) |
| Edited catalogs, profiles you saved | `%LOCALAPPDATA%\Mesura\`, and the folders you chose for profiles |
| Start-up log | `%LOCALAPPDATA%\Mesura\logs\mesura-startup.log` |
| Staged update, its result and the installer's log | `%LOCALAPPDATA%\Mesura\updates\` |
| Move all of the above | environment variable `MESURA_DATA_DIR` (for a portable copy that keeps its data beside it, or for tests) |

## When something goes wrong

- **The installer says it cannot write to the folder:** choose another folder, or install for the current user.
- **"Another installation is in progress":** wait a minute; Windows Update or another installer is running.
- **Mesura does not start after installing:** run `Diagnose Mesura.cmd` from the portable zip (read-only; it writes one text file to the Desktop), and attach that file when you report the problem.
- **A path with spaces, Cyrillic or a long name:** all supported by the installer, the updater and the app (checked).
- **The update does not appear:** Settings > Updates > Check now says why (no internet, nothing published yet, verification failed).

## Known limits

- Not Authenticode-signed: SmartScreen / Smart App Control may warn or block the first run. The update signature protects updates, not the first download.
- Installing for all users and updating an all-users install need administrator rights (one UAC prompt); this path was built, its package structure checked and the elevated steps unit-tested, but a real
  all-users install and update has not been run on a real PC yet (no administrator session was available); it needs a test PC or a virtual machine.
- Cancelling the wizard or an install half-way was not clicked through (it is a rollback, like the failure scenarios that were run).
- Windows 10 and Windows Home were not available to test on.
