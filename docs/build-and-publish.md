# Building, publishing and committing Mesura

The one documented way to produce a Mesura build. It is build-output only: it changes nothing on this PC, never
touches the installed copy, and makes no shortcut or start-up entry. It does not publish anywhere external, and there is
no in-app updater (that is a separate product decision, not part of this workflow).

## Build and publish the review version

```
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

This cleans and builds a self-contained Release of the app and the CLI and publishes them to `artifacts\current\`. The
default `-Label review` stamps the build `0.1.0-review` (shown in Settings > About, in the zip's name and at the top of
`Start here.txt`). For a plain release build, `tools\publish.ps1 -Label ''` gives `0.1.0`.

Verified in both tree states (2026-10-25):

- **From a working tree with uncommitted changes** (the normal case during development): the repo as it is now →
  `Commit: f83c949 (built from a working tree with uncommitted changes)`, and the executable version ends `.dirty`.
- **From a clean checkout** (every change committed): a clean worktree at `f83c949` → `Commit: f83c949`, no
  "uncommitted changes" note and no `.dirty` suffix. (Tested by running the current `publish.ps1` against a clean
  `git worktree`; `git status --porcelain` empty.)

So the version metadata always says which commit a build came from and whether that commit's tree had uncommitted
changes — a build is never silently passed off as a clean one.

## Where the build is written

```
artifacts\current\Mesura\Mesura.exe              the app (window + tray), self-contained — no .NET needed on the PC
artifacts\current\Mesura\cli\mesura.exe          the command line (same name to Windows, so its own folder)
artifacts\current\Mesura\Start here.txt          what the folder is and how to run it
artifacts\current\Mesura-0.1.0-review-win-x64.zip   the same folder as one file
artifacts\current\BUILD.txt                      version, commit, clean/dirty, when, and SHA-256 of each exe and the zip
artifacts\current\build.json                     the same facts as data
```

The newest build is always in `artifacts\current\`. The build it replaces is moved first to
`artifacts\previous\<date>-<version>-<commit>[-dirty]\`, so older builds stay for comparison, clearly apart. The Track A
capture kit keeps its own place in `artifacts\publish\` and is never touched.

## Running a build

```
tools\run-current.cmd                 prints BUILD.txt, then starts artifacts\current\Mesura\Mesura.exe
tools\run-current.cmd --no-elevate    the same, without the administrator prompt (for read-only checks)
tools\run-current.cmd --folder        opens the folder in Explorer instead of running it
```

## Checking what assistive technology reads

```
powershell -ExecutionPolicy Bypass -File tools\check-ui-names.ps1 [-Exe <Mesura.exe>] [-Lang en|ru]
```

Starts the app (default: the current build) with `--no-elevate`, visits every page and All settings through UI Automation,
and fails if any button, checkbox, list item, radio button or text box has no accessible name or is named after a C# type
(`Mesura.App.SettingRow`). Read-only (it only navigates) and refuses to run while another Mesura is open, because a second
launch would attach to it. Run it after changing a page or a list's item class; every list item class overrides
`ToString()` to its display name for this reason.

## The installed copy is kept separate

The build **never** writes to `%LOCALAPPDATA%\Programs\Mesura` (the installed copy). `publish.ps1` refuses to publish
into that folder: if `-Out` points inside it, the script stops with an error. To update the installed copy, copy
`artifacts\current\Mesura\` there by hand — deliberately, not as a side effect of building.

## Preparing a completed milestone for a local commit

Commits happen only on the owner's explicit authorisation, and nothing is pushed automatically.

1. Build and test: `dotnet build Mesura.slnx` then `dotnet test Mesura.slnx --no-build` (all green).
2. Publish the review build (above) and sanity-check it: start it, confirm Settings > About shows the expected
   `0.1.0-review (build <commit>)` and the uncommitted-changes note while the tree is still dirty.
3. Review the diff (`git status`, `git diff`) and group the work into coherent commits — the session report proposes a
   grouping.
4. On the owner's go, commit (and, separately and only when asked, push). The build artifacts under `artifacts\` are
   git-ignored and are not part of the commit; the build is reproduced from the committed sources.

After a real commit, the next `publish.ps1` from that clean tree stamps the build with the new commit and no `.dirty`
suffix, so the published build and the source commit line up exactly.

## Making a release

A release is a build of a clean, committed tree with no label, plus the hashes:

1. `dotnet build Mesura.slnx` and `dotnet test Mesura.slnx --no-build` (all green).
2. `git status` is clean. From a PowerShell session in the repository run `& .\tools\publish.ps1 -Label ''` (an empty
   label cannot be passed through `powershell -File`). It cleans, publishes the app and the command line self-contained,
   copies `LICENSE`, `THIRD-PARTY-NOTICES.md`, `README.md` and `CHANGELOG.md` into the folder, and writes:
   - `artifacts\current\Mesura-<version>-win-x64.zip` (the download),
   - `artifacts\current\SHA256SUMS.txt` (the zip's SHA-256, to be attached to the release),
   - `artifacts\current\BUILD.txt` and `build.json` (version, commit, hashes of the zip and both executables),
   - `Mesura\EXE-SHA256.txt` inside the zip (the executables' hashes).
3. Check the zip: its entries are `Mesura/Mesura.exe`, `Mesura/cli/mesura.exe`, `Mesura/cli/example.profile.json`,
   `Mesura/Start here.txt`, `Mesura/README.md`, `Mesura/LICENSE`, `Mesura/THIRD-PARTY-NOTICES.md`,
   `Mesura/CHANGELOG.md` and `Mesura/EXE-SHA256.txt`; the executables' version names the commit; the hash recomputed from
   the zip equals the one in `SHA256SUMS.txt`.
4. The executables are not code-signed. Say so in the release notes (SmartScreen will warn) and give the SHA-256.

## The installer, the update manifest and the checksums (0.3)

After `tools\publish.ps1` (which writes the portable zip), in order:

1. `powershell -File tools\build-installer.ps1` copies the files an install carries into `artifacts\current\installer-payload` and builds `Mesura-<version>-x64.msi` (English wizard) and
   `Mesura-<version>-x64-ru.msi` with WiX 6 (restored from NuGet; nothing is installed on the PC). The MSI version is `major.minor.patch.rc` (the release candidate number is the 4th field).
2. `powershell -File tools\make-release.ps1 -Channel preview|stable -Url <https url of the English msi> -NotesEn <file> [-NotesRu <file>]` signs `update-<channel>.json` (see `docs/updates.md`)
   with the key in `%USERPROFILE%\.mesura-release\` and writes `SHA256SUMS.txt` for the installers, the zip and the manifest. `-Local http://127.0.0.1:8099` writes the manifest for a local
   test server instead.
3. Nothing is uploaded by these scripts. Publishing is a GitHub release made by the owner, with those files attached under exactly those names.

End-to-end check of installing and updating without publishing anything: `tools\e2e-update.ps1`; the installer's own lifecycle (install, reinstall, upgrade, uninstall, a failed update) is `tools\e2e-installer.ps1`; what a published release looks like to the app is `tools\verify-published.ps1`.
