# Administrator rights at start (0.7, D049)

The owner decided (2026-11-07): Mesura asks Windows for administrator rights by default when it starts. Convenience over least privilege. This file says how that is built, what each way of starting does, and what only a real PC can confirm.

## What it was

Mesura.exe (the window, and `--tray`) already restarted itself elevated on request (D029, 2026-10-23): `ShellLauncher.RelaunchElevated` starts the same executable with the `runas` verb and `--relaunched`; the elevated copy waits for the first to release the single-instance mutex, then takes over. The default, though, was **"ask"**: a dialog first ("Restart as administrator" / "Continue without admin rights" / "Remember my choice"), then Windows' own prompt. Saved choices: `normal` (never ask) and `admin` (ask Windows straight away) in `app-settings.json` (`StartupMode`, with the older `ElevateAtStart` switch). In a folder any program can change (a per-user install, a build folder) it never elevated and said so once (docs/security.md, 2026-10-30, no override). The command line is a separate program (`cli\mesura.exe`), never elevates itself and has no manifest asking for it.

## What it is now

* **The default is `admin`.** `StartupPolicy.EffectiveMode`: a saved mode wins; with none saved, the older switch decides (on, which is every user who never touched it: `admin`; off, turned off on purpose: `normal`). So a 0.6.0 user who never chose gets Windows' prompt at start with no dialog in front of it. Nothing is migrated or rewritten: the setting file keeps its shape.
* **What was chosen on purpose stays.** A saved `normal` ("Continue without admin rights, remember", or Settings) still means never ask; a saved `ask` still shows the dialog. Settings lists the three modes, Administrator first.
* **Decided once per start, in one tested place** (`Mesura.Core.Support.StartupPolicy.Decide`, `StartupPolicyTests`): the window asks nothing when the process is already elevated, when it is the copy an elevation restart started (`--relaunched`), when the launch said `--no-elevate`, or when it is the tray started at sign-in (the tray's window asks once, when it first opens: no prompt at logon). In a folder any program can change nothing is requested and, with no choice saved, the one explanation is shown (the default cannot be honoured there), as the old "ask" did; "Don't show this again" saves `normal`.
* **Declining the prompt** keeps Mesura running unelevated: the process that asked has already marked "asked" for its whole life (the tray's window will not ask again), the dock says "No admin rights" and offers Restart as administrator, and changes that need it are skipped and said so. There is no loop: the decision is not repeated by the same process, and the elevated copy never restarts again.
* **One instance.** Unchanged and checked: the first process releases the session mutex when it leaves for the elevated copy, the elevated copy waits for it (20 s), then takes over; a later plain launch of the same user signals the running instance (mutex and events grant the signed-in user full access) instead of starting another.

## GUI, command line, children

| | Level | Why |
|---|---|---|
| Mesura.exe (window) from Explorer, Start menu, a shortcut, a terminal | starts as the user, asks Windows once, then continues as administrator (the first process exits) | the prompt can be declined without ending the start |
| `Mesura.exe --no-elevate` (scripts, tests, the update restart of a `--no-elevate` copy) | as it is | no decision of any kind |
| `Mesura.exe --tray` at sign-in | as the user | no prompt at logon; the first window asks once |
| `cli\mesura.exe` | whoever started it; never prompts, never launches the window | automation and the other components call it |
| Mesura started again after a machine-wide update | as the signed-in user (the updater's shell-token start, so never as another account that approved the prompt), then it asks once as any start does | one prompt per start, not an elevated child of an elevated updater |
| winget for per-user apps (Discord, Spotify, Vencord), NVIDIA Profile Inspector, per-user settings | the signed-in user's token or hive, as before | an elevated Mesura still acts for the signed-in user where the work is theirs (D029) |
| Windows settings pages (`ms-settings:`), https links | opened by the shell from the elevated process, as before | see "Not decided here" |

## Installer and shortcuts

Nothing in the package changes. The installer asks Windows for administrator approval for an all-users install and not for a per-user one, as before, and does not ask again for Mesura: its "start Mesura" box starts the program as the user, so the one prompt after a clean install is Mesura's own. The Start menu and desktop shortcuts start `Mesura.exe` with no arguments and no run-as-administrator flag on purpose: with that flag a declined prompt would end the start instead of leaving Mesura running without elevation. An upgrade replaces the program and never touches `%LOCALAPPDATA%\Mesura`, so the saved start-up mode survives it. All-users is the wizard's default, which is what makes the default effective; a per-user copy keeps running unelevated by the security rule above, and says how to get full access.

## Checked

* Unit tests: `StartupPolicyTests` (21): default, older switch, saved choices, unknown saved values, already elevated, `--relaunched`, `--no-elevate`, tray, shared folder, once-per-start.
* The real binary, isolated data folder (`MESURA_DATA_DIR`): the start-up log records the decision (`administrator rights at start: ExplainSharedFolder` from a build folder).
* Windows Sandbox, `admin-start` (all-users install of the English and of the Russian package, 2026-11-07): 37 PASS, 0 FAIL. No `requireAdministrator` or `highestAvailable` manifest in the window or the command line; the shortcut starts `Mesura.exe` with no arguments and without the run-as-administrator flag; the program folder is not writable by ordinary users; from the shortcut one process and one window, the start-up log says "Continue (elevated now: True)", no `--relaunched` copy; a second launch leaves one process; `--exit` ends it; `--no-elevate` starts; the command line ran `windows list` to the end with no window and no process left; uninstall kept the settings file byte for byte.
* Windows Sandbox, `update` (the real 0.6.0 candidate package, installed per user, then the new build's package over it, 0.6.5 as the test version; the installer lifecycle part was skipped because the package itself is unchanged): 15 PASS, 0 FAIL. One product registered and it is the newer; program and shortcut are the newer's; every file in the data folder byte for byte as before; the newer command line lists and reverts the older build's run; uninstall keeps the data.
* The sandbox desktop is itself elevated (UAC off), so these show the "already elevated" side and everything around it, not the prompt: accepting and declining it need a real PC.

## Needs a real PC (UAC on), about ten minutes

Install the all-users MSI on a PC where that is acceptable, with a profile that never chose a start-up mode (delete `StartupMode` from `%LOCALAPPDATA%\Mesura\Setup\app-settings.json` or use a new user), then:

1. Start from the Start menu: one Windows prompt, then one window, "Administrator" in the dock; no dialog before the prompt; Task Manager shows one Mesura.
2. Start again while it runs: the window comes forward, no prompt.
3. Close, start, **decline** the prompt: Mesura opens without admin rights, says so, does not ask again; Restart as administrator works.
4. `Mesura.exe --no-elevate`: no prompt. `cli\mesura.exe windows list` from a plain terminal: no prompt.
5. Sign out and in with the tray option on: no prompt at logon; opening the window from the tray asks once.
6. Apply a harmless per-user setting and a machine-wide one (both undone from Activity), and open an https link from Mesura: the link opens in the browser you normally use (see below).
7. Upgrade over an older install: one prompt for the installer (if started from a plain window), one for Mesura; settings and history unchanged.

## Not decided here

Links and Windows settings pages are opened with `ShellExecute` from the process as it is. From an elevated Mesura a browser may start elevated (a Chromium browser then shows an "administrator" warning, or opens a separate window). Windows offers a de-elevated way to open links (through the running shell), but it cannot be verified in a sandbox whose shell is elevated, so it was not changed without a real-PC check. Check 6 above shows whether it matters.
