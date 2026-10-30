@echo off
rem Mesura operations test, for a TEST machine or VM only. Without arguments it is a dry run and changes nothing.
rem To run the real test (administrator PowerShell, VM snapshot first):
rem   powershell -ExecutionPolicy Bypass -File "Test-MesuraOperations.ps1" -Run -ConfirmTestMachine
rem Тест операций Mesura только для ТЕСТОВОЙ машины или ВМ. Без аргументов ничего не меняет (пробный прогон).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-MesuraOperations.ps1" %*
echo.
pause
