# Builds the owner's kit for testing the network adapter presets on a REAL adapter (the one thing the 0.6.0 profiles do that has never run on a real PC):
#
#   <Out>\1 - Preview the network presets (read only).cmd
#   <Out>\2 - REAL network preset test (the network drops, asks YES).cmd
#   <Out>\3 - Network inventory for other makers (read only).cmd
#   <Out>\Test-NetworkPreset.ps1, Measure-NetworkLatency.ps1, mesura.exe, README-RU.txt
#
# It copies files and writes text; it changes nothing on this PC and runs nothing from the kit.
#
#   powershell -ExecutionPolicy Bypass -File tools\tester\make-network-kit.ps1 -Cli <mesura.exe of the build under test> -Out artifacts\owner-network-tests
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Cli = (Resolve-Path $Cli).Path
$Out = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repo $Out }))
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$ascii = New-Object Text.UTF8Encoding($false)
function Cmd([string]$name, [string]$body) { [IO.File]::WriteAllText((Join-Path $Out $name), ($body -replace "`r?`n", "`r`n"), $ascii) }

Cmd '1 - Preview the network presets (read only).cmd' @'
@echo off
rem Only previews: which network adapters Mesura finds, which of the five presets are for them and what each would change. Writes network-preset-test.txt on your Desktop. Changes nothing.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-NetworkPreset.ps1"
echo.
pause
'@

Cmd '2 - REAL network preset test (the network drops, asks YES).cmd' @'
@echo off
rem THIS WRITES TO A REAL NETWORK ADAPTER, then puts it back. Read README-RU.txt first. Run it AT THE PC (the network drops for a few seconds per value; a remote session would cut itself off).
rem It asks you to type YES; anything else cancels. Applies ONE preset, reads every value back with Windows' own cmdlets, waits for you, undoes it and reads the values back again.
rem Elevation: "fltmc" succeeds only in an elevated process and does not need the Server service; the "elevated" argument allows one relaunch only.
if /i "%~1"=="elevated" goto elevated
fltmc >nul 2>&1
if %errorlevel% equ 0 goto elevated
powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath cmd.exe -ArgumentList '/c \"\"%~f0\" elevated\"'"
exit /b
:elevated
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-NetworkPreset.ps1" -Apply -HoldBeforeUndo
echo.
pause
'@

Cmd '3 - Network inventory for other makers (read only).cmd' @'
@echo off
rem Writes network-inventory.json on your Desktop: for each physical adapter the keywords its driver declares with their values. No network names, passwords, IP or MAC addresses or serial numbers (Mesura never reads those). Send it to add a maker (MediaTek, Realtek 2.5G, Aquantia, Intel I226 ...).
"%~dp0mesura.exe" network --json > "%USERPROFILE%\Desktop\network-inventory.json"
echo Wrote %USERPROFILE%\Desktop\network-inventory.json
echo.
pause
'@

Copy-Item (Join-Path $PSScriptRoot 'Test-NetworkPreset.ps1') $Out
Copy-Item (Join-Path $PSScriptRoot 'Measure-NetworkLatency.ps1') $Out
Copy-Item $Cli (Join-Path $Out 'mesura.exe')

$readme = @'
Тест сетевых пресетов на настоящем адаптере
===========================================

Что это. Профили Mesura 0.6.0 (Эталон Mesura и Максимальная производительность) по умолчанию применяют пресет для каждого найденного сетевого адаптера: Ethernet Realtek, Intel или Killer;
Wi-Fi Intel/Killer или Realtek. Запись в адаптер проверена только на заглушках и в Windows Sandbox (виртуальный адаптер). НА НАСТОЯЩЕМ АДАПТЕРЕ ЗАПИСЬ НИКОГДА НЕ ЗАПУСКАЛАСЬ.
Этот набор - единственная реальная проверка. Разрешение на тесты GPU на запись в сетевые адаптеры НЕ распространяется: запускайте шаг 2 только если вы согласны.

Что в папке
  1 - Preview the network presets (read only).cmd     ПРЕДПРОСМОТР: какие адаптеры найдены, какой пресет для какого, что изменилось бы. Ничего не пишет. Безопасно в любое время.
  2 - REAL network preset test (...).cmd              ПИШЕТ В НАСТОЯЩИЙ АДАПТЕР и возвращает обратно. Просит набрать YES. Нужны права администратора.
  3 - Network inventory for other makers (read only).cmd   network-inventory.json на рабочем столе: ключевые слова драйвера каждого адаптера. Читает без записи. Пришлите, чтобы добавить другого производителя.
  Test-NetworkPreset.ps1, Measure-NetworkLatency.ps1, mesura.exe - сами скрипты и программа.

Порядок
  1. Запустите шаг 1 и пришлите network-preset-test.txt с рабочего стола. Он покажет, какой пресет действует на этом ПК. Если он пишет "nothing to change", тестировать нечего.
  2. Шаг 2 только если согласны. Сидите за самим ПК (не по удалённому доступу): сеть пропадает на несколько секунд на каждое записанное значение. Скрипт:
       - берёт ПЕРВЫЙ пресет, который здесь применим (или тот, что указан: Test-NetworkPreset.ps1 -Apply -Preset network.preset-intel-wifi);
       - читает начальные значения средствами Windows (Get-NetAdapterAdvancedProperty) и сверяет с тем, что показал Mesura; расхождение: запись не начинается;
       - сохраняет снимок ВСЕХ настроек адаптеров (запасной путь назад: mesura network restore <id> --apply);
       - просит YES, применяет пресет, читает значения СНОВА средствами Windows, проверяет что адаптер снова "Up";
       - ждёт Enter (можно посмотреть на соединение), откатывает запуск и читает значения ещё раз: они должны стать прежними; сравнивает адаптеры со снимком.
     Пришлите network-preset-test.txt.

Что может пойти не так
  - Драйвер может отказать в значении: запуск завершится ошибкой, а изменённое Mesura вернёт.
  - Драйвер может принять значение и снова показывать старое: независимое чтение это поймает.
  - Адаптер может не подняться после записи: скрипт сообщит; откат попробует всё вернуть. Если сети нет: mesura network restore latest --apply, либо отключить и включить адаптер в Windows
    (Параметры > Сеть > Дополнительные параметры сети), либо перезагрузить ПК.
  - Если Mesura упадёт после записи, скрипт найдёт запись о запуске (история Mesura пишется до окончания) и откатит её сам.

Чего этот тест НЕ показывает
  Прирост FPS или снижение задержки. Это не измерялось и нигде не заявляется. Измерить можно Measure-NetworkLatency.ps1 (пинг до роутера, схема А-Б-А-Б-А-Б, только по проводу; на Wi-Fi разброс больше эффекта).

Что прислать
  network-preset-test.txt (и network-inventory.json с других ПК, если хотите добавить производителя). Файл inventory содержит названия адаптеров как в Windows (их можно переименовать), версии драйверов, GUID интерфейсов и значения параметров драйвера. Имён сетей, паролей, IP-адресов, MAC-адресов и серийных номеров в нём нет: Mesura их не читает.
'@
[IO.File]::WriteAllText((Join-Path $Out 'README-RU.txt'), ($readme -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($true)))
Write-Output "Built $Out"
Get-ChildItem $Out | Select-Object Name, Length | Format-Table -AutoSize | Out-String
