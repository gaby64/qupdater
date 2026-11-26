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
        private readonly System.Windows.Forms.Timer timer = new();
        private bool useWinGet;
        private string variant;
        private bool restartAfterUpdate;
        private int checkHours;
        private const string SettingsPath = @"Software\qUpdater";
        private void LoadSettings()
        {
            using var settings = Registry.CurrentUser.OpenSubKey(SettingsPath);
            variant = settings?.GetValue("Variant") as string;
            restartAfterUpdate = (settings?.GetValue("Restart") as int? ?? 0) != 0;
            checkHours = settings?.GetValue("CheckHours") as int? ?? 24;
            if (checkHours != 0 && checkHours != 6 && checkHours != 24) checkHours = 24;
        }
        private void SaveSettings()
        {
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsPath);
            if (variant != null) settings.SetValue("Variant", variant);
            settings.SetValue("Restart", restartAfterUpdate ? 1 : 0);
            settings.SetValue("CheckHours", checkHours);
        }
        private void ConfigureTimer()
        {
            timer.Stop();
            if (checkHours > 0) { timer.Interval = checkHours * 3600000; timer.Start(); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--self-test")
                return SecureRelease.SelfTest() == 0 && NativeShutdown.SelfTest() == 0 ? 0 : 1;
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
            LoadSettings();
            SetupTrayIcon();
            timer.Tick += async (s, e) => await CheckForUpdates(false);
            ConfigureTimer();
            EventHandler onIdle = null;
            onIdle = async (sender, args) =>
            {
                Application.Idle -= onIdle;
                await CheckForUpdates(false);
            };
            Application.Idle += onIdle;
            Application.ApplicationExit += (sender, args) =>
            {
                timer.Dispose();
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
            contextMenu.Items.Add("Check for Updates (direct)", null, async (s, e) => await CheckForUpdates(true));
            contextMenu.Items.Add("Check using WinGet (standard build only)", null, async (s, e) =>
            {
                if (isChecking) return;
                useWinGet = true;
                try { await CheckForUpdates(true); } finally { useWinGet = false; }
            });
            var options = new ToolStripMenuItem("Settings");
            var builds = new ToolStripMenuItem("Installer variant");
            foreach (var item in new[] { ("standard", "Standard (current libtorrent)"), ("lt12", "libtorrent 1.2"), ("lt20", "libtorrent 2.0, if offered") })
            {
                var choice = new ToolStripMenuItem(item.Item2);
                builds.DropDownItems.Add(choice);
                choice.Click += (s, e) => { if (isChecking) return; variant = item.Item1; SaveSettings(); };
                builds.DropDownOpening += (s, e) => choice.Checked = variant == item.Item1;
            }
            options.DropDownItems.Add(builds);
            var restart = new ToolStripMenuItem("Restart qBittorrent after successful update");
            restart.Click += (s, e) => { if (isChecking) return; restartAfterUpdate = !restartAfterUpdate; SaveSettings(); };
            options.DropDownOpening += (s, e) => restart.Checked = restartAfterUpdate;
            options.DropDownItems.Add(restart);
            var interval = new ToolStripMenuItem("Automatic check interval");
            foreach (var hours in new[] { 0, 6, 24 })
            {
                var choice = new ToolStripMenuItem(hours == 0 ? "Startup only" : $"Every {hours} hours");
                interval.DropDownItems.Add(choice);
                choice.Click += (s, e) => { if (isChecking) return; checkHours = hours; SaveSettings(); ConfigureTimer(); };
                interval.DropDownOpening += (s, e) => choice.Checked = checkHours == hours;
            }
            options.DropDownItems.Add(interval);
            contextMenu.Items.Add(options);
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

        private static bool IsTargetProcess(Process process, string executable)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                return !process.HasExited && process.SessionId == self.SessionId &&
                    string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        private static NativeShutdown.Credentials PromptWebUiCredentials(string username, Uri endpoint)
        {
            using var form = new Form { Text = "qBittorrent WebUI authentication", Size = new Size(470, 250),
                StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false };
            form.Controls.Add(new Label { Text = $"qBittorrent requires authentication at {endpoint.GetLeftPart(UriPartial.Authority)}.\nCredentials are used only for this shutdown request.",
                AutoSize = true, Location = new Point(12, 12) });
            form.Controls.Add(new Label { Text = "Username", AutoSize = true, Location = new Point(12, 67) });
            var user = new TextBox { Text = username, Location = new Point(112, 64), Width = 325 };
            form.Controls.Add(user);
            form.Controls.Add(new Label { Text = "Password", AutoSize = true, Location = new Point(12, 105) });
            var password = new TextBox { UseSystemPasswordChar = true, Location = new Point(112, 102), Width = 325 };
            form.Controls.Add(password);
            var ok = new Button { Text = "Log in", DialogResult = DialogResult.OK, Location = new Point(257, 148), Enabled = user.TextLength > 0 };
            user.TextChanged += (s, e) => ok.Enabled = user.TextLength > 0;
            var skip = new Button { Text = "Skip", DialogResult = DialogResult.Cancel, Location = new Point(342, 148) };
            form.Controls.AddRange(new Control[] { ok, skip });
            form.AcceptButton = ok;
            form.CancelButton = skip;
            form.ActiveControl = password;
            if (form.ShowDialog() != DialogResult.OK) return null;
            var credentials = new NativeShutdown.Credentials(user.Text, password.Text);
            password.Clear();
            return credentials;
        }

        private static async Task<bool> WaitForQbittorrentExit(int seconds)
        {
            for (int attempt = 0; attempt < seconds; attempt++)
            {
                if (!QbittorrentRunning()) return true;
                await Task.Delay(1000);
            }
            return !QbittorrentRunning();
        }

        private static async Task<bool> CloseQbittorrentGracefully(string executable)
        {
            var processes = Process.GetProcessesByName("qbittorrent");
            try
            {
                if (processes.Length == 0) return true;
                // Never send shutdown requests to another session or installation.
                var targets = Array.FindAll(processes, process => IsTargetProcess(process, executable));
                if (targets.Length != processes.Length)
                {
                    Log("Another or inaccessible qBittorrent instance is running. Update deferred.");
                    return false;
                }

                var settings = NativeShutdown.ReadSettings();
                if (settings != null)
                {
                    foreach (var process in targets)
                    {
                        if (!IsTargetProcess(process, executable)) continue;
                        var result = await NativeShutdown.TryShutdownAsync(settings, process.Id, PromptWebUiCredentials);
                        Log("Native WebUI shutdown: " + result);
                        if (result == NativeShutdown.Outcome.Requested)
                        {
                            // qBittorrent may need time to save resume data and flush disk I/O.
                            // Do not send a second close request if native shutdown is already underway.
                            return await WaitForQbittorrentExit(60);
                        }
                    }
                }
                else Log("WebUI not enabled or normal-profile settings unavailable.");

                // Fall back to WM_CLOSE, which may only minimize the app to the tray.
                foreach (var process in targets)
                {
                    try { if (IsTargetProcess(process, executable)) process.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                return await WaitForQbittorrentExit(30);
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private async Task InstallWithWinGet(Version version)
        {
            var winget = FindWinGet();
            if (MessageBox.Show($"qBittorrent {version} is available. Install via Windows Package Manager?\n\n" +
                "qBittorrent must exit first. WinGet validates the installer hash and may show an installer or UAC prompt.",
                "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            // No silent source agreements or hash-check overrides.
            if (await RunWinGet(winget, true, "show", "--id", "qBittorrent.qBittorrent", "--exact", "--source", "winget",
                "--version", version.ToString(), "--disable-interactivity") != 0)
                throw new InvalidOperationException("This version is unavailable through WinGet, or its source needs setup. Run winget source update in a terminal and retry.");

            if (!await CloseQbittorrentGracefully(ResolveInstalledExecutable()))
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

        private string ResolveInstalledExecutable()
        {
            var exe = TryGetQbittorrentExePathFromRegistry() ?? ProbeCommonInstallLocationsAcrossDrives();
            if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe) ||
                !File.Exists(exe) || !string.Equals(Path.GetFileName(exe), "qbittorrent.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Could not locate an existing qBittorrent executable. Portable installations are not supported.");
            return Path.GetFullPath(exe);
        }

        private bool ChooseVariant()
        {
            if (variant is "standard" or "lt12" or "lt20") return true;
            using var form = new Form { Text = "Choose your existing qBittorrent build", Size = new Size(460, 210),
                StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false };
            form.Controls.Add(new Label { Text = "Select the libtorrent variant you currently use.\nCheck qBittorrent's About dialog if unsure.", AutoSize = true, Location = new Point(12, 12) });
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(12, 65), Width = 410 };
            combo.Items.AddRange(new object[] { "Standard (current libtorrent)", "libtorrent 1.2", "libtorrent 2.0 (if offered)" });
            form.Controls.Add(combo);
            var ok = new Button { Text = "Continue", DialogResult = DialogResult.OK, Location = new Point(245, 110), Enabled = false };
            combo.SelectedIndexChanged += (s, e) => ok.Enabled = combo.SelectedIndex >= 0;
            form.Controls.Add(ok);
            form.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(330, 110) });
            form.AcceptButton = ok;
            if (form.ShowDialog() != DialogResult.OK || combo.SelectedIndex < 0) return false;
            variant = new[] { "standard", "lt12", "lt20" }[combo.SelectedIndex];
            SaveSettings();
            return true;
        }

        private async Task InstallDirect(SecureRelease.Release release, string exe)
        {
            var version = release.Version;
            if (MessageBox.Show($"qBittorrent {version} ({release.Variant}) is available.\n\n" +
                "Download and verify the official installer? qBittorrent will remain running until verification completes.",
                "Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var progress = new Form { Text = "Downloading verified update", Size = new Size(490, 185),
                StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false, MaximizeBox = false, MinimizeBox = false };
            var label = new Label { AutoSize = true, Location = new Point(12, 12), Text = "Downloading..." };
            var bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(12, 42), Size = new Size(440, 24) };
            var cancel = new Button { Text = "Cancel", Location = new Point(375, 82) };
            cancel.Click += (s, e) => { cancellation.Cancel(); cancel.Enabled = false; };
            progress.Controls.AddRange(new Control[] { label, bar, cancel });
            var directory = Directory.CreateTempSubdirectory("qupdater-").FullName;
            var installer = Path.Combine(directory, release.FileName);
            var signature = installer + ".asc";
            try
            {
                progress.Show();
                await SecureRelease.DownloadAsync(httpClient, release.InstallerUri, installer, 512L * 1024 * 1024,
                    (received, total) =>
                    {
                        if (total > 0) { bar.Style = ProgressBarStyle.Continuous; bar.Value = (int)Math.Min(100, received * 100 / total); }
                        else bar.Style = ProgressBarStyle.Marquee;
                        label.Text = $"Downloaded {received / 1048576.0:F1} MB" + (total > 0 ? $" / {total / 1048576.0:F1} MB" : "");
                    }, cancellation.Token);
                label.Text = "Downloading detached OpenPGP signature...";
                bar.Style = ProgressBarStyle.Marquee;
                await SecureRelease.DownloadAsync(httpClient, release.SignatureUri, signature, 128 * 1024, null, cancellation.Token);

                // Preserve the Windows Internet-zone marker; never unblock the installer.
                File.WriteAllText(installer + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                // Lock the verified file against writes/deletion through launch and installation.
                using var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
                label.Text = "Verifying SHA-256 and pinned OpenPGP signature...";
                await Task.Run(() => SecureRelease.Verify(locked, signature, release.Sha256, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                cancel.Enabled = false;
                progress.Hide();
                Log($"Verified {release.FileName}; SHA-256 {release.Sha256}; signer {SecureRelease.Fingerprint}");

                var running = QbittorrentRunning();
                if (MessageBox.Show($"Verified the official signature and SHA-256 for {release.FileName}.\n\n" +
                    $"Install to {Path.GetDirectoryName(exe)}? qBittorrent must close. Windows may request elevation.",
                    "Install verified update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                if (!await CloseQbittorrentGracefully(exe) || QbittorrentRunning())
                {
                    MessageBox.Show("qBittorrent is still running. Exit from its tray menu and retry. No process was force-terminated.");
                    return;
                }
                var originalVersion = TryGetVersionFromExecutable(exe);
                if (!Version.TryParse(originalVersion, out var installedBefore) || installedBefore >= version)
                    throw new InvalidOperationException("The existing version changed during download. Check for updates again.");
                string installDirectory = Path.GetDirectoryName(exe);
                if (string.IsNullOrEmpty(installDirectory) || installDirectory == Path.GetPathRoot(installDirectory) ||
                    installDirectory.StartsWith(@"\\") || installDirectory.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("Unsupported installation directory.");

                label.Text = "Waiting for official installer to finish...";
                progress.Show();
                // NSIS /D must be last, with an unquoted absolute path, including spaces.
                using var process = Process.Start(new ProcessStartInfo(installer)
                {
                    UseShellExecute = true, WorkingDirectory = directory, Arguments = "/D=" + installDirectory
                }) ?? throw new InvalidOperationException("Could not start installer.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Installer exited with code {process.ExitCode}.");
                if (!File.Exists(exe) || !Version.TryParse(TryGetVersionFromExecutable(exe), out var installed) || installed != version)
                    throw new InvalidOperationException("Installer finished, but the expected version was not found in the original directory.");
                Log($"Installed and verified {installed} at {exe}");
                if (restartAfterUpdate && running && !QbittorrentRunning())
                {
                    try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = installDirectory })?.Dispose(); }
                    catch (Exception ex) { Log("Restart failed: " + ex.Message); MessageBox.Show("Update installed, but restart failed: " + ex.Message); }
                }
                MessageBox.Show($"qBittorrent {installed} installed successfully.", "Update complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                progress.Close();
                try { Directory.Delete(directory, true); }
                catch (IOException ex) { Log("Temporary cleanup failed: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Log("Temporary cleanup failed: " + ex.Message); }
            }
        }

        private async Task CheckForUpdates(bool manual)
        {
            if (isChecking) return;
            isChecking = true;
            try
            {
                var exe = ResolveInstalledExecutable();
                if (!Version.TryParse(TryGetVersionFromExecutable(exe), out var current))
                    throw new InvalidOperationException("Could not determine the installed qBittorrent version.");
                var releases = await SecureRelease.GetReleasesAsync(httpClient);
                var latest = releases[0].Version;
                Log($"Installed {current}; available {latest}.");
                if (latest <= current)
                {
                    if (manual) MessageBox.Show("qBittorrent is already up to date.");
                    return;
                }
                if (!ChooseVariant()) return;
                var selected = releases.Find(r => r.Variant == variant);
                if (selected == null) throw new InvalidOperationException("Your selected installer variant is not offered for this release. No variant will be substituted automatically.");
                if (useWinGet)
                {
                    if (variant != "standard") throw new InvalidOperationException("WinGet is only supported for the standard variant. Use direct updates instead.");
                    await InstallWithWinGet(selected.Version);
                }
                else await InstallDirect(selected, exe);
            }
            catch (OperationCanceledException)
            {
                Log("Download canceled or timed out.");
                MessageBox.Show("Download canceled or timed out. No unverified installer was started.");
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                if (manual || ex is not HttpRequestException)
                    MessageBox.Show(ex.Message, "qUpdater", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { isChecking = false; }
        }
    }
}
