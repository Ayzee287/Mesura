@echo off
rem Collects the read-only Mesura 0.5.5 check into a zip on your Desktop. It asks Windows for administrator rights once (UAC): one of the checks needs them.
rem Read Collect-MesuraStableCheck.ps1 first if you like: it changes nothing and sends nothing.
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath cmd.exe -ArgumentList '/c \"\"%~f0\"\"'"
  exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-MesuraStableCheck.ps1"
echo.
pause
