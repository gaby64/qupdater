using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QUpdater
{
    internal static class NativeShutdown
    {
        internal sealed record Settings(int Port, bool Https, string Username);
        internal sealed record Credentials(string Username, string Password);
        internal enum Outcome { Unavailable, AuthenticationSkipped, AuthenticationFailed, Requested }
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int MIB_TCP_STATE_ESTAB = 5;

        [DllImport("iphlpapi.dll", SetLastError = false)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);

        internal static Settings ReadSettings()
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "qBittorrent", "qBittorrent.ini");
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
                // Only parse the WebUI settings needed for transport and the optional username.
                // Never read the password hash or automatically reuse an API key.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return ParseSettings(reader);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static Settings ParseSettings(TextReader reader)
        {
            bool preferences = false;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    preferences = string.Equals(line, "[Preferences]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!preferences) continue;
                int separator = line.IndexOf('=');
                if (separator < 1) continue;
                string key = line[..separator].Trim().Replace('\\', '/');
                if (key is "WebUI/Enabled" or "WebUI/Port" or "WebUI/HTTPS/Enabled" or "WebUI/Username")
                {
                    if (values.ContainsKey(key)) return null;
                    values[key] = line[(separator + 1)..].Trim();
                }
            }
            if (!values.TryGetValue("WebUI/Enabled", out var enabled) ||
                !bool.TryParse(enabled, out bool on) || !on) return null;
            int port = 8080;
            if (values.TryGetValue("WebUI/Port", out var portText) &&
                (!int.TryParse(portText, out port) || port < 1 || port > 65535)) return null;
            bool https = false;
            if (values.TryGetValue("WebUI/HTTPS/Enabled", out var httpsText) &&
                !bool.TryParse(httpsText, out https)) return null;
            string username = values.TryGetValue("WebUI/Username", out var name) ? name.Trim('"') : "admin";
            // Unusual Qt-escaped usernames may be edited in the login prompt.
            return new Settings(port, https, username);
        }

        // MIB_TCPROW_OWNER_PID contains six DWORDs. TCP ports are network-byte-order
        // in the low two bytes; IPv4 addresses are in network byte order in memory.
        private static int Port(int raw) => ((raw & 0xff) << 8) | ((raw >> 8) & 0xff);
        private static bool AddressMatches(IntPtr table, int offset, IPAddress expected, bool ipv6)
        {
            byte[] bytes = new byte[ipv6 ? 16 : 4];
            Marshal.Copy(IntPtr.Add(table, offset), bytes, 0, bytes.Length);
            var actual = new IPAddress(bytes);
            if (actual.IsIPv4MappedToIPv6) actual = actual.MapToIPv4();
            return actual.Equals(expected);
        }

        private static bool OwnsConnectedPeer(IPAddress loopback, int serverPort, int clientPort, int expectedPid)
        {
            if (loopback.Equals(IPAddress.Loopback) &&
                MatchTcpTable(AF_INET, loopback, serverPort, clientPort, expectedPid)) return true;
            // Dual-stack listeners may report IPv4 clients as IPv4-mapped IPv6 addresses.
            return MatchTcpTable(AF_INET6, loopback, serverPort, clientPort, expectedPid);
        }

        private static bool MatchTcpTable(int family, IPAddress loopback, int serverPort, int clientPort, int expectedPid)
        {
            int size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != ERROR_INSUFFICIENT_BUFFER) return false;
            bool ipv6 = family == AF_INET6;
            int rowSize = ipv6 ? 56 : 24;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (size < 4 || size > 64 * 1024 * 1024) return false;
                int allocated = size;
                IntPtr table = Marshal.AllocHGlobal(allocated);
                try
                {
                    result = GetExtendedTcpTable(table, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (result == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (result != 0) return false;
                    int count = Marshal.ReadInt32(table);
                    if (count < 0 || count > (allocated - 4) / rowSize) return false;
                    for (int i = 0; i < count; i++)
                    {
                        int offset = 4 + i * rowSize;
                        if (Marshal.ReadInt32(table, offset + (ipv6 ? 48 : 0)) == MIB_TCP_STATE_ESTAB &&
                            Marshal.ReadInt32(table, offset + (ipv6 ? 52 : 20)) == expectedPid &&
                            Port(Marshal.ReadInt32(table, offset + (ipv6 ? 20 : 8))) == serverPort &&
                            Port(Marshal.ReadInt32(table, offset + (ipv6 ? 44 : 16))) == clientPort &&
                            AddressMatches(table, offset + (ipv6 ? 0 : 4), loopback, ipv6) &&
                            AddressMatches(table, offset + (ipv6 ? 24 : 12), loopback, ipv6))
                            return true;
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            return false;
        }

        private static HttpClient CreateClient(Settings settings, int processId)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };
            handler.ConnectCallback = async (context, token) =>
            {
                // Connect only to literal loopback addresses, without DNS or proxies.
                if (context.DnsEndPoint.Host != "localhost" || context.DnsEndPoint.Port != settings.Port)
                    throw new IOException("Unexpected WebUI endpoint.");
                foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, settings.Port), token).ConfigureAwait(false);
                        int clientPort = ((IPEndPoint)socket.LocalEndPoint).Port;
                        // Verify server-side connection ownership before transmitting HTTP or TLS data.
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            if (OwnsConnectedPeer(address, settings.Port, clientPort, processId))
                                return new NetworkStream(socket, ownsSocket: true);
                            await Task.Delay(25, token).ConfigureAwait(false);
                        }
                        socket.Dispose();
                    }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new IOException("Local WebUI connection is not owned by the expected qBittorrent process, or is unavailable.");
            };
            // Default TLS certificate validation remains active, including hostname validation.
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        }

        internal static async Task<Outcome> TryShutdownAsync(Settings settings, int processId,
            Func<string, Uri, Credentials> prompt)
        {
            using var client = CreateClient(settings, processId);
            var endpoint = new UriBuilder(settings.Https ? "https" : "http", "localhost", settings.Port).Uri;
            return await RequestShutdownAsync(client, endpoint, settings.Username, prompt);
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string route)
        {
            var request = new HttpRequestMessage(method, new Uri(endpoint, route));
            request.Headers.Referrer = endpoint;
            request.Headers.TryAddWithoutValidation("Origin", endpoint.GetLeftPart(UriPartial.Authority));
            return request;
        }

        private static bool NeedsLogin(HttpStatusCode status) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        private static async Task<(HttpStatusCode Status, string Body)> ReadVersionAsync(HttpClient client, Uri endpoint)
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, "/api/v2/app/version");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static async Task<Outcome> RequestShutdownAsync(HttpClient client, Uri endpoint, string username,
            Func<string, Uri, Credentials> prompt)
        {
            bool loggedIn = false;
            bool shutdownSent = false;
            try
            {
                var probe = await ReadVersionAsync(client, endpoint);
                if (NeedsLogin(probe.Status))
                {
                    var credentials = prompt(username, endpoint);
                    if (credentials == null) return Outcome.AuthenticationSkipped;
                    using var login = CreateRequest(HttpMethod.Post, endpoint, "/api/v2/auth/login");
                    login.Content = new FormUrlEncodedContent(new[]
                    {
                        new KeyValuePair<string,string>("username", credentials.Username),
                        new KeyValuePair<string,string>("password", credentials.Password)
                    });
                    using var result = await client.SendAsync(login);
                    // qBittorrent returns HTTP 200 for both correct and incorrect passwords.
                    // Never retry a failed login automatically, to avoid account/IP bans.
                    if (result.StatusCode != HttpStatusCode.OK ||
                        !string.Equals((await result.Content.ReadAsStringAsync()).Trim(), "Ok.", StringComparison.Ordinal))
                        return Outcome.AuthenticationFailed;
                    loggedIn = true;
                    probe = await ReadVersionAsync(client, endpoint);
                }
                if (probe.Status != HttpStatusCode.OK ||
                    !Regex.IsMatch(probe.Body.Trim(), @"^v?\d+\.\d+\.\d+(?:[.\w+-]*)$", RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)))
                    return Outcome.Unavailable;

                using var shutdown = CreateRequest(HttpMethod.Post, endpoint, "/api/v2/app/shutdown");
                shutdown.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string,string>>());
                // A successful shutdown may drop the HTTP connection before a response arrives.
                shutdownSent = true;
                using var response = await client.SendAsync(shutdown);
                if (response.IsSuccessStatusCode) return Outcome.Requested;
                shutdownSent = false;
                return Outcome.Unavailable;
            }
            catch (HttpRequestException) { return shutdownSent ? Outcome.Requested : Outcome.Unavailable; }
            catch (OperationCanceledException) { return shutdownSent ? Outcome.Requested : Outcome.Unavailable; }
            catch (IOException) { return shutdownSent ? Outcome.Requested : Outcome.Unavailable; }
            finally
            {
                if (loggedIn && !shutdownSent)
                {
                    try
                    {
                        using var logout = CreateRequest(HttpMethod.Post, endpoint, "/api/v2/auth/logout");
                        logout.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string,string>>());
                        using var response = await client.SendAsync(logout);
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                }
            }
        }

        // Offline deterministic HTTP tests: no real qBittorrent instance or password is used.
        private sealed class MockHandler : HttpMessageHandler
        {
            private readonly Queue<(HttpMethod Method, string Path, HttpStatusCode Status, string Body)> steps;
            internal int Remaining => steps.Count;
            internal MockHandler(params (HttpMethod, string, HttpStatusCode, string)[] expected) => steps = new(expected);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var expected = steps.Dequeue();
                if (request.Method != expected.Method || request.RequestUri.AbsolutePath != expected.Path ||
                    request.RequestUri.Host != "localhost" || request.RequestUri.Port != 8080 ||
                    request.Headers.Referrer?.Host != "localhost")
                    throw new InvalidDataException("Unexpected mock WebUI request.");
                return Task.FromResult(new HttpResponseMessage(expected.Status) { Content = new StringContent(expected.Body) });
            }
        }

        private static async Task<bool> TestConnectionOwnership(IPAddress address)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(address, 0);
            listener.Start();
            int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(address.AddressFamily);
            var accept = listener.AcceptTcpClientAsync(timeout.Token);
            await client.ConnectAsync(address, serverPort, timeout.Token).ConfigureAwait(false);
            using var peer = await accept.ConfigureAwait(false);
            int clientPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (OwnsConnectedPeer(address, serverPort, clientPort, Environment.ProcessId))
                    return !OwnsConnectedPeer(address, serverPort, clientPort, 0);
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
            return false;
        }

        internal static int SelfTest()
        {
            try
            {
                var settings = ParseSettings(new StringReader("[Preferences]\nWebUI\\Enabled=true\nWebUI\\Port=8181\nWebUI\\HTTPS\\Enabled=true\nWebUI\\Username=example"));
                if (settings is not { Port: 8181, Https: true, Username: "example" }) return 1;
                if (ParseSettings(new StringReader("[Preferences]\nWebUI\\Enabled=false")) != null ||
                    ParseSettings(new StringReader("[Preferences]\nWebUI\\Enabled=true\nWebUI\\Port=65536")) != null ||
                    ParseSettings(new StringReader("[Other]\nWebUI\\Enabled=true")) != null ||
                    ParseSettings(new StringReader("[Preferences]\nWebUI\\Enabled=true\nWebUI\\Port=8080\nWebUI\\Port=8081")) != null ||
                    Port(0x901f) != 8080) return 1;
                if (!TestConnectionOwnership(IPAddress.Loopback).GetAwaiter().GetResult()) return 1;
                if (Socket.OSSupportsIPv6 && !TestConnectionOwnership(IPAddress.IPv6Loopback).GetAwaiter().GetResult()) return 1;
                var endpoint = new Uri("http://localhost:8080/");
                using var noAuth = new MockHandler(
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.OK, "v5.3.1"),
                    (HttpMethod.Post, "/api/v2/app/shutdown", HttpStatusCode.OK, ""));
                using var client1 = new HttpClient(noAuth);
                if (RequestShutdownAsync(client1, endpoint, "admin", (_, _) => throw new InvalidOperationException())
                    .GetAwaiter().GetResult() != Outcome.Requested || noAuth.Remaining != 0) return 1;
                using var auth = new MockHandler(
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.Forbidden, ""),
                    (HttpMethod.Post, "/api/v2/auth/login", HttpStatusCode.OK, "Ok."),
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.OK, "v5.3.1"),
                    (HttpMethod.Post, "/api/v2/app/shutdown", HttpStatusCode.OK, ""));
                using var client2 = new HttpClient(auth);
                if (RequestShutdownAsync(client2, endpoint, "admin", (_, _) => new Credentials("test", "test"))
                    .GetAwaiter().GetResult() != Outcome.Requested || auth.Remaining != 0) return 1;
                using var rejected = new MockHandler(
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.Forbidden, ""),
                    (HttpMethod.Post, "/api/v2/auth/login", HttpStatusCode.OK, "Fails."));
                using var client3 = new HttpClient(rejected);
                if (RequestShutdownAsync(client3, endpoint, "admin", (_, _) => new Credentials("test", "wrong"))
                    .GetAwaiter().GetResult() != Outcome.AuthenticationFailed || rejected.Remaining != 0) return 1;
                using var skipped = new MockHandler(
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.Unauthorized, ""));
                using var client4 = new HttpClient(skipped);
                if (RequestShutdownAsync(client4, endpoint, "admin", (_, _) => null)
                    .GetAwaiter().GetResult() != Outcome.AuthenticationSkipped || skipped.Remaining != 0) return 1;
                using var unexpected = new MockHandler(
                    (HttpMethod.Get, "/api/v2/app/version", HttpStatusCode.OK, "<html>Not qBittorrent</html>"));
                using var client5 = new HttpClient(unexpected);
                if (RequestShutdownAsync(client5, endpoint, "admin", (_, _) => null)
                    .GetAwaiter().GetResult() != Outcome.Unavailable || unexpected.Remaining != 0) return 1;
                return 0;
            }
            catch { return 1; }
        }
    }
}
