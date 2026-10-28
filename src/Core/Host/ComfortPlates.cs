using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "ComfortMe clears its hover highlight only when one is lit": outside its
    /// comfort tab ComfortMe 1.3.1 switches off the highlight plate on every piece button of the
    /// build menu on every frame, hammer out or not, looking each button's plate up again each
    /// time. A plate lights only through a lift, so after a clearing nothing is lit until the
    /// next lift; the clearing runs again after a lift or after the menu's buttons were rebuilt
    /// (a button back from the menu's pool may carry a lit plate). Off, every clearing runs.
    /// </summary>
    internal sealed class ComfortPlates
    {
        private bool _dirty = true;

        public bool ClearingRuns(bool on) => !on || _dirty;

        public void Cleared() => _dirty = false;

        public void Lifted(bool lifted)
        {
            if (lifted) _dirty = true;
        }

        public void ButtonsRebuilt() => _dirty = true;

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool on, bool found, string version, string problem, long ran, long skipped, double ranMs)
        {
            if (!on) return "off: this game's switch (Patches.ComfortMeHighlight)";
            if (!found) return "not needed: ComfortMe is not installed";
            if (problem != null) return "stepped aside: " + problem;
            string line = "working · ComfortMe " + version;
            long all = ran + skipped;
            if (all == 0) return line + " · no clearing yet";
            return line + string.Format(CultureInfo.InvariantCulture, " · {0} of {1} highlight clearings skipped, {2:0.00} ms each when run", skipped, all, ran > 0 ? ranMs / ran : 0);
        }
    }
}
