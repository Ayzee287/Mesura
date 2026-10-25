@echo off
rem Starts the current build of Mesura (artifacts\current\Mesura\Mesura.exe, made by tools\publish.ps1).
rem Arguments are passed on, for example: run-current.cmd --no-elevate   (no administrator prompt)
rem                                     run-current.cmd --folder        (open the build's folder instead)
setlocal
set "BUILD=%~dp0..\artifacts\current"
if not exist "%BUILD%\Mesura\Mesura.exe" (
  echo There is no current build yet. Make one with:  powershell -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
  exit /b 1
)
if /i "%~1"=="--folder" (
  start "" explorer.exe "%BUILD%"
  exit /b 0
)
type "%BUILD%\BUILD.txt"
start "" "%BUILD%\Mesura\Mesura.exe" %*
