using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace QUpdater
{
    internal static class SecureRelease
    {
        internal const string Fingerprint = "D8F3DA77AAC6741053599C136E4A2D025B7CC9A2";
        internal sealed record Release(Version Version, string Variant, string FileName, string Sha256, Uri InstallerUri, Uri SignatureUri);
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

        internal static List<Release> ParsePage(string html)
        {
            // Scope to Windows only; other OS tables repeat the same version numbers.
            var section = Regex.Match(html, @"<h3\s+id=""windows""[^>]*>([\s\S]*?)(?=<h3\b)", RegexOptions.IgnoreCase, RegexTimeout);
            if (!section.Success) throw new InvalidDataException("Official Windows release section not found.");
            var rows = Regex.Matches(section.Groups[1].Value, @"<tr\b[^>]*>\s*<td>\s*(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?:\s+\((?<variant>lt12|lt20)\))?\s*</td>\s*<td\b[^>]*>\s*<code>\s*(?<hash>[0-9a-fA-F]{64})\s*</code>\s*</td>\s*</tr>", RegexOptions.IgnoreCase, RegexTimeout);
            var releases = new List<Release>();
            foreach (Match row in rows)
            {
                var version = Version.Parse(row.Groups["version"].Value);
                var variant = row.Groups["variant"].Success ? row.Groups["variant"].Value.ToLowerInvariant() : "standard";
                if (releases.Exists(x => x.Variant == variant)) throw new InvalidDataException("Ambiguous release checksum.");
                string filename = $"qbittorrent_{version}{(variant == "standard" ? "" : "_" + variant)}_x64_setup.exe";
                string path = $"qbittorrent-win32/qbittorrent-{version}/{filename}";
                // Require the exact installer and signature links in the same Windows section.
                string official = "https://sourceforge.net/projects/qbittorrent/files/" + path;
                if (!section.Groups[1].Value.Contains("href=\"" + official + "/download\"", StringComparison.Ordinal) ||
                    !section.Groups[1].Value.Contains("href=\"" + official + ".asc/download\"", StringComparison.Ordinal))
                    throw new InvalidDataException("Official installer or signature link missing.");
                string mirror = "https://downloads.sourceforge.net/project/qbittorrent/" + path;
                releases.Add(new Release(version, variant, filename, row.Groups["hash"].Value.ToUpperInvariant(), new Uri(mirror), new Uri(mirror + ".asc")));
            }
            var standard = releases.Find(r => r.Variant == "standard");
            if (standard == null || releases.Exists(r => r.Version != standard.Version))
                throw new InvalidDataException("Missing or inconsistent Windows release metadata.");
            return releases;
        }

        internal static bool Allowed(Uri uri, bool mirror)
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                return false;
            var host = uri.IdnHost;
            if (!mirror) return host == "www.qbittorrent.org" || host == "qbittorrent.org";
            return (host == "downloads.sourceforge.net" || host.EndsWith(".dl.sourceforge.net", StringComparison.Ordinal) ||
                host == "dl.sourceforge.net") && uri.AbsolutePath.StartsWith("/project/qbittorrent/", StringComparison.Ordinal);
        }

        private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Uri uri, bool mirror, CancellationToken token)
        {
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                if (!Allowed(uri, mirror)) throw new InvalidDataException("Blocked untrusted download URL: " + uri.Host);
                var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null) throw new InvalidDataException("Redirect without a destination.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                try { response.EnsureSuccessStatusCode(); return response; }
                catch { response.Dispose(); throw; }
            }
            throw new InvalidDataException("Too many redirects.");
        }

        internal static async Task<List<Release>> GetReleasesAsync(HttpClient client)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var response = await OpenAsync(client, new Uri("https://www.qbittorrent.org/download"), false, timeout.Token);
            using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            await CopyBounded(source, buffer, 2 * 1024 * 1024, response.Content.Headers.ContentLength ?? -1, null, timeout.Token);
            return ParsePage(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        internal static async Task DownloadAsync(HttpClient client, Uri uri, string destination, long limit,
            Action<long,long> progress, CancellationToken token)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await OpenAsync(client, uri, true, token);
                    using var source = await response.Content.ReadAsStreamAsync(token);
                    using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await CopyBounded(source, target, limit, response.Content.Headers.ContentLength ?? -1, progress, token);
                    return;
                }
                catch (HttpRequestException) when (attempt == 0 && !token.IsCancellationRequested)
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    await Task.Delay(1000, token);
                }
            }
        }

        private static async Task CopyBounded(Stream source, Stream target, long limit, long total, Action<long,long> progress, CancellationToken token)
        {
            if (total > limit || total == 0) throw new InvalidDataException("Unexpected download size.");
            byte[] buffer = new byte[65536];
            long received = 0;
            int count;
            long lastProgress = -1;
            while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                received += count;
                if (received > limit || (total >= 0 && received > total)) throw new InvalidDataException("Download size exceeded.");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
                if (progress != null && (received - lastProgress > 262144 || received == total))
                { progress(received, total); lastProgress = received; }
            }
            if (received == 0 || (total >= 0 && received != total)) throw new InvalidDataException("Incomplete download.");
            progress?.Invoke(received, total);
        }

        private static PgpPublicKey PinnedKey()
        {
            using var resource = typeof(SecureRelease).Assembly.GetManifestResourceStream("QUpdater.qbittorrent-signing-key.asc")
                ?? throw new InvalidDataException("Embedded signing key missing.");
            using var decoded = PgpUtilities.GetDecoderStream(resource);
            var rings = new PgpPublicKeyRingBundle(decoded);
            foreach (PgpPublicKeyRing ring in rings.GetKeyRings())
            {
                var key = ring.GetPublicKey();
                if (Convert.ToHexString(key.GetFingerprint()) == Fingerprint) return key;
            }
            throw new InvalidDataException("Embedded key fingerprint mismatch.");
        }

        internal static void Verify(Stream installer, string signatureFile, string expectedHash, CancellationToken token)
        {
            installer.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            int count;
            while ((count = installer.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("Installer SHA-256 mismatch. Installation blocked.");

            var key = PinnedKey();
            using var signatureStream = File.OpenRead(signatureFile);
            using var decoded = PgpUtilities.GetDecoderStream(signatureStream);
            var factory = new PgpObjectFactory(decoded);
            if (factory.NextPgpObject() is not PgpSignatureList signatures || signatures.Count != 1 || factory.NextPgpObject() != null)
                throw new InvalidDataException("Invalid detached signature.");
            var signature = signatures[0];
            // Only the exact pinned primary key is trusted; unknown signing subkeys or rotations fail closed.
            if (signature.KeyId != key.KeyId || signature.SignatureType != PgpSignature.BinaryDocument ||
                signature.HashAlgorithm is not (HashAlgorithmTag.Sha256 or HashAlgorithmTag.Sha384 or HashAlgorithmTag.Sha512))
                throw new InvalidDataException("Unexpected signing key, signature type or digest.");
            signature.InitVerify(key);
            installer.Position = 0;
            while ((count = installer.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                signature.Update(buffer, 0, count);
            }
            if (!signature.Verify()) throw new InvalidDataException("OpenPGP signature verification failed. Installation blocked.");
        }

        internal static int SelfTest()
        {
            try
            {
                using var resource = typeof(SecureRelease).Assembly.GetManifestResourceStream("QUpdater.windows-release-fixture.html");
                using var reader = new StreamReader(resource);
                var html = reader.ReadToEnd();
                var releases = ParsePage(html);
                if (releases.Count < 2 || releases.Find(r => r.Variant == "lt12") == null) return 1;
                if (!Allowed(releases[0].InstallerUri, true) ||
                    Allowed(new Uri("http://downloads.sourceforge.net/project/qbittorrent/a"), true) ||
                    Allowed(new Uri("https://downloads.sourceforge.net.evil.example/project/qbittorrent/a"), true) ||
                    Allowed(new Uri("https://downloads.sourceforge.net/project/other/a"), true)) return 1;
                try { ParsePage(html.Replace(".exe.asc/download", ".exe.bad/download")); return 1; }
                catch (InvalidDataException) { }
                if (Convert.ToHexString(PinnedKey().GetFingerprint()) != Fingerprint) return 1;
                using var payload = new MemoryStream(new byte[] { 1, 2, 3 });
                try { Verify(payload, "unused", new string('0', 64), CancellationToken.None); return 1; }
                catch (InvalidDataException) { }
                return 0;
            }
            catch { return 1; }
        }
    }
}
