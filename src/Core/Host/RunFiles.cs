using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The files of one recorded session, under Vigil's name ("Vigil-summary.txt") or, for a
    /// session NetProbe recorded before Vigil replaced it, under NetProbe's ("NetProbe-summary.txt").
    /// </summary>
    internal static class RunFiles
    {
        private static readonly string[] Prefixes = { OutputLocation.FolderName + "-", OutputLocation.LegacyName + "-" };

        /// <summary>The session's file with this name after the prefix ("summary.txt"); Vigil's name when it has neither.</summary>
        public static string Find(string sessionDir, string name)
        {
            foreach (var prefix in Prefixes)
            {
                string path = Path.Combine(sessionDir, prefix + name);
                if (File.Exists(path)) return path;
            }
            return Path.Combine(sessionDir, Prefixes[0] + name);
        }

        /// <summary>Every file of the session matching the pattern after the prefix ("incident-*.txt"), by name.</summary>
        public static List<string> All(string sessionDir, string pattern)
        {
            if (!Directory.Exists(sessionDir)) return new List<string>();
            return Prefixes.SelectMany(p => Directory.GetFiles(sessionDir, p + pattern))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
        }
    }
}
