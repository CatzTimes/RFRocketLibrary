using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using RFRocketLibrary.Constants;
using RFRocketLibrary.Enum;

namespace RFRocketLibrary.Utils
{
    public static class DependencyUtil
    {
        #region Methods

        /// <summary>
        /// Allowed download hosts for dependency assemblies.
        /// </summary>
        private static readonly string[] AllowedHosts = { "cdn.jsdelivr.net", "raw.githubusercontent.com", "gitlab.com" };

        /// <summary>
        /// Validates a dependency download URL before any network request is made:
        /// only https is allowed, the host must be whitelisted, and the resolved
        /// addresses must not point at loopback/private/reserved ranges (blocks
        /// misconfigured links and trivial DNS rebinding toward internal services).
        /// </summary>
        public static void ValidateDependencyUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"Blocked dependency URL (https only): {url}");

            if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Blocked dependency URL host '{uri.Host}' (not whitelisted).");

            foreach (var address in Dns.GetHostAddresses(uri.Host))
            {
                if (IPAddress.IsLoopback(address)
                    || (address.AddressFamily == AddressFamily.InterNetwork && (
                           address.GetAddressBytes()[0] == 10
                           || (address.GetAddressBytes()[0] == 172 && address.GetAddressBytes()[1] >= 16 && address.GetAddressBytes()[1] <= 31)
                           || (address.GetAddressBytes()[0] == 192 && address.GetAddressBytes()[1] == 168)
                           || (address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254)))
                    || (address.AddressFamily == AddressFamily.InterNetworkV6 && (
                           address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)))
                    throw new InvalidOperationException(
                        $"Blocked dependency URL '{url}': host resolves to a loopback/private/reserved address ({address}).");
            }
        }

        private static byte[] DownloadAssembly(WebClient client, string url)
        {
            ValidateDependencyUrl(url);
            return client.DownloadData(url);
        }

        private static async Task<byte[]> DownloadAssemblyAsync(WebClient client, string url)
        {
            ValidateDependencyUrl(url);
            return await client.DownloadDataTaskAsync(url);
        }

        public static bool CanBeLoaded(EDependency dependency)
        {
            if(Rocket.Core.Plugins.RocketPlugin.IsDependencyLoaded(dependency.ToString()))return false;
            return AppDomain.CurrentDomain.GetAssemblies().All(x =>
                x.FullName != typeof(DependencyFullName).GetField(dependency.ToString()).GetValue(null).ToString());
        }

        public static string GetCachePath(EDependency dependency)
        {
            var rocketDir = Environment.CurrentDirectory;
            var librariesDir = Path.Combine(rocketDir, "Libraries");
            var cacheDir = Path.Combine(librariesDir, "Cache");
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            return Path.Combine(cacheDir,
                typeof(DependencyIntegrity).GetField(dependency.ToString()).GetValue(null).ToString());
        }

        public static string GetIntegrity(string filePath)
        {
            using var md = MD5.Create();
            using var fileStream = File.OpenRead(filePath);
            var value = md.ComputeHash(fileStream);
            var result = BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
            fileStream.Close();

            return result;
        }

        public static string GetIntegrity(byte[] buffer)
        {
            using var md = MD5.Create();
            var value = md.ComputeHash(buffer);
            var result = BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
            return result;
        }

        public static bool IsCacheExists(EDependency dependency)
        {
            var cachePath = GetCachePath(dependency);
            return File.Exists(cachePath);
        }

        public static void Load(EDependency dependency, bool mirror = false)
        {
            if (!CanBeLoaded(dependency))
                return;

            using var wc = new WebClient();
            wc.Proxy = null;
            byte[] assembly = null;
            var cachePath = GetCachePath(dependency);
            if (dependency != EDependency.RFRocketLibrary && IsCacheExists(dependency))
            {
                try
                {
                    using var fs = File.OpenRead(cachePath);
                    assembly = StreamToByteArray(fs);
                    fs.Close();
                }
                catch
                {
                    try
                    {
                        assembly = DownloadAssembly(wc, typeof(DependencyLink)
                            .GetField(dependency + $"{(mirror ? "Mirror" : string.Empty)}").GetValue(null)
                            .ToString());
                    }
                    catch
                    {
                        try
                        {
                            assembly = DownloadAssembly(wc, typeof(DependencyLink).GetField(dependency + "Mirror").GetValue(null)
                            .ToString());
                        }
                        catch { }
                    }
                }
            }
            else
            {
                try
                {
                    assembly = DownloadAssembly(wc, typeof(DependencyLink)
                        .GetField(dependency + $"{(mirror ? "Mirror" : string.Empty)}").GetValue(null)
                        .ToString());
                }
                catch
                {
                    try
                    {
                        assembly = DownloadAssembly(wc, typeof(DependencyLink).GetField(dependency + "Mirror").GetValue(null)
                            .ToString());
                    }
                    catch { }
                }

                try
                {
                    if (dependency != EDependency.RFRocketLibrary)
                        File.WriteAllBytes(cachePath, assembly);
                }
                catch (Exception)
                {
                    //
                }
            }

            Assembly.Load(assembly);
        }

        public static async Task LoadAsync(EDependency dependency, bool mirror = false)
        {
            if (!CanBeLoaded(dependency))
                return;

            using var wc = new WebClient();
            wc.Proxy = null;
            byte[] assembly;
            var cachePath = GetCachePath(dependency);
            if (IsCacheExists(dependency))
            {
                try
                {
                    using var fs = File.OpenRead(cachePath);
                    assembly = StreamToByteArray(fs);
                    fs.Close();
                }
                catch (Exception)
                {
                    try
                    {
                        assembly = await DownloadAssemblyAsync(wc, typeof(DependencyLink)
                            .GetField(dependency + $"{(mirror ? "Mirror" : string.Empty)}").GetValue(null)
                            .ToString());
                    }
                    catch
                    {
                        assembly = await DownloadAssemblyAsync(wc, typeof(DependencyLink).GetField(dependency + "Mirror")
                            .GetValue(null)
                            .ToString());
                    }
                }
            }
            else
            {
                try
                {
                    assembly = await DownloadAssemblyAsync(wc, typeof(DependencyLink)
                        .GetField(dependency + $"{(mirror ? "Mirror" : string.Empty)}").GetValue(null)
                        .ToString());
                }
                catch
                {
                    assembly = await DownloadAssemblyAsync(wc, typeof(DependencyLink).GetField(dependency + "Mirror")
                        .GetValue(null)
                        .ToString());
                }

                try
                {
                    File.WriteAllBytes(cachePath, assembly);
                }
                catch (Exception)
                {
                    //
                }
            }

            Assembly.Load(assembly);
        }

        public static byte[] StreamToByteArray(Stream stream)
        {
            if (stream is MemoryStream memoryStream)
            {
                return memoryStream.ToArray();
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        #endregion
    }
}