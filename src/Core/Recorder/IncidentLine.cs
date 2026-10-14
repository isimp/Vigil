using System;

namespace Vigil.Core
{
    /// <summary>
    /// One of the session's incidents as the recorder keeps it, "time kind: detail (file)", split
    /// for the panel's list. A line of another shape is all detail.
    /// </summary>
    internal sealed class IncidentLine
    {
        public string Time = "", Kind = "", Detail = "", File = "";

        public static IncidentLine Parse(string line)
        {
            line = line ?? "";
            var whole = new IncidentLine { Detail = line };
            int space = line.IndexOf(' ');
            int colon = space < 0 ? -1 : line.IndexOf(": ", space, StringComparison.Ordinal);
            if (space != 8 || colon < 0 || line[2] != ':' || line[5] != ':') return whole;

            string rest = line.Substring(colon + 2);
            string file = "";
            int open = rest.LastIndexOf(" (", StringComparison.Ordinal);
            if (open >= 0 && rest.EndsWith(")", StringComparison.Ordinal))
            {
                file = rest.Substring(open + 2, rest.Length - open - 3);
                rest = rest.Substring(0, open);
            }
            return new IncidentLine { Time = line.Substring(0, 8), Kind = line.Substring(space + 1, colon - space - 1), Detail = rest, File = file };
        }
    }
}
