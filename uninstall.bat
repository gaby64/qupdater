@echo off
setlocal
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\qUpdater"
if exist "%INSTALL_DIR%\qUpdater.exe" (
  del "%INSTALL_DIR%\qUpdater.exe" || (
    echo Exit qUpdater from its tray menu before uninstalling.
    exit /b 1
  )
)
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v qUpdater >nul 2>&1
if not errorlevel 1 (
  reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v qUpdater /f || exit /b 1
)
if exist "%INSTALL_DIR%" rd "%INSTALL_DIR%"
echo Removed current-user qUpdater. Diagnostic logs are retained.
