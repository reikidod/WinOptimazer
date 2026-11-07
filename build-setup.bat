@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================
echo  WinOptimizer - build installer
echo  Dir: %CD%
echo ============================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet not found. Install .NET 8 SDK:
  echo https://dotnet.microsoft.com/download
  goto :fail
)

echo [1/3] dotnet publish ...
if exist "publish" rd /s /q "publish"
dotnet publish -c Release -r win-x64 --self-contained false -o publish
if errorlevel 1 (
  echo ERROR: dotnet publish failed.
  goto :fail
)

if not exist "publish\WinOptimizer.exe" (
  echo ERROR: publish\WinOptimizer.exe not found.
  goto :fail
)
echo OK: publish\WinOptimizer.exe

set "ISCC="
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo ERROR: ISCC.exe not found. Install Inno Setup 6:
  echo https://jrsoftware.org/isdl.php
  echo publish folder is ready: %CD%\publish
  goto :fail
)

if not exist "installer\WinOptimizer.iss" (
  echo ERROR: installer\WinOptimizer.iss not found.
  goto :fail
)

if not exist "installer\LICENSE.txt" (
  echo ERROR: installer\LICENSE.txt not found.
  goto :fail
)

echo [2/3] ISCC: %ISCC%
echo [3/3] Compiling installer\WinOptimizer.iss ...
echo.

"%ISCC%" "%CD%\installer\WinOptimizer.iss"
if errorlevel 1 (
  echo ERROR: ISCC failed, code %ERRORLEVEL%
  goto :fail
)

echo.
echo ============================================
echo  DONE. Setup file:
dir /b "%CD%\dist\WinOptimizer-Setup-*.exe" 2>nul
echo  Folder: %CD%\dist
echo ============================================
echo.
pause
exit /b 0

:fail
echo.
echo Build failed.
pause
exit /b 1
