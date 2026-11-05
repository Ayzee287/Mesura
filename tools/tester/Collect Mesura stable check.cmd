@echo off
rem Collects the read-only Mesura 0.6 check into a zip on your Desktop. It asks Windows for administrator rights once (UAC): one of the checks needs them.
rem Read Collect-MesuraStableCheck.ps1 first if you like: it changes nothing and sends nothing.
rem Elevation: "fltmc" succeeds only in an elevated process and, unlike "net session", does not need the Server service. (Possible cause of the endless console opening and closing seen on
rem 2026-11-04: with the Server service stopped "net session" fails even when elevated, so the old check relaunched forever; not confirmed.) The "elevated" argument allows one relaunch only.
if /i "%~1"=="elevated" goto elevated
fltmc >nul 2>&1
if %errorlevel% equ 0 goto elevated
powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath cmd.exe -ArgumentList '/c \"\"%~f0\" elevated\"'"
exit /b
:elevated
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-MesuraStableCheck.ps1"
echo.
pause
