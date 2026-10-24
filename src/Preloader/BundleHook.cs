using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
using Vigil.Core;

namespace Vigil.Startup
{
    /// <summary>
    /// Asked first by Unity's four internal bundle loads from memory and streams (<see cref="BundleHookEdit"/>).
    /// <list type="bullet">
    /// <item>A bundle with a ready fast copy loads from that copy, after its asset names are checked
    /// against the original's; a copy that fails or differs is refused for good and the bundle loads
    /// the usual way in the same call.</item>
    /// <item>An LZMA bundle without a copy loads the usual way, and Unity writes a copy in the background.</item>
    /// <item>Everything else loads the usual way.</item>
    /// </list>
    /// A load Vigil makes itself sets a thread flag, so the edited methods do not ask again. The usual
    /// way is the public method the mod called, with the same arguments, so it behaves exactly as before.
    /// </summary>
    public static class BundleHook
    {
        [ThreadStatic] private static bool t_inside;
        private static readonly HashSet<string> Converting = new HashSet<string>(StringComparer.Ordinal);
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        private sealed class Request
        {
            public long Length;
            public uint Crc;
            public bool Seekable;
            public Func<long, int, byte[]> ReadAt;
            public Action<string> WriteSource;
        }

        private sealed class Plan
        {
            public BundleChoice Choice;
            public string Key;
            public string Outcome;
            public BundleEntry Entry;
            public string CopyPath;
        }

        // ------------------------------------------------------------------ the four entry points

        public static bool TryMemory(byte[] binary, uint crc, out AssetBundle result)
        {
            result = null;
            if (t_inside || binary == null) return false;
            var request = MemoryRequest(binary, crc);
            var plan = Decide(request, async: false);
            if (plan == null) return false;
            result = LoadNow(plan, request, () => AssetBundle.LoadFromMemory(binary, crc));
            return true;
        }

        public static bool TryMemoryAsync(byte[] binary, uint crc, out AssetBundleCreateRequest result)
        {
            result = null;
            if (t_inside || binary == null) return false;
            var request = MemoryRequest(binary, crc);
            var plan = Decide(request, async: true);
            if (plan == null) return false;
            return LoadLater(plan, request, out result);
        }

        public static bool TryStream(Stream stream, uint crc, uint managedReadBufferSize, out AssetBundle result)
        {
            result = null;
            if (t_inside || stream == null) return false;
            var request = StreamRequest(stream, crc);
            var plan = Decide(request, async: false);
            if (plan == null) return false;
            result = LoadNow(plan, request, () => AssetBundle.LoadFromStream(stream, crc, managedReadBufferSize));
            return true;
        }

        public static bool TryStreamAsync(Stream stream, uint crc, uint managedReadBufferSize, out AssetBundleCreateRequest result)
        {
            result = null;
            if (t_inside || stream == null) return false;
            var request = StreamRequest(stream, crc);
            var plan = Decide(request, async: true);
            if (plan == null) return false;
            return LoadLater(plan, request, out result);
        }

        // ------------------------------------------------------------------ reading the bundle

        private static Request MemoryRequest(byte[] binary, uint crc) => new Request
        {
            Length = binary.LongLength,
            Crc = crc,
            Seekable = true,
            ReadAt = (at, count) =>
            {
                int n = (int)Math.Max(0, Math.Min(count, binary.LongLength - at));
                var bytes = new byte[n];
                Array.Copy(binary, at, bytes, 0, n);
                return bytes;
            },
            WriteSource = path => File.WriteAllBytes(path, binary),
        };

