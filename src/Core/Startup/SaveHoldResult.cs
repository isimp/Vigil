using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>What a hold of config saves did: saves held, files, written at its end and before a read-back, and the files it left alone.</summary>
    internal sealed class SaveHoldResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public SaveHoldResult(string why, int held, int files, int written, int flushed, double writeMs)
        {
            Why = why ?? "";
            Held = held;
            Files = files;
            Written = written;
            Flushed = flushed;
            WriteMs = writeMs;
        }

        /// <summary>When the hold ended, in words: "the menu showed", "the world was up", "the game quit".</summary>
        public string Why { get; }

        public int Held { get; }

        public int Files { get; }

        public int Written { get; }

        /// <summary>Files written during the hold because they were about to be read back.</summary>
        public int Flushed { get; }

        public double WriteMs { get; }

        /// <summary>The files with the most held saves, by their mod's name, most first.</summary>
        public List<KeyValuePair<string, int>> Most { get; } = new List<KeyValuePair<string, int>>();

        /// <summary>Files another mod wrote itself after their last held save, left as it wrote them.</summary>
        public List<string> Kept { get; } = new List<string>();

        /// <summary>Files that could not be written: "name: reason".</summary>
        public List<string> Failed { get; } = new List<string>();

        /// <summary>Held files a mod opened, checked or copied itself, past BepInEx, and that were written first for it: "mod: file".</summary>
        public List<string> ReadPast { get; } = new List<string>();

        public List<string> Lines()
        {
            string written = Written == Files && Flushed == 0
                ? "each written once when " + Why
                : Written.ToString("N0", Ci) + " written when " + Why + (Flushed > 0 ? ", " + Flushed.ToString("N0", Ci) + " earlier before a read-back" : "");
            string most = Most.Count > 0 ? "; the most: " + string.Join(", ", Most.Select(m => m.Key + " " + m.Value.ToString("N0", Ci))) : "";
            var lines = new List<string>
            {
                "Config saves: " + Held.ToString("N0", Ci) + " held for " + Files.ToString("N0", Ci) + (Files == 1 ? " file, " : " files, ") + written
                + " (" + (WriteMs < 1000 ? WriteMs.ToString("0", Ci) + " ms" : StartupTimeline.Duration(WriteMs)) + ")" + most,
            };

            var odd = new List<string>();
            if (Kept.Count > 0)
            {
                odd.Add(Kept.Count == 1
                    ? "1 file kept as another mod wrote it (" + Kept[0] + ")"
                    : Kept.Count.ToString(Ci) + " files kept as other mods wrote them (" + string.Join(", ", Kept) + ")");
            }

            if (ReadPast.Count > 0) odd.Add(ReadPast.Count.ToString(Ci) + (ReadPast.Count == 1 ? " file" : " files") + " read past BepInEx, so written first: " + string.Join(", ", ReadPast));
            if (Failed.Count > 0) odd.Add(Failed.Count.ToString(Ci) + (Failed.Count == 1 ? " file" : " files") + " not written: " + string.Join(", ", Failed));
            if (odd.Count > 0) lines.Add("Config saves: " + string.Join("; ", odd));
            return lines;
        }

        /// <summary>The result as a startup step's text: why|held|files|written|flushed|ms|name=count;...|kept;...|failed;...|read past;...</summary>
        public string EventText() => string.Join("|", new[]
        {
            Clean(Why), Held.ToString(Ci), Files.ToString(Ci), Written.ToString(Ci), Flushed.ToString(Ci), WriteMs.ToString("0.0", Ci),
            string.Join(";", Most.Select(m => Clean(m.Key) + "=" + m.Value.ToString(Ci))),
            string.Join(";", Kept.Select(Clean)),
            string.Join(";", Failed.Select(Clean)),
            string.Join(";", ReadPast.Select(Clean)),
        });

        /// <summary>The result from its step's text; a text without the read-past field (Vigil 0.40.0) reads as having none.</summary>
        public static SaveHoldResult Parse(string text)
        {
            var f = (text ?? "").Split('|');
            if ((f.Length != 9 && f.Length != 10)
                || !int.TryParse(f[1], NumberStyles.Integer, Ci, out int held) || !int.TryParse(f[2], NumberStyles.Integer, Ci, out int files)
                || !int.TryParse(f[3], NumberStyles.Integer, Ci, out int written) || !int.TryParse(f[4], NumberStyles.Integer, Ci, out int flushed)
                || !double.TryParse(f[5], NumberStyles.Float, Ci, out double ms))
            {
                return null;
            }

            var r = new SaveHoldResult(f[0], held, files, written, flushed, ms);
            foreach (var m in Items(f[6]))
            {
                int eq = m.LastIndexOf('=');
                if (eq > 0 && int.TryParse(m.Substring(eq + 1), NumberStyles.Integer, Ci, out int n)) r.Most.Add(new KeyValuePair<string, int>(m.Substring(0, eq), n));
            }

            r.Kept.AddRange(Items(f[7]));
            r.Failed.AddRange(Items(f[8]));
            if (f.Length == 10) r.ReadPast.AddRange(Items(f[9]));
            return r;
        }

        private static IEnumerable<string> Items(string field) => field.Split(';').Where(s => s.Length > 0);

        private static string Clean(string s) => (s ?? "").Replace('|', ' ').Replace(';', ' ').Replace('=', ' ');
    }
}
