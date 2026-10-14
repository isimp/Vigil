using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The panel's load test form turned into the console command it stands for: which test, for
    /// the player simulation how many players (left to the console's default when not a number)
    /// and how they move, then force and deep.
    /// </summary>
    internal static class LoadForm
    {
        public enum Test { BothWays, Upload, Download, Players }

        /// <summary>The tests as the form names them, in <see cref="Test"/>'s order.</summary>
        public static readonly string[] Names = { "Both ways", "Upload", "Download", "Simulated players" };

        public static string Command(Test test, string players, string pattern, bool force, bool deep)
        {
            var words = new List<string> { "load" };
            switch (test)
            {
                case Test.Upload: words.Add("up"); break;
                case Test.Download: words.Add("down"); break;
                case Test.Players:
                    words.Add("players");
                    if (int.TryParse((players ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) words.Add(n.ToString(CultureInfo.InvariantCulture));
                    words.Add(pattern);
                    break;
            }
            if (force) words.Add("force");
            if (deep) words.Add("deep");
            return string.Join(" ", words);
        }
    }
}
