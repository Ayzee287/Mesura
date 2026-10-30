# Updates (0.3)

How Mesura finds, downloads, verifies and installs a newer version, why the update can be trusted, and what is still outside the repository.

## What the user sees

1. **At start**, five seconds after the window opens, Mesura asks the update server in the background (12 s limit, one request pair). It never delays the
   window. Switch it off in Settings > Updates.
2. **If something is newer**, a card appears at the bottom of the sidebar ("Version 0.3.1 is available"). Click it: the dialog shows the release notes in the
   app's language, the size, and three choices: **Install and restart**, **Remind me tomorrow**, **Skip this version** (a newer one is offered again).
3. **Install and restart** downloads with a progress bar and checks the package. Then one confirmation ("Mesura closes, installs 0.3.1 and opens again"), and
   Mesura closes. A small updater runs the installer and starts Mesura again, which says "Updated to 0.3.1" once.
4. **If anything fails** the running version stays as it is and Mesura says what happened in a sentence (technical details behind a disclosure, the installer's
   own log in `%LOCALAPPDATA%\Mesura\updates\update-install.log`).

Nothing is downloaded or installed without a click. Settings > Updates also has **Check now** and the channel (Stable, Preview).

## Where updates come from

| What | Where |
|---|---|
| Stable manifest | `https://github.com/Ayzee287/Mesura/releases/latest/download/update-stable.json` and `.sig` (assets of the newest non-pre-release) |
| Preview manifest | `https://github.com/Ayzee287/Mesura/releases/download/preview/update-preview.json` and `.sig` (assets of a rolling pre-release tagged `preview`) |
| Package | the `Mesura-<version>-x64.msi` asset of the same release; its URL is inside the signed manifest |
| Test source | `MESURA_UPDATE_BASE=http://127.0.0.1:8099` serves `update-<channel>.json` flat from that base (only https or loopback is accepted) |

The host is not trusted (see below), so a mirror, a proxy or another host would work the same.

## The trust model

The threat is not only a corrupted download. A hash published next to the file on the same server proves nothing if the server is what is attacked. So:

- Every release's manifest is **signed with an ECDSA P-256 key** whose private half is a file kept outside the repository on the release machine
  (`%USERPROFILE%\.mesura-release\update-signing.pk8`). The app carries the public half(s) (`Mesura.Core/Update/UpdateKeys.cs`). A manifest whose signature does
  not verify is thrown away before a byte of it is read as data.
- The manifest carries the **SHA-256 and size** of the installer. The downloaded file must match both, so the file is exactly what the signature vouches for,
  wherever it was fetched from. A mismatch deletes the file.
- The manifest carries its **channel**, an **expiry** date and a **sequence number**. A preview manifest cannot be passed off as stable, an old manifest stops
  working after it expires (default 180 days) and an older manifest than one already accepted is a replay and is refused.
- The package URL must be https (or this PC itself, for tests). A pre-release is never offered on the stable channel. A version that is not newer is never offered.
- The updater **verifies again** what was staged (manifest signature, package hash, that the version is newer) before it runs the installer, so a file
  swapped in the download folder between the download and the install is caught.
- **Key rotation:** the app trusts a list of keys. To change keys, publish a release signed by the old key whose app lists the new one too, then retire the old one a
  release later. Losing the private key means every installed copy must be updated by hand once (a new installer carries the new public key).

What this does not give: **Authenticode signing** of `Mesura.exe` and the installer needs a code-signing certificate, which does not exist yet. Windows SmartScreen
will warn about the downloaded installer on a first run, and Smart App Control may block it. The update signature protects updates, not the very first download:
verify the first installer's SHA-256 against the value published with the release.

## How the update is applied

An update is **the new installer package run over the installed one** (a Windows Installer major upgrade). Windows Installer is transactional: if it fails part-way
it restores the previous version, which is the rollback. The running program is never overwritten by itself.

