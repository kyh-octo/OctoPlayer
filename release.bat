@echo off
rem OctoPlayer one-click release
rem   build installer from the latest git commit -> GitHub release -> update octo-brain.com
rem   options are passed through to installer\release.ps1 (e.g. release.bat -SkipWebsite)
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\release.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (echo [FAILED] exit code %RC%) else (echo [DONE])
pause
exit /b %RC%
