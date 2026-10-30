# Security: what runs with administrator rights, and why (0.3.0-rc2)

Mesura changes settings for the whole PC, so part of it runs elevated. The question this page answers is the one that matters for a program that elevates:
**can something that is not an administrator get administrator rights by going through Mesura?** It was audited for rc2 by reading every place that starts a process, reads a file
or writes a registry value while elevated, and by reproducing each weakness with a test before fixing it. Nothing here is a claim that the program is "secure"; it is a list of
what was checked, what was changed, and what is still true.

## The principle

A process with administrator rights is only as trustworthy as everything it *executes* and everything it *believes*. A program in the user's profile can be changed by any program the user
runs (a browser download, a game, a script). So elevation is only safe when the elevated code and the data that steers it come from places only an administrator can change, or are
checked against something that is.

## The chain of elevated operations (audited)

| Step | What runs elevated | Where it comes from | Finding in rc1 | rc2 |
|---|---|---|---|---|
| Start the window elevated (the UAC prompt at start, "Restart as administrator", the tray) | `Mesura.exe` and every DLL beside it | the program folder: `%LOCALAPPDATA%\Programs\Mesura` for a per-user install, or any unzipped folder, is **writable by any program the user runs** | **Real.** A program that replaced `Mesura.exe` there would run with administrator rights the next time the user approved the prompt (the UAC prompt names Mesura and the path, not who wrote the file) | The folder's access list is read (`ProgramFolderTrust`): if a non-administrator can change it, administrator mode is **not offered** (the window starts normally; the interface says why and how to get full access (install for all users); there is no switch to override it). Program Files: unchanged |
| An all-users update | the updater that runs `msiexec` | rc1 copied `mesura.exe` into `%LOCALAPPDATA%\Mesura\updates\run-*` and started **that copy** elevated; the package and manifest were also read from that user folder | **Real.** The copy could be replaced between the copy and the start; the package between the check and the install | `update stage`: the elevated process is the **installed** command line in Program Files; it verifies the signature, copies the package into a folder only administrators can write **while hashing what it writes**, copies itself there, and starts that protected copy. Version, install folder, language and restart path come from HKLM, not from arguments. Details below |
| Undo of a run | the registry/service/task writes that put values back | the history record: a JSON file in `%LOCALAPPDATA%\Mesura\Setup\history\`, **writable by the user** | **Real.** A forged record naming `HKLM\...\Run\updater` made an elevated Undo write that value (the `RevertTrustTests` reproduce it) | An elevated Undo restores only targets the operation **declares** in the built-in catalog (`RevertTrust`; since 0.4 also the declarations of operations that release dropped, kept in `windows.retired.json`); a record naming anything else is refused whole, changing nothing |
| Apply from an edited catalog | whatever the catalog names (registry values, services, tasks; winget package ids) | `windows.catalog.json` / `apps.catalog.json` copied for editing into the user's data folder | **Real.** An edited copy could name any registry value or any winget package and the elevated run would write or install it | An elevated Mesura uses the **built-in** catalogs only and says an edited copy was left unused; a normal (non-elevated) session still uses the edited copy |
| NVIDIA Profile Inspector (export/import of driver profiles), NVCleanstall | the tool, started by an elevated Mesura (inherits the token) | a per-user winget package (`%LOCALAPPDATA%\Microsoft\WinGet\Packages`) or the tool's own install folder | **Real**, same class as the first row | Refused when the tool's folder can be changed by a non-administrator; the message says to install it for all users (`winget install --scope machine`) |
| `winget` (installing apps, runtimes) | `winget.exe`, resolved by name through the program's folder, the current folder and the user's PATH (which has a user-writable `WindowsApps` folder) | PATH | **Real** for the cases where Mesura runs winget itself with its own token (when it runs as the signed-in user's de-elevated copy, from the shell's token, the user's own rights apply) | An elevated launch names the file: only a winget in a protected folder, or the genuine App Execution Alias in the user's `WindowsApps` folder (a reparse point), is started; the first winget on the PATH is the one judged, relative PATH entries are skipped (`WingetLocator`) |
| Windows tools (`powercfg`, `bcdedit`, `schtasks`, `Get-MMAgent`, restore point, DISM/Appx) | Windows' own executables | started by full path under `%SystemRoot%\System32` | none (not searched through `PATH`) | unchanged |
| Reading `app-settings.json`, `pending-setup.json`, saved profiles | data, not code | the user's data folder | reviewed: `pending-setup.json` is written but never read back; `app-settings.json` holds preferences and ids that are looked up in the built-in catalog, a chosen profile file's contents are applied to the user's own application settings through the built-in config catalog, and the NVIDIA profile file is only imported as driver settings | no change needed; nothing in these files names a program to run or a registry path |

## What `ProgramFolderTrust` decides

It reads the folder's access list (not by trying to write: an elevated process can write anywhere, which proves nothing) and answers "can something that is not the system, an administrator or
the installer change this folder?": a non-trusted owner, an Allow entry for anyone else with write, append, create, delete or change-permission rights on the folder, or delete/change-permission
rights on the folder itself through any parent. An unreadable list counts as "yes". Program Files and System32 pass; `%LOCALAPPDATA%\Programs\Mesura`, a Downloads folder and an unzipped folder
do not. The tests show it on the real file system: an ordinary process really overwrites a file in the per-user folder, really cannot write into Program Files, and the check agrees with both.

**What this means in practice.** Per-user install and the unelevated functions are untouched. In a per-user install, "ask for administrator rights at start" no longer prompts:
machine-wide settings stay "needs administrator", which the window explains (below). To have both, install for all users (`msiexec /i Mesura.msi ALLUSERS=1`, one UAC prompt, Program Files).

**There is no switch to override this.** Protection is not weakened for convenience. The two supported installs are:

| Install | Where | Administrator rights | What works |
|---|---|---|---|
| Per user (default) | `%LOCALAPPDATA%\Programs\Mesura` or any folder users can write | never requested; Mesura says why | everything that needs no administrator rights; machine-wide settings, activation, NVIDIA profile import, all-users updates are skipped or declined with a stated reason |
| All users | `Program Files\Mesura` (`ALLUSERS=1`) | requested at start (UAC) | everything, including administrative operations |

**How the interface says it** (every place that used to say "restart as administrator" now says the true thing in a per-user install, through one rule, `Loc.AdminKey`: a `.PerUser` variant of the string):

- the sidebar chip: "No admin rights" with "How to get full access", which opens an explanation (folder, why, the exact `msiexec ... ALLUSERS=1` route, the technical reason) instead of a prompt that cannot come;
- Settings > Start-up: the "Ask for administrator rights at start" switch is shown off and disabled, with a note that it is not used in a per-user install, and a section "Administrator mode is unavailable in this install" with the reason;
- Setup page: the status chip reads "Per-user install: no administrator rights"; the confirmation before applying says how many changes will be skipped and that installing for all users applies them; the follow-up after a run says the same;
- Tweaks, Debloat, Profiles and the other lists: the "Needs administrator" tag has a tooltip: restart as administrator (all-users install, not elevated) or install for all users (per-user install);
- Activation: the note above the cards;
- NVIDIA step of Setup: "needs administrator rights, which a per-user install never uses: install Mesura for all users";
- the tray start-up prompt and the start-up prompt are simply not shown.
  In an all-users install none of this changes: the original strings are used.

## The machine-wide updater, in order

1. The app (not elevated) downloads the package and manifest into `%LOCALAPPDATA%\Mesura\updates\` and verifies signature, size, SHA-256 (as before). On "Install", it starts
   `Program Files\Mesura\cli\mesura.exe update stage ...` with the UAC prompt. If the install folder is not protected it does not start it at all.
2. `update stage` refuses to run unless it is administrator, **is** the command line registered in HKLM, and the install is machine-wide. It reads the manifest and signature (size-limited), verifies the signature,
   then checks schema, expiry, that the version is newer than the one running, and the package description.
3. It creates `%ProgramData%\MesuraUpdates\run-*` with an access list that gives write access only to SYSTEM and Administrators (users may read, to show the log). The result is **checked after it is made**: if
   someone else made the parent first, or it can otherwise be changed, a new unpredictable parent is used instead.
4. It copies the package into that folder, hashing the bytes it writes; the size and SHA-256 must equal the signed manifest's, otherwise the folder is removed. It writes the verified manifest bytes and
   signature there and copies the command line there.
5. It starts the protected copy: `update apply` with the protected files. That is the updater of rc1 (wait for the app to close, verify again, run `msiexec`, restart), now running from a place users cannot change.
   `update apply` also refuses to run elevated, for a machine-wide install, from a folder users can change.
6. The result is written to the user's data folder; the elevated writer refuses a link (reparse point) where it expects a plain folder or file.

Tested with fakes (`UpdateStagerTests`: signature, hash, size, expiry, downgrade, language, unprotected install, no folder made when refused). **Not run on real hardware**: creating the protected folder with real administrator
rights and the whole path through a real UAC prompt (it needs a test PC or VM); it is listed as not yet verified.

## What is still true (residual risk, stated plainly)

- Running an unzipped or per-user build as administrator **by hand** (right-click, Run as administrator) is the user's own decision: Windows asks, and Mesura works elevated from that folder, as any program would. Mesura does not offer or prompt for it. The command line (`mesura.exe`) has no such guard either, except `update apply` for a machine-wide install.
- The elevated result file and log are written into the user's data folder; the writer rejects links but cannot be made race-free against a user who is actively swapping folders at that moment. The worst it can do is
  put one small JSON file somewhere the updater account can write; it is not code that is run.
- Installers and executables are **not Authenticode-signed** (no certificate): Windows SmartScreen and the UAC prompt show an unknown publisher. The update signature protects updates; the first download should be checked
  against the published SHA-256.
- An attacker who can already run code as an **administrator** has nothing to gain from Mesura.
- A per-user install's `HKCU\Software\Mesura` registration can be edited by any program the user runs; it is only used to choose the (non-elevated) update path, and a machine-wide update reads HKLM only.
- Not yet verified: Windows 10, Windows Home, an elevated session, a second user, Smart App Control.

## Tests that reproduce the problems

- `RevertTrustTests`: forged registry, service and mixed records, with and without the check (the "without" test is the weakness; the "with" test is the fix).
- `ProgramFolderTrustTests`: a real overwrite in a per-user folder; Program Files and System32 protected; a missing folder untrusted.
- `NvidiaToolTrustTests`: elevated refuses a tool in a user-writable folder, not-elevated allows it.
- `CatalogFilesTests`: elevated Mesura ignores an edited (or broken) Windows or apps catalog.
- `UpdateStagerTests`, `UpdateApplierTests`: see above.
