using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// One step of a game start as the patcher saw it: when (milliseconds since the patcher started),
    /// what kind (<see cref="StartupLines"/>) and its text. Kept as one tab-separated line, so the patcher
    /// can hand its steps to the plugin as plain strings.
    /// </summary>
    internal sealed class StartupEvent
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public StartupEvent(double ms, string kind, string text)
        {
            Ms = ms;
            Kind = kind ?? "";
            Text = Clean(text);
        }

        public double Ms { get; }

        public string Kind { get; }

        public string Text { get; }

        public string Line() => Ms.ToString("0.0", Ci) + "\t" + Kind + "\t" + Text;

        /// <summary>The step a <see cref="Line"/> holds; null for anything else.</summary>
        public static StartupEvent Parse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var parts = line.Split('\t');
            if (parts.Length < 3 || !double.TryParse(parts[0], NumberStyles.Float, Ci, out double ms)) return null;
            return new StartupEvent(ms, parts[1], parts[2]);
        }

        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