1. The app (checked it is not in the middle of a run that changes the PC) starts the updater, then exits. **Per-user install:** it copies `cli\mesura.exe` to
   `%LOCALAPPDATA%\Mesura\updates\run-xxxxxxxx\` and starts that copy as `mesura update apply ...` (no elevation: everything runs with the user's own rights). **All-users install:** it starts the
   *installed* `Program Files\Mesura\cli\mesura.exe update stage ...` (one UAC prompt if the app isn't elevated), which verifies the package and moves it into a folder only administrators can write, and starts
   the updater from there (`docs/security.md` has the steps); nothing from the user's folders is run elevated.
2. The updater waits up to 45 s for the app to exit, verifies the staged files, and runs `msiexec /i <package> /qb! /norestart` with the same scope and folder as
   the installed copy (`ALLUSERS=1`, or `ALLUSERS=2 MSIINSTALLPERUSER=1`; `INSTALLFOLDER=<folder>`).
3. It writes `update-result.json` and starts Mesura again (`--updated-from <old>` on success, `--update-failed` otherwise). The app reads the result once, shows it
   and cleans the staging folder.

A copy that is **not installed** (an unzipped folder, a build folder) cannot replace itself: Install opens the verified installer's wizard instead, which installs
Mesura properly. That is also the migration path from a manual unzip (see `docs/installing.md`).

## Failures and what Mesura does

| Situation | Behaviour |
|---|---|
| No internet, DNS failure | silent at start; "Couldn't reach the update server" on Check now; keeps running |
| Server error, 404 (nothing published), timeout | same, with the matching sentence |
| Signature, channel, expiry, replay or package description refused | "didn't pass verification and was ignored"; nothing offered |
| Disk space short | said before downloading (needs twice the package plus 150 MB) |
| Connection drops mid-download | resumed with an HTTP Range request (3 attempts) |
| Hash or size differs | file deleted, "damaged or isn't the official one" |
| Data folder not writable | "couldn't save the update" |
| Mesura does not close in 45 s | nothing is changed, the app is not restarted |
| All-users install in a folder users can change | the elevated stage is not started; "couldn't start the updater" |
| Manifest expired between the check and "Install" | refused again at apply time |
| UAC declined / installer cancelled / Windows busy | nothing changed; named in the notice |
| Installer fails part-way | Windows Installer rolls back; old version restarted; notice "The update didn't finish" |

## Language

The installer records its language (`HKCU|HKLM\Software\Mesura\Language`, `en` or `ru`). The manifest lists the English installer first (an older Mesura takes the first one) and, when built with the Russian
installer present, the Russian one with `"language": "ru"`. An update downloads the installer in the installed language and checks that package's own size and hash; if the manifest has none in that language the English
one is used. Either installer replaces the other in place. The language you chose **inside Mesura** (Settings > Language) is a separate setting and is never changed by an update.

## Publishing a Preview release (an external step: not done by the build)

Everything is prepared locally; publishing is yours to do and nothing here does it:

1. `powershell -File tools\publish.ps1 -Label rc2`, then `tools\build-installer.ps1` (the FAILTEST packages are for testing only and are **never** published).
2. `powershell -File tools\make-release.ps1 -Channel preview -Url https://github.com/Ayzee287/Mesura/releases/download/preview/Mesura-0.3.0-rc2-x64.msi -NotesEn notes-en.md -NotesRu notes-ru.md`
   writes `update-preview.json`, `update-preview.json.sig` and `SHA256SUMS.txt` (the Russian installer is listed in the manifest automatically).
3. On GitHub (the owner's step): a pre-release tagged `preview` with these assets and exactly these names: both `Mesura-0.3.0-rc2-x64*.msi`, the zip, `SHA256SUMS.txt`, `update-preview.json`, `update-preview.json.sig`.
4. Check what is published, the way the app would, without installing anything: `powershell -File tools\verify-published.ps1 -Channel preview -ExpectVersion 0.3.0-rc2`.
5. The end-to-end update from the real source: `tools\e2e-update.ps1 -Old <the rc1 build folder> -New <the published build's folder> -RealSource` installs the previous build per user into a throwaway folder,
   uses the real GitHub address and the Preview channel, drives the window, and cleans up after itself.

Until step 3, `mesura update check --channel preview` says nothing has been published (HTTP 404) and `verify-published.ps1` reports the same: that is the true state, not a fault.

## Publishing a release (owner)

```
powershell -File tools\publish.ps1 -Label rc1          # builds the app and command line, the portable zip
powershell -File tools\build-installer.ps1             # builds the English and Russian installers
powershell -File tools\make-release.ps1 -Channel preview -Url <https url of the English msi> -NotesEn notes-en.md -NotesRu notes-ru.md
```

`make-release.ps1` signs the manifest with the key above and writes `update-<channel>.json`, `.sig` and `SHA256SUMS.txt` beside the installers. To publish: create a GitHub
release (stable: a normal release; preview: the `preview` pre-release), attach the installers, the zip, `SHA256SUMS.txt` and the two manifest files with exactly those names.

## Still needs doing outside the repository

- Publishing the first release with these assets. Until then Check now says "No update has been published yet" (the 404 is expected).
- A code-signing certificate (Authenticode) for `Mesura.exe` and the installers, if SmartScreen warnings matter.
- A backup of `update-signing.pk8`.
