# Installing, updating and removing Mesura (0.3)

## Two ways to have Mesura

| | Installer (`Mesura-<version>-x64.msi`) | Portable (`Mesura-<version>-win-x64.zip`) |
|---|---|---|
| Install | wizard: a welcome page, who it is for (only you, or every user), then Install (no licence, folder or feature pages) | unzip the **whole** folder anywhere and run `Mesura.exe` |
| Registered with Windows | Apps > Installed apps; Start menu entry; optional desktop shortcut | nothing |
| Updates | in the app (Settings > Updates), or by running a newer installer | the app downloads the verified installer and opens it |
| Needs administrator rights | for "For all users of this PC", the recommended choice (Windows asks) | no |

Both are self-contained: no .NET or other runtime is needed on the PC. Windows 10 22H2 (build 19045) or later and Windows 11, x64.

## The installer

Download `Mesura-<version>-x64.msi` (English wizard) or `Mesura-<version>-x64-ru.msi` (Russian wizard) and compare its SHA-256 with `SHA256SUMS.txt` of the same release. The installers are
not Authenticode-signed (no certificate yet), so Windows may show a SmartScreen warning: *More info > Run anyway*.

- **Where it installs.** The wizard's second page asks who Mesura is for (0.5).
  **For all users of this PC (recommended, selected by default)**: `Program Files\Mesura`; Windows asks you to approve the installation as administrator when it starts (the ordinary Windows Installer prompt, nothing avoids it). Only this copy can ask for administrator rights each time it starts, and a program in `Program Files` cannot be replaced by something you ran.
  **Only for me**: the current user, `%LOCALAPPDATA%\Programs\Mesura`, no administrator rights, no UAC prompt.
  There is no folder to choose; a silent install can still name one (below; spaces and Cyrillic are fine) and makes the same choice with `ALLUSERS=1` (every user) or `ALLUSERS=2 MSIINSTALLPERUSER=1` (this user), which is also what the updater passes. A silent install with no properties stays per-user (there is nobody to approve a prompt); `MESURA_SCOPE=user` makes the wizard's page start on "Only for me".
  Mesura asks for administrator rights each time it starts (it changes Windows settings), so an all-users install is the hardened choice: a program in
  `Program Files` cannot be replaced by a process that is not elevated. **A per-user install does not ask for administrator rights at start**, because any program you run could replace
  `Mesura.exe` in your profile and be elevated in its place (`docs/security.md`); there is no setting to override this, and the window says so and how to get full access (run the installer again and choose "For all users of this PC"). The per-user default matches where Mesura's Settings already expected an installed copy.
- **What it installs.** `Mesura.exe` and its five native libraries, `cli\mesura.exe` (the command line, also the updater), the licence, third-party notices, README and changelog. A Start menu shortcut,
  and, on request (`ADDLOCAL=MainFeature,DesktopFeature`), a desktop shortcut. Registration in `HKCU\Software\Mesura` (or `HKLM` for all users): the install folder and version, which the app and the updater read.
- **What it does not touch.** `%LOCALAPPDATA%\Mesura` (settings, history and backups, profiles, edited catalogs, logs, staged updates) is not part of the package: install, upgrade and
  uninstall leave it as it is, in either scope. Uninstalling removes the program, the shortcuts and the registration only.
- **Three kinds of files, kept apart (0.5).** The program (`Program Files\Mesura` or the per-user folder): written by the installer, replaced by an update, removed by an uninstall. Each user's settings and data
  (`%LOCALAPPDATA%\Mesura` of that user): written by the app, never by the installer, so installing for all users cannot change or reveal another user's settings, and a second user starts with
  their own empty ones. Shared data: none exists (no `ProgramData` folder is created); the one thing every user of an all-users install shares is the program itself and its machine-wide registration (`HKLM\Software\Mesura`).
- **Silent install:** `msiexec /i Mesura-<version>-x64.msi /qn INSTALLFOLDER="D:\Apps\Mesura" [ALLUSERS=1]`. Add `/l*v install.log` for a log.
- **Repair / remove:** Apps > Installed apps > Mesura, or `msiexec /x Mesura-<version>-x64.msi`.
- **Installing over an older version** (or the same one) upgrades in place and keeps the folder you chose before. The new files are installed first and the old product is removed after
  (`afterInstallExecute`), so a failure leaves the old version in place. The language of the package does not matter (an English installer replaces a Russian one in place; it never installs beside it).
  A package whose first three version numbers are older than the installed ones is refused; the fourth (the release candidate number) is ignored by Windows Installer, so going from rc2 back to rc1 is
  not refused by the installer (the updater never offers it).
