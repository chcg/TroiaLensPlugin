using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace NodeSetup
{
    // Installs the latest Current Node.js when it is missing. Burn runs this elevated.
    internal static class Program
    {
        const string DefaultIndexUrl = "https://nodejs.org/dist/index.json";
        const string GitHubLatestUrl = "https://api.github.com/repos/nodejs/node/releases/latest";
        const string DistBase = "https://nodejs.org/dist/";

        static readonly string LogPath = Path.Combine(Path.GetTempPath(), "TroiaLens-NodeSetup.log");
        static readonly string MsiLogPath = Path.Combine(Path.GetTempPath(), "TroiaLens-NodeSetup-msiexec.log");

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try
            {
                int code = Run(args);
                Log("Çıkış kodu: " + code);
                return code;
            }
            catch (Exception ex)
            {
                Log("Beklenmeyen hata: " + ex);
                return 1;
            }
        }

        static bool dryRun;

        // URL overrides for testing; ignored outside --dry-run so a real install always uses nodejs.org
        static string TestUrl(string name) => dryRun ? Environment.GetEnvironmentVariable(name) : null;

        static int Run(string[] args)
        {
            var opts = new HashSet<string>(args.Select(a => a.TrimStart('-', '/').ToLowerInvariant()));
            dryRun = opts.Contains("dry-run"); bool force = opts.Contains("force");
            Log("NodeSetup başladı. Argümanlar: " + string.Join(" ", args));

            if (!force && TryFindNode(out string nodePath))
            {
                Log($"Node.js zaten kurulu: {nodePath} ({GetNodeVersion(nodePath) ?? "sürüm okunamadı"})");
                return 0;
            }

            ConfigureTls();
            using (var http = CreateClient())
            {
                string version, msiName;
                try { (version, msiName) = ResolveLatest(http); }
                catch (Exception ex)
                {
                    Log("Son Node.js sürümü bulunamadı. İnternet bağlantısını kontrol edin. " + ex.GetBaseException().Message);
                    return 3;
                }
                Log($"Son sürüm: {version}, paket: {msiName}");

                string tempDir = Path.Combine(Path.GetTempPath(), "TroiaLens-NodeSetup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string msiPath = Path.Combine(tempDir, msiName);
                    string shasums;
                    try
                    {
                        string sumsUrl = TestUrl("TROIALENS_NODE_SHASUMS_URL") ?? $"{DistBase}{version}/SHASUMS256.txt";
                        shasums = GetString(http, sumsUrl);
                        Log("İndiriliyor: " + DistBase + version + "/" + msiName);
                        Download(http, $"{DistBase}{version}/{msiName}", msiPath);
                    }
                    catch (Exception ex)
                    {
                        Log("Node.js indirilemedi. İnternet bağlantısını kontrol edin. " + ex.GetBaseException().Message);
                        return 3;
                    }

                    string expected = shasums.Split('\n')
                        .Select(l => l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        .Where(p => p.Length == 2 && p[1].TrimStart('*') == msiName)
                        .Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
                    string actual = Sha256(msiPath);
                    if (expected != actual)
                    {
                        Log($"Dosya doğrulanamadı. Beklenen: {expected ?? "(yok)"}, bulunan: {actual}. Kurulum yapılmadı.");
                        return 2;
                    }
                    Log("SHA-256 doğrulandı: " + actual);

                    if (dryRun)
                    {
                        Log("Deneme modu: kurulacak paket " + msiName + " (msiexec çalıştırılmadı).");
                        return 0;
                    }

                    Log("Node.js kuruluyor...");
                    var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                        $"/i \"{msiPath}\" /qn /norestart /l*v \"{MsiLogPath}\"") { UseShellExecute = false };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        int code = p.ExitCode;
                        Log(code == 0 ? "Node.js kuruldu."
                            : code == 3010 || code == 1641 ? "Node.js kuruldu. Yeniden başlatma gerekiyor."
                            : $"Node.js kurulamadı (msiexec {code}). Ayrıntı: {MsiLogPath}");
                        return code;
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        // --- Detection ---

        static bool TryFindNode(out string path)
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Node.js"))
                    if (key?.GetValue("InstallPath") is string dir && IsNodeDir(dir, out path)) return true;
            }
            catch { }

            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetEnvironmentVariable("ProgramFiles");
            if (pf != null && IsNodeDir(Path.Combine(pf, "nodejs"), out path)) return true;

            var paths = new[]
            {
                ReadRegString(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path"),
                ReadRegString(Registry.CurrentUser, "Environment", "Path"),
                Environment.GetEnvironmentVariable("PATH"),
            };
            foreach (string entry in paths.Where(p => p != null).SelectMany(p => p.Split(';')))
            {
                string dir = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (dir.Length > 0 && IsNodeDir(dir, out path)) return true;
            }
            path = null;
            return false;
        }

        static bool IsNodeDir(string dir, out string path)
        {
            path = null;
            try { path = Path.Combine(dir, "node.exe"); return File.Exists(path); }
            catch { return false; }
        }

        static string ReadRegString(RegistryKey root, string subKey, string name)
        {
            try
            {
                using (var key = root.OpenSubKey(subKey))
                    return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch { return null; }
        }

        static string GetNodeVersion(string nodePath)
        {
            try
            {
                var psi = new ProcessStartInfo(nodePath, "--version")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } return null; }
                    return output.Result.Trim();
                }
            }
            catch { return null; }
        }

        // --- Networking ---

        static void ConfigureTls()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            // TLS 1.3 only where Schannel supports it (Windows 11 / Server 2022+), otherwise the handshake can fail.
            try
            {
                if (int.TryParse(ReadRegString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber"), out int build) && build >= 20348)
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
            }
            catch { }
        }

        static HttpClient CreateClient()
        {
            var proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
            var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true, UseDefaultCredentials = true };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TroiaLens-NodeSetup/1.0");
            return http;
        }

        static (string version, string msi) ResolveLatest(HttpClient http)
        {
            string indexUrl = TestUrl("TROIALENS_NODE_INDEX_URL") ?? DefaultIndexUrl;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var latest = (Dictionary<string, object>)((object[])json.DeserializeObject(GetString(http, indexUrl)))[0];
                string version = (string)latest["version"];
                var files = ((object[])latest["files"]).Cast<string>();
                bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64 && files.Contains("win-arm64-msi");
                return (CheckVersion(version), $"node-{version}-{(arm ? "arm64" : "x64")}.msi");
            }
            catch (Exception ex)
            {
                Log("index.json okunamadı, GitHub deneniyor. " + ex.GetBaseException().Message);
            }
            var gh = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(GetString(http, GitHubLatestUrl));
            string tag = CheckVersion((string)gh["tag_name"]);
            return (tag, $"node-{tag}-x64.msi");
        }

        static string CheckVersion(string v)
        {
            if (v == null || !v.StartsWith("v") || v.IndexOfAny(new[] { '/', '\\', ' ' }) >= 0)
                throw new InvalidDataException("Geçersiz sürüm: " + v);
            return v;
        }

        static string GetString(HttpClient http, string url)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var resp = http.GetAsync(url, cts.Token).GetAwaiter().GetResult())
                return resp.EnsureSuccessStatusCode().Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        static void Download(HttpClient http, string url, string target)
        {
            using (var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using (var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var dst = File.Create(target))
                    src.CopyTo(dst);
            }
        }

        static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(fs).Select(b => b.ToString("x2")));
        }

        static void Log(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(line);
            try { File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}
