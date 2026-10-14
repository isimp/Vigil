using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// A restart the server tooling announces before it stops a server with players on: a file of
    /// named values ("seconds=60;reason=mod update") in the server's Vigil-state folder. The
    /// server's Vigil tells every player at once, and again with <see cref="Marks"/> seconds left.
    /// </summary>
    internal sealed class RestartNotice
    {
        public const string FileName = "restart.txt";
        public const int MaxSeconds = 600;

        /// <summary>The seconds left at which players hear of it again.</summary>
        public static readonly int[] Marks = { 30, 10 };

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public double Seconds { get; private set; }

        public string Reason { get; private set; }

        /// <summary>The notice in a file's words; null for one that is missing, damaged or asks for no time or too much.</summary>
        public static RestartNotice Parse(string text)
        {
            var d = KeyValues.Decode(text);
            double seconds = KeyValues.Number(d, "seconds");
            if (double.IsNaN(seconds) || seconds <= 0 || seconds > MaxSeconds) return null;
            d.TryGetValue("reason", out string reason);
            return new RestartNotice { Seconds = seconds, Reason = string.IsNullOrEmpty(reason) ? null : reason };
        }

        public static string Format(int seconds, string reason) =>
            KeyValues.Encode(new Dictionary<string, object> { { "seconds", seconds }, { "reason", reason } });

        /// <summary>Whether players hear of it again between two moments: when one of the marks was passed.</summary>
        public static bool Due(double leftBefore, double leftNow)
        {
            foreach (int mark in Marks)
                if (leftBefore > mark && leftNow <= mark) return true;
            return false;
        }

        public static string Message(double left, string reason) =>
            "The server restarts in " + Math.Max(0, Math.Round(left)).ToString("0", Ci) + " s" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
    }
}