- **A failed or cancelled install or update** restores the previous version (or leaves nothing, for a first install). Since rc2 an uninstall after such a failure removes the program folder, the registration and
  the Start menu entry as it should; before rc2 it left them behind. `tools\e2e-installer.ps1` checks this on real packages. Your settings, history and profiles are never part of the package and are never touched.
- **Files Mesura did not install** (a log or scratch file someone put in the program folder) are not removed by the uninstall, as for any Windows Installer product: the folder then stays with only those files in it.

## Two kinds of copy (0.5)

Windows Installer identifies a package by its product code, and every version has its own. Installing a **different version** for the other scope therefore **adds** a second copy (measured in Windows
Sandbox); it does not replace the first. Opening **the very same package** (the same build) again never makes a second copy: Windows Installer sees its product code as installed and the wizard goes to
Change / Repair / Remove (a silent install for the other scope does nothing, exit 0). By design of Windows Installer, not something the package can hide, so the wizard says it (owner's decision, 2026-11-02):
before Change / Repair / Remove a page "Mesura is already installed" names the scope of the installed copy, says this installer cannot add a second copy, and says how to move (Remove, then run it again and
choose the other scope; settings stay). Seen in Windows Sandbox for both scopes (`docs/sandbox-results/installer-wizard-2026-11-02.txt`).

| Installed | You choose | What happens | What stays |
|---|---|---|---|
| Only for me | Only for me, the same build | the "already installed" page, then Change / Repair / Remove; no second copy | everything |
| Only for me | Only for me, a newer version | upgrade in place, folder kept | everything |
| For all users | For all users, the same build | the "already installed" page, then Change / Repair / Remove; no second copy | everything |
| For all users | For all users, a newer version | upgrade in place (needs approval) | everything |
| Only for me | For all users, another version | a second copy in Program Files; both appear in Installed apps; the scope page says so beforehand | the per-user copy until you uninstall it |
| For all users | Only for me, another version | a second copy in your profile; the page says so | the all-users copy |

**Moving to the other kind (0.5).** The copy you run offers to remove the other one: Settings, "Installed copies" shows it, and "Remove the other copy" asks for your confirmation (and, for an all-users copy,
Windows' administrator approval), then runs Windows Installer's own uninstall for that copy's product code (`msiexec /x`) and reads the registrations again. The copy being removed is never the one
you are running; if the uninstall is cancelled or fails, both copies stay; if Windows Installer ever removed this copy's own registration, Mesura says so and asks you to run the installer again. When the
two copies carry the same product code (the same package version installed for both kinds) Mesura does not guess which one `msiexec` would remove and tells you to remove the unused one in Installed apps.
The decisions are covered by tests with fakes; the real removal is not run yet (scenario A4 and A5 in `docs/hardware-test-plan-0.5.md`).

Uninstalling either copy is not expected to touch the other (two products) and the package never touches `%LOCALAPPDATA%\Mesura` (settings, history, profiles), which every copy of a user shares. The notice is set from the registry
(`HKCU\Software\Mesura` for a per-user copy of the current user, `HKLM\Software\Mesura` for an all-users copy), so it does not see another user's per-user copy. What is proven: the package passes
ICE validation and the notice's property logic works in a Windows Installer session in both languages. What is not: the pages were not clicked through on a clean VM, and nothing here has been
run through UAC (`docs/hardware-test-plan-0.5.md`, section A).

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
- Installing for all users and updating an all-users install need administrator rights (one UAC prompt). The wizard's scope page (0.5) was built and the package's tables checked (the pages, the properties it
  writes, the folders for each scope, ICE validation); its pages were not clicked through for the all-users choice, and a real all-users install, update and uninstall has not been run: that needs a virtual machine or a
  test PC (`tools\e2e-installer-allusers.ps1`). Going from a per-user copy to an all-users copy (or back) was not run either; see "Two kinds of copy" below.
- Cancelling the wizard or an install half-way was not clicked through (it is a rollback, like the failure scenarios that were run).
- Windows 10 and Windows Home were not available to test on.
