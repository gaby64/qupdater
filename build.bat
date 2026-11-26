@echo off
setlocal
cd /d "%~dp0"
dotnet publish QUpdater.csproj -c Release -r win-x64 --self-contained true -o publish
if errorlevel 1 exit /b 1
copy /y "publish\qUpdater.exe" "qUpdater.exe" >nul
if errorlevel 1 exit /b 1
echo Build complete: qUpdater.exe
