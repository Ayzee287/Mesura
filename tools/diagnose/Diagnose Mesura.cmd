@echo off
rem Collects a diagnostic report about why Mesura doesn't start, on the Desktop. Reads only; nothing is changed or sent.
rem Собирает отчёт о том, почему Mesura не запускается, на рабочий стол. Только чтение; ничего не меняется и не отправляется.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Diagnose-Mesura.ps1" -Folder "%~dp0."
echo.
pause
