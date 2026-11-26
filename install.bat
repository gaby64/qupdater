@echo off
setlocal
cd /d "%~dp0"
if not exist "qUpdater.exe" (
  echo Build or extract qUpdater.exe first.
  exit /b 1
)
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\qUpdater"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%" || exit /b 1
copy /y "qUpdater.exe" "%INSTALL_DIR%\qUpdater.exe" >nul || (
  echo Exit qUpdater from its tray menu before installing.
  exit /b 1
)
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v qUpdater /t REG_SZ /d "\"%INSTALL_DIR%\qUpdater.exe\"" /f || exit /b 1
start "" "%INSTALL_DIR%\qUpdater.exe"
echo Installed for the current user. No administrator privileges required.
