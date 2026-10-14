using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// What the config files of the mods Vigil replaces carry into Vigil's own at its first start:
    /// each setting Vigil still has, under the section and name it has now. The old files are only
    /// read. Reading follows the config file format (a "[Section]" line, then "Key = Value" lines,
    /// "#" lines are descriptions); a line that fits neither is skipped.
    /// </summary>
    internal static class ConfigCarryOver
    {
        public const string NetProbeFile = "isimp.NetProbe.cfg";
        public const string SpawnProofFile = "isimp.SpawnProof.cfg";

        private sealed class Rule
        {
            public string File, FromSection, ToSection;
            public string[] Keys;
            public string FromKey, ToKey;
        }

        private static readonly Rule[] Rules =
        {
            new Rule { File = NetProbeFile, FromSection = "General", FromKey = "Enabled", ToSection = "Modules", ToKey = "Recorder" },
            new Rule { File = NetProbeFile, FromSection = "General", FromKey = "Recording", ToSection = "Recording", ToKey = "Mode" },
            new Rule { File = NetProbeFile, FromSection = "Output", Keys = new[] { "KeepMegabytes", "Folder" } },
            new Rule
            {
                File = NetProbeFile, FromSection = "Hooks",
                Keys = new[] { "ModCallbacks", "Coroutines", "Allocations", "Exceptions", "FileWrites", "PatchChanges", "MessageHandlers", "ObjectCreation" },
            },
            new Rule { File = NetProbeFile, FromSection = "Lag report", Keys = new[] { "Key" } },
            new Rule
            {
                File = NetProbeFile, FromSection = "Incidents",
                Keys = new[]
                {
                    "LongFrameMs", "SlowFps", "SlowSeconds", "SilentMs", "PingMs", "SendBacklogKb", "LogLinesPerSecond",
                    "MemoryLoadPct", "PageFaultsPerSecond", "MaxReportsPerSession", "QuietSeconds",
                },
            },
            new Rule { File = NetProbeFile, FromSection = "Deep capture", Keys = new[] { "StartOnSlowStretch", "Seconds", "MinutesBetween", "MsPerFrame" } },
            new Rule
            {
                File = SpawnProofFile, FromSection = "General", ToSection = "Spawn marks",
                Keys = new[] { "Key", "Radius", "Spacing", "MarkStyle", "PillarHeight", "MarkColor" },
            },
        };

        /// <summary>
        /// The settings to carry over, by their place in Vigil's file, from the old files' texts
        /// keyed by file name (<see cref="NetProbeFile"/>, <see cref="SpawnProofFile"/>).
        /// </summary>
        public static Dictionary<(string Section, string Key), string> Values(Dictionary<string, string> oldFiles)
        {
            var result = new Dictionary<(string Section, string Key), string>();
            foreach (var file in oldFiles)
            {
                var old = Read(file.Value);
                foreach (var rule in Rules.Where(r => r.File == file.Key))
                {
                    var pairs = rule.Keys != null
                        ? rule.Keys.Select(k => (From: k, To: k))
                        : new[] { (From: rule.FromKey, To: rule.ToKey) };
                    foreach (var (from, to) in pairs)
                        if (old.TryGetValue((rule.FromSection, from), out string value))
                            result[(rule.ToSection ?? rule.FromSection, to)] = value;
                }
            }
            return result;
        }

        /// <summary>A config file holding just these settings, for the config reader to take up.</summary>
        public static string NewFileText(Dictionary<(string Section, string Key), string> values)
        {
            var sb = new StringBuilder();
            foreach (var section in values.GroupBy(kv => kv.Key.Section))
            {
                sb.Append('[').Append(section.Key).Append("]\r\n\r\n");
                foreach (var kv in section) sb.Append(kv.Key.Key).Append(" = ").Append(kv.Value).Append("\r\n");
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Every setting in a config file's text, by section and name; a later line wins.</summary>
        public static Dictionary<(string Section, string Key), string> Read(string text)
        {
            var result = new Dictionary<(string Section, string Key), string>();
            string section = "";
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (key.Length == 0) continue;
                result[(section, key)] = line.Substring(eq + 1).Trim();
            }
            return result;
        }
    }
}
