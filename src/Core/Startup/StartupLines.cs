using System;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The kinds of startup step, and which of the lines BepInEx and StartupAccelerator log mark one.
    /// BepInEx logs "Loading [Name Version] (GUID)" just before it loads each plugin and runs its
    /// Awake, and "Chainloader startup complete (...)" after the last; StartupAccelerator logs how long
    /// its batch of deferred patches took. Every other line is left out.
    /// </summary>
    internal static class StartupLines
    {
        public const string Started = "started", Plugin = "plugin", PluginsDone = "plugins-done", Batch = "batch", Bundle = "bundle";
        public const string MenuScene = "menu-scene", MenuStart = "menu", MenuShown = "menu-shown", WorldUp = "world-up";
        public const string JoinStart = "join-start", JoinStep = "join-step", JoinMessage = "join-message", JoinWorld = "join-world";

        private const string LoadingPrefix = "Loading [", DonePrefix = "Chainloader startup complete", BatchPrefix = "Batch-patched ";

        /// <summary>The step kind a log line marks; null for a line that marks none.</summary>
        public static string Classify(string source, string message)
        {
            if (message == null) return null;
            if (string.Equals(source, "BepInEx", StringComparison.Ordinal))
            {
                if (message.StartsWith(LoadingPrefix, StringComparison.Ordinal) && message.Contains("] (")) return Plugin;
                if (message.StartsWith(DonePrefix, StringComparison.Ordinal)) return PluginsDone;
                return null;
            }
            if (string.Equals(source, "StartupAccelerator", StringComparison.Ordinal) && message.StartsWith(BatchPrefix, StringComparison.Ordinal)) return Batch;
            return null;
        }

        /// <summary>"Name Version" from a "Loading [Name Version] (GUID)" line.</summary>
        public static string PluginName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int open = text.IndexOf('['), close = text.LastIndexOf("] (", StringComparison.Ordinal);
            return open >= 0 && close > open ? text.Substring(open + 1, close - open - 1) : text;
        }

        /// <summary>The methods and milliseconds of "Batch-patched N methods after ... in M ms".</summary>
        public static bool ParseBatch(string text, out int methods, out double ms)
        {
            methods = 0;
            ms = 0;
            if (text == null || !text.StartsWith(BatchPrefix, StringComparison.Ordinal)) return false;
            var words = text.Split(' ');
            if (words.Length < 4 || !int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out methods)) return false;
            int inAt = Array.LastIndexOf(words, "in");
            return inAt > 0 && inAt + 1 < words.Length && double.TryParse(words[inAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
        }
    }
}
