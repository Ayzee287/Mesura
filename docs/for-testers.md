# For testers / Для тестировщиков

Mesura 0.2.0-rc1. Two things are asked of testers: (1) if an **earlier version did not start** on your PC, collect a diagnostic file;
(2) if you have a **spare PC or a virtual machine**, run the operations test. English first, Russian below each part.

Mesura 0.2.0-rc1. От тестировщиков нужны две вещи: (1) если **прежняя версия не запускалась**, собрать диагностический файл;
(2) если есть **запасной ПК или виртуальная машина**, прогнать тест операций. Русский текст идёт после английского в каждой части.

---

## 1. Mesura did not start (version 0.1.0 or earlier) / Mesura не запускалась

**What it probably was.** Version 0.1.0 unpacked its native libraries into your TEMP folder every time it started. If TEMP is missing,
read-only, full, or blocked by a security product, the program ended before showing anything and left no log. Version 0.2.0 ships those
files beside `Mesura.exe` and writes a log from its first line. This is the *likely* cause, not a proven one for your PC: that is what the
diagnostic file is for.

**What to do (nothing is changed or sent; you send the file yourself):**

1. Download the 0.2.0 zip and unzip the **whole** archive to a folder such as `C:\Mesura` (not "Open" inside the zip).
2. Open the folder and double-click **`Diagnose Mesura.cmd`**. It writes `Mesura-diagnostics-<time>.txt` on your Desktop. It reads only: Windows
   version, Smart App Control state, whether Software Restriction Policy or AppLocker exist, TEMP and LOCALAPPDATA checks, the Windows event log
   entries about Mesura, and Mesura's start log. Your user name is replaced by `%USERNAME%`.
3. To diagnose the **old** version instead, run it against the old folder: `powershell -ExecutionPolicy Bypass -File Diagnose-Mesura.ps1 -Folder "D:\path\to\old\Mesura"`.
4. Start `Mesura.exe` from the new folder. If it does not start, run `Diagnose Mesura.cmd` again, then send **both** files:
   the diagnostics text and `%LOCALAPPDATA%\Mesura\logs\mesura-startup.log` (if it exists).
5. Tell us: Windows 10 or 11, Home or Pro, whether Smart App Control is on, any antivirus, and what you saw (nothing, an error window, a SmartScreen warning).

**How to read the result (for us):** a start log with a last "stage:" line shows how far Mesura got; **no log at all** means it ended before
`Program.Main`: look at the Application event log entries in the diagnostics (`.NET Runtime` 1023, `Application Error` 1000), at Smart App Control /
SRP / AppLocker, and at "TEMP exists / writable" for 0.1.0.

**Do not assume it is fixed.** Version 0.2.0 fixes the TEMP cause, which was reproduced on one PC. Until testers whose PC failed before confirm that 0.2.0
starts there, the problem is *addressed*, not *resolved*. Smart App Control and security products can still block an unsigned program.

**Что это, скорее всего, было.** Версия 0.1.0 при каждом запуске распаковывала свои нативные библиотеки во временную папку TEMP. Если TEMP
отсутствует, только для чтения, переполнена или заблокирована защитой, программа завершалась, ничего не показав и не оставив журнала. Версия 0.2.0
кладёт эти файлы рядом с `Mesura.exe` и пишет журнал с первой строки. Это *вероятная* причина, но для вашего ПК она не доказана: для этого и нужен диагностический файл.

**Что сделать (ничего не меняется и не отправляется; файл вы присылаете сами):**

1. Скачайте zip версии 0.2.0 и распакуйте **весь** архив в папку, например `C:\Mesura` (не открывайте программу прямо из архива).
2. Откройте папку и дважды щёлкните **`Diagnose Mesura.cmd`**. На рабочем столе появится `Mesura-diagnostics-<время>.txt`. Скрипт только читает: версию Windows,
   состояние Smart App Control, наличие политик SRP и AppLocker, проверки TEMP и LOCALAPPDATA, записи журнала событий Windows о Mesura и журнал запуска Mesura.
   Имя пользователя в путях заменено на `%USERNAME%`.
3. Чтобы проверить **старую** версию, запустите скрипт для старой папки: `powershell -ExecutionPolicy Bypass -File Diagnose-Mesura.ps1 -Folder "D:\путь\к\старой\Mesura"`.
4. Запустите `Mesura.exe` из новой папки. Если не запускается — снова запустите `Diagnose Mesura.cmd` и пришлите **оба** файла: диагностику и
   `%LOCALAPPDATA%\Mesura\logs\mesura-startup.log` (если он есть).
5. Напишите: Windows 10 или 11, Home или Pro, включён ли Smart App Control, какой антивирус, и что вы увидели (ничего, окно ошибки, предупреждение SmartScreen).

**Не считайте проблему решённой.** 0.2.0 устраняет причину с TEMP, воспроизведённую на одном ПК. Пока тестировщики, у которых запуск не удавался, не подтвердят, что 0.2.0
у них запускается, проблема *устранена по причине*, но не *решена*. Smart App Control и защитные программы по-прежнему могут блокировать неподписанную программу.

---

