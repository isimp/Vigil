using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// Writes what this session runs with, so two sessions can be compared later
    /// (tools/VigilReport diff): Vigil-mods.tsv (every plugin with version and file),
    /// Vigil-configs.tsv (every config file with size and MD5, hashed on a background
    /// thread) and Vigil-system.txt (game, engine, machine, graphics, display settings).
    /// </summary>
    internal static class SessionInfo
    {
        /// <param name="dir">The recordings folder.</param>
        /// <param name="configs">False while config saves are held: reading the files then would make the hold write each of them, so they are listed when it ends.</param>
        public static void Write(string dir, bool configs = true)
        {
            WriteMods(Path.Combine(dir, "Vigil-mods.tsv"));
            WriteSystem(Path.Combine(dir, "Vigil-system.txt"));
            if (!configs) return;
            string list = Path.Combine(dir, "Vigil-configs.tsv");
            new Thread(() => OwnThreads.Timed(() => WriteConfigs(Paths.ConfigPath, list))) { IsBackground = true, Name = "Vigil.ConfigHashes" }.Start();
        }

        private static void WriteMods(string path)
        {
            var sb = new StringBuilder("guid\tname\tversion\tfile\r\n");
            foreach (var p in Chainloader.PluginInfos.Values.OrderBy(p => p.Metadata.Name, StringComparer.OrdinalIgnoreCase))
            {
                string file = p.Location ?? "";
                if (file.StartsWith(Paths.PluginPath, StringComparison.OrdinalIgnoreCase)) file = file.Substring(Paths.PluginPath.Length).TrimStart('\\', '/');
                sb.Append(p.Metadata.GUID).Append('\t').Append(p.Metadata.Name).Append('\t').Append(p.Metadata.Version).Append('\t').Append(file).Append("\r\n");
            }
            Try(() => File.WriteAllText(path, sb.ToString()));
        }

        private static void WriteConfigs(string configDir, string path)
        {
            try
            {
                var sb = new StringBuilder("file\tsize\thash\r\n");
                using (var md5 = MD5.Create())
                {
                    foreach (var f in Directory.GetFiles(configDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var info = new FileInfo(f);
                            if (info.Length > 20 * 1024 * 1024) continue;
                            string ext = info.Extension.ToLowerInvariant();
                            if (ext != ".cfg" && ext != ".yml" && ext != ".yaml" && ext != ".json" && ext != ".txt" && ext != ".ini" && ext != ".xml") continue;
                            string hash;
                            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                hash = BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                            sb.Append(f.Substring(configDir.Length).TrimStart('\\', '/')).Append('\t').Append(info.Length).Append('\t').Append(hash).Append("\r\n");
                        }
                        catch (Exception) { }
                    }
                }
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception) { }
        }

        private static void WriteSystem(string path)
        {
            var sb = new StringBuilder();
            void Line(string k, Func<object> v)
            {
                object value;
                try { value = v(); } catch (Exception e) { value = "(" + e.GetType().Name + ")"; }
                sb.Append(k).Append(": ").Append(value).Append("\r\n");
            }
            Line("written", () => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("valheim", () => global::Version.GetVersionString());
            Line("unity", () => Application.unityVersion);
            Line("bepinex", () => typeof(Chainloader).Assembly.GetName().Version);
            Line("plugins loaded", () => Chainloader.PluginInfos.Count);
            Line("command line", () => Environment.CommandLine);
            Line("os", () => SystemInfo.operatingSystem);
            Line("cpu", () => SystemInfo.processorType + ", " + SystemInfo.processorCount + " threads, " + SystemInfo.processorFrequency + " MHz");
            Line("ram", () => SystemInfo.systemMemorySize + " MB");
            Line("gpu", () => SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceVendor + "), " + SystemInfo.graphicsMemorySize + " MB, " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceVersion);
            Line("gpu driver/shader", () => SystemInfo.graphicsShaderLevel);
            Line("screen", () => Screen.width + "x" + Screen.height + " @ " + Screen.currentResolution.refreshRateRatio + " Hz, " + Screen.fullScreenMode);
            Line("quality level", () => QualitySettings.names[QualitySettings.GetQualityLevel()]);
            Line("vsync", () => QualitySettings.vSyncCount);
            Line("target frame rate", () => Application.targetFrameRate);
            Line("shadows", () => QualitySettings.shadows + ", distance " + QualitySettings.shadowDistance + ", resolution " + QualitySettings.shadowResolution);
            Line("lod bias", () => QualitySettings.lodBias);
            Line("anti aliasing", () => QualitySettings.antiAliasing);
            Line("gc mode", () => UnityEngine.Scripting.GarbageCollector.GCMode + (UnityEngine.Scripting.GarbageCollector.isIncremental ? ", incremental, slice " + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1000000.0 + " ms" : ", not incremental"));
            Line("headless", () => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null);
            Try(() => File.WriteAllText(path, sb.ToString()));
        }

        private static void Try(Action a) { try { a(); } catch (Exception) { } }
    }
}
