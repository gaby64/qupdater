# qUpdater – security-hardening draft

Fork of [celgost/qupdater](https://github.com/celgost/qupdater). Original attribution retained. No separate upstream license file was found; no new license is asserted. Confirm redistribution rights with the upstream author before distributing builds outside the GitHub fork.

Checks for stable qBittorrent updates at startup and from the Windows tray. SourceForge provides a version hint only; Windows Package Manager performs the update using the exact qBittorrent.qBittorrent package from the winget source and its installer hash verification. No direct installer execution, hash override, or forced qBittorrent termination. This design relies on WinGet and its configured source; it does not independently pin or verify an Authenticode publisher.

## Requirements and installation

- Windows x64, current Windows App Installer (WinGet), and an existing qBittorrent installation recognized by WinGet.
- For local compilation, install the .NET 10 SDK and run build.bat.
- For migration, exit the old updater first. The previous Program Files installation is not automatically removed; remove it manually if appropriate.
- Run install.bat as your normal user. Startup is registered for your account only.
- Exit via the tray and run uninstall.bat to remove the new installation. Logs are retained in %LOCALAPPDATA%\qUpdater.

## Validation before production use

Windows CI publishes a self-contained executable and checks stable release filename parsing. This is not an end-to-end installation test. Test on a VM: no-update startup, update refusal, unavailable WinGet/source, network failure, a tray-minimized qBittorrent that refuses to exit, canceled UAC, successful upgrade and installed-version verification, and install/uninstall without elevation. An update may be deferred until the requested version reaches WinGet. The WinGet installer may show its own UI and elevation prompt. No automatic application restart is attempted. WinGet source agreements may need to be accepted manually in a terminal first.