        private static Request StreamRequest(Stream stream, uint crc)
        {
            bool seekable = stream.CanSeek && stream.CanRead;
            long start = seekable ? stream.Position : 0;
            return new Request
            {
                Length = seekable ? stream.Length - start : 0,
                Crc = crc,
                Seekable = seekable,
                ReadAt = (at, count) =>
                {
                    long keep = stream.Position;
                    try
                    {
                        stream.Position = start + at;
                        var bytes = new byte[count];
                        int got = 0;
                        while (got < count)
                        {
                            int n = stream.Read(bytes, got, count - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got < count) Array.Resize(ref bytes, got);
                        return bytes;
                    }
                    finally { stream.Position = keep; }
                },
                WriteSource = path =>
                {
                    long keep = stream.Position;
                    try
                    {
                        stream.Position = start;
                        using (var file = File.Create(path)) stream.CopyTo(file);
                    }
                    finally { stream.Position = keep; }
                },
            };
        }

        // ------------------------------------------------------------------ deciding

        /// <summary>The way this load goes; null to let Unity's method go on untouched (the cache is off, or reading the bundle failed).</summary>
        private static Plan Decide(Request r, bool async)
        {
            try
            {
                StartupState.Asked++;
                if (!StartupState.CacheOn) return null;
                BundleShape shape = r.Seekable && r.Length >= BundleCache.MinBytes ? BundleFormat.Read(r.ReadAt, r.Length) : null;
                string key = shape != null && shape.WorthACopy ? BundleKey.Of(r.ReadAt, r.Length, Application.unityVersion) : null;
                var entry = StartupState.Entry(key);
                string copy = key == null ? null : Path.Combine(StartupState.CacheDir, key + BundleCache.CopyExtension);
                // A waiting load cannot fall back to the usual way once the copy is handed out, so it uses only a copy whose original's assets are known.
                bool ready = entry != null && entry.IsReady && File.Exists(copy) && (!async || entry.Assets.Length > 0);
                bool converting;
                lock (Converting) converting = key != null && Converting.Contains(key);
                var choice = BundleCache.Choose(shape, r.Length, r.Crc, r.Seekable, ready, entry != null && entry.IsBlocked, converting, on: true);
                string outcome = choice == BundleChoice.Copy ? BundleOutcome.Copy
                    : shape == null || !shape.WorthACopy || r.Crc != 0 ? BundleOutcome.Plain
                    : entry != null && entry.IsBlocked ? BundleOutcome.Blocked
                    : BundleOutcome.Usual;
                return new Plan { Choice = choice, Key = key, Outcome = outcome, Entry = entry, CopyPath = copy };
            }
            catch (Exception e)
            {
                Preloader.Log?.LogWarning("Bundle cache: " + e.GetType().Name + ": " + e.Message + "; this bundle loads the usual way.");
                return null;
            }
        }

        // ------------------------------------------------------------------ loading

        private static AssetBundle LoadNow(Plan plan, Request r, Func<AssetBundle> usual)
        {
            var watch = Stopwatch.StartNew();
            string outcome = plan.Outcome;
            if (plan.Choice == BundleChoice.Copy)
            {
                var copy = Inside(() => AssetBundle.LoadFromFile(plan.CopyPath));
                if (copy != null && SameAssets(plan.Entry, copy))
                {
                    StartupState.MarkUsed(plan.Key);
                    StartupState.CopiesLoaded++;
                    Note(BundleOutcome.Copy, r.Length, watch, plan.Key);
                    return copy;
                }
                if (copy != null) copy.Unload(true);
                Refuse(plan, r.Length, copy == null ? "Unity could not load it" : "its assets differ from the original's");
                outcome = BundleOutcome.Failed;
            }

            var bundle = Inside(usual);
            if (plan.Choice == BundleChoice.UsualAndConvert && bundle != null) Convert(plan.Key, r, Assets(bundle));
            Note(outcome, r.Length, watch, plan.Key);
            return bundle;
        }

        // A waiting load: from the copy when there is one, checked once it has loaded; otherwise Unity's method goes on as it was.
        private static bool LoadLater(Plan plan, Request r, out AssetBundleCreateRequest result)
        {
            result = null;
            if (plan.Choice == BundleChoice.Copy)
            {
                var request = Inside(() => AssetBundle.LoadFromFileAsync(plan.CopyPath));
                if (request != null)
                {
                    request.completed += _ =>
                    {
                        var loaded = request.assetBundle;
                        if (loaded != null && SameAssets(plan.Entry, loaded)) { StartupState.MarkUsed(plan.Key); StartupState.CopiesLoaded++; }
                        else Refuse(plan, r.Length, loaded == null ? "Unity could not load it" : "its assets differ from the original's");
                    };
                    StartupState.Add(StartupLines.Bundle, Text(BundleOutcome.Copy, r.Length, 0, plan.Key));
                    result = request;
                    return true;
                }
                Refuse(plan, r.Length, "Unity did not start loading it");
                StartupState.Add(StartupLines.Bundle, Text(BundleOutcome.Failed, r.Length, 0, plan.Key));
                return false;
            }
            if (plan.Choice == BundleChoice.UsualAndConvert) Convert(plan.Key, r, "");
            StartupState.Add(StartupLines.Bundle, Text(plan.Outcome, r.Length, 0, plan.Key));
            return false;
        }

        private static T Inside<T>(Func<T> load) where T : class
        {
            bool was = t_inside;
            t_inside = true;
            try { return load(); }
            finally { t_inside = was; }
        }

        private static string Assets(AssetBundle bundle)
        {
            try { return BundleCache.AssetsSignature(bundle.GetAllAssetNames()); }
            catch (Exception) { return ""; }
        }

        private static bool SameAssets(BundleEntry entry, AssetBundle copy) => entry == null || entry.Assets.Length == 0 || Assets(copy) == entry.Assets;

        private static void Refuse(Plan plan, long length, string why)
        {
            StartupState.CopiesRefused++;
            StartupState.SetEntry(new BundleEntry(plan.Key, DateTime.UtcNow, length, 0, BundleEntry.Blocked, plan.Entry?.Assets ?? "", plan.Entry?.Plugin ?? StartupState.CurrentPlugin));
            StartupState.SaveIndex();
            TryDelete(plan.CopyPath);
            Preloader.Log?.LogWarning("Bundle cache: the fast copy " + plan.Key + " was refused (" + why + "); this bundle loads the usual way from now on.");
        }

        private static void Note(string outcome, long length, Stopwatch watch, string key) =>
            StartupState.Add(StartupLines.Bundle, Text(outcome, length, watch.Elapsed.TotalMilliseconds, key));

        private static string Text(string outcome, long length, double ms, string key) =>
            outcome + "|" + length.ToString(Ci) + "|" + ms.ToString("0.0", Ci) + "|" + (key ?? "");

        // ------------------------------------------------------------------ making a copy

        private static void Convert(string key, Request r, string assets)
        {
            if (key == null) return;
            lock (Converting) if (!Converting.Add(key)) return;
            string dir = StartupState.CacheDir, plugin = StartupState.CurrentPlugin;
            string source = Path.Combine(dir, key + BundleCache.SourceExtension), temp = Path.Combine(dir, key + BundleCache.TempExtension), copy = Path.Combine(dir, key + BundleCache.CopyExtension);
            try
            {
                Directory.CreateDirectory(dir);
                r.WriteSource(source);
                var op = AssetBundle.RecompressAssetBundleAsync(source, temp, BuildCompression.LZ4Runtime, 0, UnityEngine.ThreadPriority.Low);
                if (op == null) { Failed(key, r.Length, plugin, assets, source, temp, "Unity did not start the conversion"); return; }
                StartupState.ConversionsStarted++;
                op.completed += _ => Finished(op, key, r.Length, plugin, assets, source, temp, copy);
            }
            catch (Exception e)
            {
                Failed(key, r.Length, plugin, assets, source, temp, e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Finished(AssetBundleRecompressOperation op, string key, long length, string plugin, string assets, string source, string temp, string copy)
        {
            try
            {
                if (!op.success || !File.Exists(temp)) { Failed(key, length, plugin, assets, source, temp, op.humanReadableResult); return; }
                var shape = FileShape(temp);
                if (!shape.UnityFs || shape.Problem != null || shape.LzmaBlocks > 0) { Failed(key, length, plugin, assets, source, temp, "the copy is not a plain LZ4 bundle (" + (shape.Problem ?? "LZMA blocks left") + ")"); return; }
                TryDelete(copy);
                File.Move(temp, copy);
                TryDelete(source);
                StartupState.SetEntry(new BundleEntry(key, DateTime.UtcNow, length, new FileInfo(copy).Length, BundleEntry.Ready, assets, plugin));
                StartupState.SaveIndex();
                StartupState.ConversionsDone++;
                Preloader.Log?.LogInfo("Bundle cache: fast copy ready for a bundle of " + plugin + " (" + (length / 1e6).ToString("0.0", Ci) + " MB LZMA), used from the next start.");
            }
            catch (Exception e)
            {
                Failed(key, length, plugin, assets, source, temp, e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                lock (Converting) Converting.Remove(key);
            }
        }

        private static BundleShape FileShape(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = file.Length;
                return BundleFormat.Read((at, count) =>
                {
                    file.Position = at;
                    var bytes = new byte[(int)Math.Max(0, Math.Min(count, length - at))];
                    int got = 0;
                    while (got < bytes.Length)
                    {
                        int n = file.Read(bytes, got, bytes.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    return bytes;
                }, length);
            }
        }

        private static void Failed(string key, long length, string plugin, string assets, string source, string temp, string why)
        {
            StartupState.ConversionsFailed++;
            StartupState.SetEntry(new BundleEntry(key, DateTime.UtcNow, length, 0, BundleEntry.Blocked, assets, plugin));
            StartupState.SaveIndex();
            TryDelete(source);
            TryDelete(temp);
            Preloader.Log?.LogWarning("Bundle cache: no fast copy for a bundle of " + plugin + " (" + why + "); it keeps loading the usual way.");
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
