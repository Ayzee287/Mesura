<p align="center"><img src="https://raw.githubusercontent.com/Ayzee287/Mesura/main/docs/images/mesura-logo.png" width="112" alt="Mesura logo"></p>

# Mesura

Mesura sets up and tunes a Windows 10 or 11 PC, and says what it did. It installs your apps, applies a reviewed set of
Windows settings (every change previewed first, recorded, and undoable), restores your app settings on a fresh install,
and sets up an NVIDIA driver and profile. The window is in English and Russian.

It is free software under the [MIT licence](LICENSE). It is not signed, not affiliated with Microsoft, NVIDIA or any app
it installs, and makes no promise that your games run faster: see [What it does not claim](#what-it-does-not-claim).

## What it does

- **Setup** is the start page. Choose **Recommended** or **Maximum performance**, look at the plan, then apply it.
  - *Recommended*: everyday Windows with less promotion, ad profiling and nagging, and nothing you would miss
    (14 settings, none that removes a feature).
  - *Maximum performance*: Recommended plus fewer optional background functions and the desktop power settings, with the
    price of each named (24 settings and 7 you tick yourself).
  - [What is in them and why](docs/profile-review-2026-10.md), and
    [how their stored values were checked](docs/value-verification-2026-10.md).
- **All settings** lists about a hundred Windows settings by category with what each writes, its risk, and what is known
  about it (documented by Microsoft, verified, or community-only). You can pick any of them one by one.
- **Apps** installs apps through winget (browsers, Steam, Discord, Git, runtimes and the like), detecting what is there.
- **Configurations** exports and restores a short allowlist of app settings (Steam, Brave, Discord, Vencord, Spotify).
- **Profiles** saves a PC's setup and runs it again on a new one.
- **NVIDIA** (when the PC has a GeForce card): looks up the current WHQL driver, installs it trimmed with NVCleanstall
  to the selection you choose, and imports [Mesura's own driver profile](docs/nvidia-profile.md), backing up and putting
  back your own profiles around the install.
- **Activation** shows the Windows and Office licence state, and installs a product key you own and activates it through
  Windows' own licensing service. Mesura doesn't store or log the key.
- **Activity** is the history of everything Mesura changed, with the exact previous value, and undoes it.
- A command line, `cli\mesura.exe`, does the same from a terminal; anything that changes the PC previews by default and
  acts only with `--apply`.

## Install

1. Download `Mesura-<version>-win-x64.zip` from the release and check its SHA-256 against `SHA256SUMS.txt`
   (`Get-FileHash .\Mesura-<version>-win-x64.zip` in PowerShell).
2. Unzip the whole folder somewhere permanent, for example `%LOCALAPPDATA%\Programs\Mesura`. There is no installer.
3. Start `Mesura.exe`. Windows SmartScreen may warn that the app is unrecognised, because the file is not code-signed:
   choose *More info*, then *Run anyway*, once you have checked the hash.
4. Mesura asks once for administrator rights, because changes to the whole PC need them. You can decline (per-user
   settings still work) or turn the question off in Settings. **Nothing changes until you confirm an Apply, Install, Undo
   or profile run.**

It needs Windows 10 (64-bit) or Windows 11 and no other software: the .NET runtime is inside `Mesura.exe`.

## Limits you should know about

- **Not yet used on a real PC.** The author has previewed the plans, read-only, on one Windows 11 25H2 Pro machine, and
  the roughly thousand tests exercise applying and undoing against mocks and temporary files. Nothing has been applied to
  a clean PC, nothing has run on Windows Home or Windows 10, and the NVIDIA step has never run on a GeForce card. The
  preview shows what would be written before anything is: treat the first run as one.
- **Windows Home** ignores machine policies. Mesura marks those entries "not applicable" there instead of writing them.
- **Telemetry:** Mesura reduces what Windows lets you reduce. Windows always sends its *Required* diagnostic data on Home
  and Pro; Edge, Microsoft 365, Store apps, OneDrive and third-party software have their own settings. See the
  [telemetry table](docs/profile-review-2026-10.md#telemetry-what-mesura-controls-and-what-it-cant).
- **Network use:** Mesura itself contacts the network only for NVIDIA's public driver lookup (your GPU's device id and
  Windows build, on NVIDIA PCs only). It has no telemetry and no account. The tools it starts at your request use the
  network as they always do: winget and the installers it runs, NVCleanstall, and Windows' own activation service.
- **Third-party marks:** the small app icons in Apps and Setup are the apps' own marks, used only to show which app is
  meant. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## What it does not claim

No setting in Mesura is claimed to raise your frame rate or lower your latency. Where Microsoft or NVIDIA documents what
a setting does, Mesura says that; where the effect is unmeasured, it says so. Several settings (high-performance power
plan, disabling background functions, the GPU's maximum-performance power mode) are standard advice with an unmeasured gain on
current hardware, and each costs something (power, heat, a feature). The point of Mesura is that you see exactly what
changes and can put it back, not that any number goes up.

## Build

```
dotnet build Mesura.slnx
dotnet test Mesura.slnx
powershell -File tools\publish.ps1 -Label ''      # a release build into artifacts\current
```

Needs the .NET SDK named in `global.json`. [`docs/build-and-publish.md`](docs/build-and-publish.md) explains the build
folders. The code is split so that `src/Mesura.Core` has no operating-system calls (parsers, models, the catalogs), the
`Platform.Windows` project only reads, and everything that writes lives in `Mesura.Setup.Windows`.

---

## По-русски

Mesura настраивает Windows 10 и 11 и показывает, что именно сделала: ставит приложения, применяет проверенный набор
настроек Windows (каждое изменение сначала показывается, записывается и отменяется), возвращает настройки приложений на
новой системе и ставит драйвер NVIDIA с собственным профилем. Интерфейс на русском и английском. Лицензия — MIT, файл не
подписан, прироста кадров программа **не обещает**.

- **Рекомендуемая** настройка — повседневная Windows с меньшей рекламой, профилированием и навязчивыми подсказками, без
  потери функций (14 параметров). **Максимальная производительность** — она же плюс сокращение необязательных фоновых
  функций и питание настольного ПК, цена каждого пункта названа (24 параметра и 7 «по желанию»).
- Установка: скачайте архив, сверьте SHA-256 с `SHA256SUMS.txt`, распакуйте папку целиком и запустите `Mesura.exe`.
  SmartScreen может предупредить, что программа не распознана (файл не подписан): «Подробнее» → «Выполнить в любом случае».
  Ничего не меняется, пока вы не подтвердите применение.
- Ограничения: на реальном ПК ничего ещё не применялось (планы просмотрены без записи на одном ПК с Windows 11 25H2 Pro,
  применение и отмена проверены тестами на заглушках); на Windows Home политики помечаются «неприменимо»; шаг NVIDIA ни
  разу не запускался на видеокарте GeForce; обязательные диагностические данные Windows отключить нельзя — программа
  сокращает только то, что Windows позволяет сократить.
