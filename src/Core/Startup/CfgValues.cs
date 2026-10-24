using System;
using System.Collections.Generic;

namespace Vigil.Core
{
    /// <summary>
    /// A BepInEx settings file's values, read the way BepInEx's <c>ConfigFile.Reload</c> reads them:
    /// lines trimmed, "#" lines skipped, "[...]" opening a section, and each other line split at its
    /// first "=" into key and value, both trimmed.
    /// </summary>
    internal static class CfgValues
    {
        /// <summary>One setting's key in <see cref="Parse"/>'s result.</summary>
        public static string Key(string section, string key) => section + "\u0001" + key;

        private static readonly char[] Equal = { '=' };

        // BepInEx compares with the current culture; so does this, to read a file exactly as it does.
        public static Dictionary<string, string> Parse(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>();
            string section = "";
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.CurrentCulture)) continue;
                if (line.StartsWith("[", StringComparison.CurrentCulture) && line.EndsWith("]", StringComparison.CurrentCulture))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }

                var split = line.Split(Equal, 2);
                if (split.Length == 2) values[Key(section, split[0].Trim())] = split[1].Trim();
            }

            return values;
        }

        /// <summary>The settings, at most <paramref name="max"/>, whose value in the file is not the one given: "[section] key".</summary>
        public static List<string> Differences(Dictionary<string, string> file, IEnumerable<KeyValuePair<string, string>> live, int max)
        {
            var found = new List<string>();
            foreach (var setting in live)
            {
                if (found.Count >= max) break;
                if (file.TryGetValue(setting.Key, out var value) && value == (setting.Value ?? "").Trim()) continue;
                int split = setting.Key.IndexOf('\u0001');
                found.Add(split < 0 ? setting.Key : "[" + setting.Key.Substring(0, split) + "] " + setting.Key.Substring(split + 1));
            }

            return found;
        }
    }
}