## 2. Operations test on a spare PC or VM / Тест операций на запасном ПК или ВМ

The release folder contains `Test-MesuraOperations.ps1` (and `Test Mesura operations.cmd`). It checks the things that could not be tested on the
developer's PC: app removal and restore, a boot-entry setting, hibernation, the memory agent, a per-adapter setting and restore points. **Each result is
read back with Windows' own tools, not with Mesura.**

**Use a disposable machine or a VM, with a snapshot or checkpoint taken first.** It really changes the system (and reverts it).

1. **Dry run first** (changes nothing): double-click `Test Mesura operations.cmd`. It lists what each scenario would do and what cannot run on this PC and why.
2. **Real run**, in PowerShell **as administrator**, from the release folder:
   `powershell -ExecutionPolicy Bypass -File .\Test-MesuraOperations.ps1 -Run -ConfirmTestMachine`
   (add `-Scenario Appx,Bcd` to run only some: `Info, Policy, RestorePoint, Appx, Bcd, Hibernate, MemoryAgent, Adapter`).
3. The report is written to the Desktop (`Mesura-operations-test-<time>.md`). Send it.

What makes a scenario meaningful:

| Scenario | Needs on the test machine | Notes |
|---|---|---|
| Policy | a Windows build that has the LongPaths or DataCollection key | applies and reverts a machine registry value |
| RestorePoint | System Protection on for C: | Windows limits automatic restore points to one per 24 hours: "skipped" is an honest outcome, not a failure |
| Appx | a removable app installed for the signed-in user (Solitaire, Clipchamp, Feedback Hub, Bing News or Weather, To Do, 3D Viewer); a **stock** Windows image has them | after revert the app should be registered again; if Windows has no staged copy, Mesura must *say so* and the test checks that it did |
| Bcd | BitLocker off or suspended; English or another language (the value text is read as English) | sets `disabledynamictick` itself, lets Mesura remove it, reverts, then restores the BCD from an export it made |
| Hibernate | hibernation currently on | `powercfg /h on` first if it is off |
| MemoryAgent | memory compression currently on | read with `Get-MMAgent` |
| Adapter | a physical adapter whose driver has the `*FlowControl` keyword | the adapter restarts (a short network drop); typically not present in a VM |

Also test by hand in the window (Windows 10 and 11, Home and Pro if you can):
- start `Mesura.exe`; check each page opens; Language switch; the Tweaks search and details drawer; Debloat tabs.
- on Debloat > Startup apps, add a harmless entry to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (value `MesuraTest`, data `notepad.exe`), press Scan again, turn it off,
  confirm in Task Manager > Startup apps that it shows Disabled, then Undo from the result banner and confirm it shows Enabled again. Delete the test value afterwards.
- on a Windows **Home** PC, machine policy entries (telemetry, Edge excluded) should read "not for this PC" and never be written.
- send what you saw, including anything that looked wrong, even if it did not seem important.

Тестовый скрипт `Test-MesuraOperations.ps1` (и `Test Mesura operations.cmd`) лежит в папке релиза. Он проверяет то, что нельзя было проверить на ПК разработчика:
удаление и восстановление приложений, параметр загрузки, гибернацию, агент памяти, параметр сетевого адаптера и точки восстановления. **Каждый результат перепроверяется
средствами самой Windows, а не Mesura.**

**Используйте запасной ПК или ВМ, предварительно сделав снимок (checkpoint).** Скрипт реально меняет систему (и возвращает обратно).

1. **Сначала пробный прогон** (ничего не меняет): дважды щёлкните `Test Mesura operations.cmd`. Он перечислит, что сделает каждый сценарий и что на этом ПК выполнить нельзя и почему.
2. **Настоящий прогон** в PowerShell **от имени администратора** из папки релиза:
   `powershell -ExecutionPolicy Bypass -File .\Test-MesuraOperations.ps1 -Run -ConfirmTestMachine`
   (`-Scenario Appx,Bcd` — только часть сценариев: `Info, Policy, RestorePoint, Appx, Bcd, Hibernate, MemoryAgent, Adapter`).
3. Отчёт появится на рабочем столе (`Mesura-operations-test-<время>.md`). Пришлите его.

Сценарий имеет смысл, если выполнены условия из таблицы выше. Пропущенный («SKIPPED») сценарий **не проверен**; успех («PASS») относится только к этому ПК.

Вручную в окне проверьте (Windows 10 и 11, Home и Pro, если возможно):
- запуск `Mesura.exe`, открытие каждой страницы, переключение языка, поиск и панель подробностей в «Твиках», вкладки «Очистки»;
- «Очистка» > «Автозагрузка»: добавьте безвредную запись в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (значение `MesuraTest`, данные `notepad.exe`), нажмите «Проверить снова», отключите её,
  убедитесь в «Диспетчере задач» > «Автозагрузка», что она «Отключено», затем нажмите «Отменить» в баннере результата и убедитесь, что снова «Включено». Потом удалите тестовое значение;
- на Windows **Home** политики для всего ПК (телеметрия; Edge исключение) должны показываться как «Не для этого ПК» и никогда не записываться;
- пришлите всё, что показалось странным, даже если кажется неважным.
