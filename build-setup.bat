@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1"
if errorlevel 1 (echo. & echo Build failed. & pause & exit /b 1)
pause
