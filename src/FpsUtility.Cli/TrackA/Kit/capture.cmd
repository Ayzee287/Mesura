@echo off
setlocal
rem FPS Utility - Track A capture kit. Internal engineering validation of a known PC:
rem it reads this PC, changes nothing and sends nothing. See OPERATOR.txt.
rem Usage: capture.cmd [output folder]    (default: the captures folder next to this file)

set "KIT=%~dp0"
set "OUT=%~1"
if "%OUT%"=="" set "OUT=%KIT%captures"
rem A trailing backslash (a dragged drive, D:\) would escape the closing quote below.
if "%OUT:~-1%"=="\" set "OUT=%OUT:~0,-1%"

rem Read-only: the Windows build, shown first so it is on screen even if the kit can't start (the CET case below).
set "BUILD=?"
set "UBR=?"
for /f "tokens=3" %%v in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v CurrentBuildNumber 2^>nul ^| find "CurrentBuildNumber"') do set "BUILD=%%v"
for /f "tokens=3" %%v in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v UBR 2^>nul ^| find "UBR"') do set /a "UBR=%%v"
echo Windows build %BUILD%.%UBR%
echo.

"%KIT%FpsUtility.Cli.exe" track-a capture --out "%OUT%"
set "CODE=%ERRORLEVEL%"
echo.

if "%CODE%"=="0" echo Done: the shareable package is ready. Hand over its shareable and evidence folders, never private.
if "%CODE%"=="1" echo The capture stopped (see the message above). Nothing unverified was written to shareable.
if "%CODE%"=="2" echo The kit refused its arguments or its output folder (see above).
if "%CODE%"=="3" echo This Windows isn't supported (see above), so nothing was captured. Record that, with the build above, in the PC's note.
if "%CODE%"=="4" echo The privacy verification failed, so that session has no shareable package. See privacy-failure.json in its folder. (In pci-e-idle-load the idle session may still have been written.)
if "%CODE%"=="6" echo A package was written, but it can't be accepted as it is (see the reasons above).
echo 0 1 2 3 4 6 | find "%CODE%" >nul || (
  echo The kit stopped with code %CODE% before finishing.
  echo On Windows 10 22H2 this is usually the .NET runtime refusing to start on a CET-capable processor
  echo because Windows lacks recent updates. Nothing was captured. Record the build shown above in the PC's note,
  echo then install the current Windows updates and run the kit again.
)
echo.
pause
