using System;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Http;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace QUpdater
{
    public class Program
    {
        private NotifyIcon trayIcon;
        private HttpClient httpClient;
        private bool isChecking = false;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test")
                return SelfTest();
            using var mutex = new Mutex(true, @"Local\qUpdater.SecurityHardened", out bool firstInstance);
            if (!firstInstance) return 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var program = new Program();
            program.Initialize();
            Application.Run();
            return 0;
        }

        public void Initialize()
        {
            httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            SetupTrayIcon();
            EventHandler onIdle = null;
            onIdle = async (sender, args) =>
            {
                Application.Idle -= onIdle;
                await CheckForUpdates();
            };
            Application.Idle += onIdle;
            Application.ApplicationExit += (sender, args) =>
            {
                trayIcon.Dispose();
                httpClient.Dispose();
            };
        }

        private void SetupTrayIcon()
        {
            // Load the icon from embedded resource
            Icon appIcon;
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("QUpdater.icon.ico"))
                {
                    if (stream != null)
                    {
                        appIcon = new Icon(stream);
                    }
                    else
                    {
                        appIcon = SystemIcons.Application;
                    }
                }
            }
            catch
            {
                appIcon = SystemIcons.Application;
            }

            trayIcon = new NotifyIcon()
            {
                Icon = appIcon,
                Visible = true,
                Text = "qUpdater"
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Check for Updates", null, async (s, e) => await CheckForUpdates());
            contextMenu.Items.Add("About", null, ShowAbout);
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Exit", null, (s, e) =>
            {
                if (isChecking)
                    MessageBox.Show("An update check or installation is in progress.");
                else Application.Exit();
            });

            trayIcon.ContextMenuStrip = contextMenu;
        }

        private void ShowAbout(object sender, EventArgs e)
        {
            MessageBox.Show(
                "qUpdater\n\n" +
                "This tool automatically checks for qBittorrent updates\n" +
                "on startup and helps you install them.\n\n" +
                "You can also manually check for updates using the tray menu.",
                "About qUpdater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }

        private string GetInstalledVersion()
        {
            // 1) Try direct version from dedicated qBittorrent keys (both 64/32-bit, HKLM/HKCU)
            string directVersion = TryReadQbittorrentVersionFromRegistry();

            // 2) Try to resolve the executable path from registry (App Paths / Uninstall entries)
            string exePath = TryGetQbittorrentExePathFromRegistry();
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var versionFromExe = TryGetVersionFromExecutable(exePath);
                if (!string.IsNullOrWhiteSpace(versionFromExe))
                    return versionFromExe;
            }

            // 3) Probe common install locations across ALL fixed drives (e.g., D:\Program Files\...)
            exePath = ProbeCommonInstallLocationsAcrossDrives();
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var versionFromExe = TryGetVersionFromExecutable(exePath);
                if (!string.IsNullOrWhiteSpace(versionFromExe))
                    return versionFromExe;
            }

            return directVersion;
        }

        private string TryReadQbittorrentVersionFromRegistry()
        {
            string[] keyPaths = new[]
            {
                "SOFTWARE\\qBittorrent",
                "SOFTWARE\\WOW6432Node\\qBittorrent"
            };

            // Check both HKLM and HKCU in both 64-bit and 32-bit views
            RegistryHive[] hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        {
                            foreach (var path in keyPaths)
                            {
                                using (var key = baseKey.OpenSubKey(path))
                                {
                                    if (key == null) continue;
                                    var version = key.GetValue("Version") as string;
                                    if (!string.IsNullOrWhiteSpace(version))
                                        return version;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private string TryGetQbittorrentExePathFromRegistry()
        {
            // Prefer App Paths which typically holds the absolute path to the executable
            string appPath = TryReadAppPaths("qbittorrent.exe");
            if (!string.IsNullOrWhiteSpace(appPath))
                return appPath;

            // Fallback: search Uninstall entries for DisplayIcon or InstallLocation
            var uninstallProbe = TryReadFromUninstall();
            if (!string.IsNullOrWhiteSpace(uninstallProbe) && File.Exists(uninstallProbe))
                return uninstallProbe;

            return null;
        }

        private string TryReadAppPaths(string exeName)
        {
            string keyPath = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths\\{exeName}";
            RegistryHive[] hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (var key = baseKey.OpenSubKey(keyPath))
                        {
                            if (key == null) continue;
                            var defaultValue = key.GetValue(null) as string; // (Default)
                            if (!string.IsNullOrWhiteSpace(defaultValue) && File.Exists(defaultValue))
                                return defaultValue;

                            var pathValue = key.GetValue("Path") as string;
                            if (!string.IsNullOrWhiteSpace(pathValue))
                            {
                                var candidate = Path.Combine(pathValue, exeName);
                                if (File.Exists(candidate))
                                    return candidate;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private string TryReadFromUninstall()
        {
            string[] uninstallRoots = new[]
            {
                "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                "SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
            };

            RegistryHive[] hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    foreach (var root in uninstallRoots)
                    {
                        try
                        {
                            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            using (var uninstallKey = baseKey.OpenSubKey(root))
                            {
                                if (uninstallKey == null) continue;
                                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                                {
                                    using (var subKey = uninstallKey.OpenSubKey(subKeyName))
                                    {
                                        if (subKey == null) continue;
                                        var displayName = subKey.GetValue("DisplayName") as string;
                                        if (string.IsNullOrWhiteSpace(displayName) || displayName.IndexOf("qbittorrent", StringComparison.OrdinalIgnoreCase) < 0)
                                            continue;

                                        // Found a qBittorrent entry; try to get version if needed
                                        var displayVersion = subKey.GetValue("DisplayVersion") as string;
                                        if (!string.IsNullOrWhiteSpace(displayVersion))
                                        {
                                            // Return version via separate call if needed, but we mainly want path
                                        }

                                        // Prefer InstallLocation when available
                                        var installLocation = subKey.GetValue("InstallLocation") as string;
                                        if (!string.IsNullOrWhiteSpace(installLocation))
                                        {
                                            var candidate = Path.Combine(installLocation, "qbittorrent.exe");
                                            if (File.Exists(candidate)) return candidate;
                                        }

                                        // Next try DisplayIcon which often points directly to exe (may include ,0 suffix)
                                        var displayIcon = subKey.GetValue("DisplayIcon") as string;
                                        if (!string.IsNullOrWhiteSpace(displayIcon))
                                        {
                                            var cleaned = displayIcon;
                                            var commaIdx = cleaned.IndexOf(',');
                                            if (commaIdx >= 0) cleaned = cleaned.Substring(0, commaIdx);
                                            cleaned = cleaned.Trim('"');
                                            if (File.Exists(cleaned) && string.Equals(Path.GetFileName(cleaned), "qbittorrent.exe", StringComparison.OrdinalIgnoreCase)) return cleaned;
                                        }

                                        // As a final hint, UninstallString may live next to the exe
                                        var uninstallString = subKey.GetValue("UninstallString") as string;
                                        if (!string.IsNullOrWhiteSpace(uninstallString))
                                        {
                                            var pathCandidate = uninstallString.Trim('"');
                                            try
                                            {
                                                var directory = Path.GetDirectoryName(pathCandidate);
                                                if (!string.IsNullOrWhiteSpace(directory))
                                                {
                                                    var exeCandidate = Path.Combine(directory, "qbittorrent.exe");
                                                    if (File.Exists(exeCandidate)) return exeCandidate;
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            return null;
        }

        private string ProbeCommonInstallLocationsAcrossDrives()
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                        var root = drive.RootDirectory.FullName;

                        string[] relativePaths = new[]
                        {
                            Path.Combine("Program Files", "qBittorrent", "qbittorrent.exe"),
                            Path.Combine("Program Files (x86)", "qBittorrent", "qbittorrent.exe")
                        };

                        foreach (var rel in relativePaths)
                        {
                            var candidate = Path.Combine(root, rel);
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }

        private string TryGetVersionFromExecutable(string exePath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(exePath);
                return $"{versionInfo.FileMajorPart}.{versionInfo.FileMinorPart}.{versionInfo.FileBuildPart}";
            }
            catch { }
            return null;
        }

        private static void Log(string message)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qUpdater");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "updater.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static Version ParseReleaseFilename(string filename)
        {
            var match = Regex.Match(filename ?? "", @"(?:^|/)qbittorrent_(\d+\.\d+\.\d+)_x64_setup\.exe$",
                RegexOptions.CultureInvariant);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
                throw new InvalidDataException("Unrecognized stable x64 release metadata.");
            return version;
        }

        private static int SelfTest()
        {
            try
            {
                if (ParseReleaseFilename("/qbittorrent/qbittorrent-5.1.2/qbittorrent_5.1.2_x64_setup.exe") != new Version(5, 1, 2))
                    return 1;
                foreach (var bad in new[] { "", "qbittorrent_5.1.2_x64_setup.exe.bak", "qbittorrent_5.1.2_beta_x64_setup.exe", "qbittorrent_5.1.2_x86_setup.exe" })
                {
                    try { ParseReleaseFilename(bad); return 1; }
                    catch (InvalidDataException) { }
                }
                return 0;
            }
            catch { return 1; }
        }

        private async Task<Version> GetLatestVersion()
        {
            // SourceForge supplies a version hint, never an executable or download URL.
            using var response = await httpClient.GetAsync(
                "https://sourceforge.net/projects/qbittorrent/best_release.json");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ParseReleaseFilename(document.RootElement.GetProperty("platform_releases")
                .GetProperty("windows").GetProperty("filename").GetString());
        }

        private static string FindWinGet()
        {
            // Avoid finding an arbitrary executable via PATH or the working directory.
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(path))
                throw new FileNotFoundException("Windows App Installer (WinGet) is required. Install or repair it through Microsoft Store.");
            return path;
        }

        private static async Task<int> RunWinGet(string executable, bool preflight, params string[] arguments)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start WinGet.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (preflight)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    throw new TimeoutException("WinGet package lookup timed out.");
                }
            }
            else
            {
                // Never forcibly interrupt an installer which may be writing application files.
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(output, error);
            Log($"WinGet exit code: 0x{unchecked((uint)process.ExitCode):X8}");
            return process.ExitCode;
        }

        private static bool QbittorrentRunning()
        {
            var processes = Process.GetProcessesByName("qbittorrent");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private static async Task<bool> CloseQbittorrentGracefully()
        {
            var processes = Process.GetProcessesByName("qbittorrent");
            try
            {
                foreach (var process in processes)
                {
                    try { if (!process.HasExited) process.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    if (!QbittorrentRunning()) return true;
                    await Task.Delay(1000);
                }
                return !QbittorrentRunning();
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private async Task DownloadAndInstall(Version version)
        {
            var winget = FindWinGet();
            if (MessageBox.Show($"qBittorrent {version} is available. Install via Windows Package Manager?\n\n" +
                "qBittorrent must exit first. WinGet validates the installer hash and may show an installer or UAC prompt.",
                "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            // No silent source agreements or hash-check overrides.
            if (await RunWinGet(winget, true, "show", "--id", "qBittorrent.qBittorrent", "--exact", "--source", "winget",
                "--version", version.ToString(), "--disable-interactivity") != 0)
                throw new InvalidOperationException("This version is unavailable through WinGet, or its source needs setup. Run winget source update in a terminal and retry.");

            if (!await CloseQbittorrentGracefully())
            {
                MessageBox.Show("qBittorrent is still running. Exit using its tray menu and try again. No qBittorrent process was force-terminated.",
                    "Update deferred", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (QbittorrentRunning()) throw new InvalidOperationException("qBittorrent restarted; update deferred.");

            using var progress = new Form { Text = "Installing qBittorrent update", Size = new Size(450, 120),
                StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false, MaximizeBox = false, MinimizeBox = false };
            progress.Controls.Add(new Label { Text = "WinGet is downloading and installing the verified package...", AutoSize = true,
                Location = new Point(10, 10) });
            progress.Controls.Add(new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(10, 40), Size = new Size(400, 20) });
            progress.Show();
            try
            {
                int result = await RunWinGet(winget, false, "upgrade", "--id", "qBittorrent.qBittorrent", "--exact",
                    "--source", "winget", "--version", version.ToString(), "--interactive", "--disable-interactivity");
                if (result != 0) throw new InvalidOperationException($"WinGet failed (0x{unchecked((uint)result):X8}). Consult WinGet diagnostics.");
                var exe = TryGetQbittorrentExePathFromRegistry() ?? ProbeCommonInstallLocationsAcrossDrives();
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) ||
                    !Version.TryParse(TryGetVersionFromExecutable(exe), out var installed) || installed < version)
                    throw new InvalidOperationException("WinGet reported success, but the installed executable version could not be verified.");
                Log($"Verified installed version {installed}.");
                MessageBox.Show($"qBittorrent {installed} installed successfully.", "Update complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { progress.Close(); }
        }

        private async Task CheckForUpdates()
        {
            if (isChecking) return;
            isChecking = true;
            try
            {
                var currentVersion = GetInstalledVersion();
                if (!Version.TryParse(currentVersion, out var current))
                    throw new InvalidOperationException("Could not determine the installed qBittorrent version.");
                // Normalize to the three-part version used by release metadata.
                current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
                var latest = await GetLatestVersion();
                Log($"Installed {current}; available {latest}.");
                if (latest > current) await DownloadAndInstall(latest);
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                MessageBox.Show(ex.Message, "qUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { isChecking = false; }
        }
    }
}
