@echo off
rem Run the PowerShell build script without changing the machine execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
