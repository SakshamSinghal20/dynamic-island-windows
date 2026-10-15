@echo off
if not exist "%~dp0dist\Island\Island.exe" (
  echo Run build.ps1 -Publish first.
  pause
  exit /b 1
)
start "" "%~dp0dist\Island\Island.exe" --open
