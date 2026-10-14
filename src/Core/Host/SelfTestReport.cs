using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The text of a self-test: a top line that says how it went, then what failed, what needs a
    /// look, what passed and what is only for information, each in the order checked and named by
    /// its area. Nothing checked never reads as success.
    /// </summary>
    internal static class SelfTestReport
    {
        /// <summary>The outcome of one check; <see cref="Info"/> notes something that is neither good nor bad.</summary>
        internal enum Result { Pass, Info, Warn, Fail }

        internal sealed class Check
        {
            public Check(string area, string what, Result result, string detail)
            {
                Area = area;
                What = what;
                Outcome = result;
                Detail = detail;
            }

            public string Area { get; }

            public string What { get; }

            public Result Outcome { get; }

            public string Detail { get; }
        }

        private static readonly Result[] Order = { Result.Fail, Result.Warn, Result.Pass, Result.Info };

        /// <param name="machine">"this game" or "the server".</param>
        /// <param name="checks">The checks in the order they ran.</param>
        public static List<string> Lines(string machine, List<Check> checks)
        {
            var lines = new List<string> { "Self-test of " + machine + ": " + Summary(checks) };
            foreach (var result in Order)
                foreach (var c in checks.Where(c => c.Outcome == result))
                    lines.Add(Mark(result) + c.Area + ": " + c.What + (string.IsNullOrEmpty(c.Detail) ? "" : " - " + c.Detail));
            return lines;
        }

        /// <summary>The worst outcome: a failure, else a warning, else passed.</summary>
        public static Result Worst(List<Check> checks) =>
            checks.Any(c => c.Outcome == Result.Fail) ? Result.Fail : checks.Any(c => c.Outcome == Result.Warn) ? Result.Warn : Result.Pass;

        private static string Summary(List<Check> checks)
        {
            if (checks.Count == 0) return "nothing was checked";
            int fail = checks.Count(c => c.Outcome == Result.Fail), warn = checks.Count(c => c.Outcome == Result.Warn);
            int pass = checks.Count(c => c.Outcome == Result.Pass), info = checks.Count(c => c.Outcome == Result.Info);
            string infoPart = info > 0 ? ", " + Count(info) + " for information" : "";
            if (fail == 0 && warn == 0) return "all " + Count(pass) + " passed" + infoPart;
            var parts = new List<string>();
            if (fail > 0) parts.Add(Count(fail) + " failed");
            if (warn > 0) parts.Add(Count(warn) + (warn == 1 ? " warning" : " warnings"));
            if (pass > 0) parts.Add(Count(pass) + " passed");
            return string.Join(", ", parts) + infoPart;
        }

        /// <summary>A report as the panel shows it: its top line's verdict, the worst result, and each check.</summary>
        internal sealed class Shown
        {
            public string Summary;
            public Result Worst;
            public List<Check> Checks { get; } = new List<Check>();
        }

        /// <summary>
        /// Reads a report's lines back (the server's comes as text): the verdict after "Self-test of
        /// ...: ", and each check from its mark, area, what and detail. A line without a mark is kept
        /// as information; a report with no check read is worth a look, never a pass.
        /// </summary>
        public static Shown Read(List<string> lines)
        {
            var shown = new Shown();
            if (lines == null || lines.Count == 0) return new Shown { Summary = "", Worst = Result.Warn };
            string top = lines[0] ?? "";
            int colon = top.StartsWith("Self-test of ", System.StringComparison.Ordinal) ? top.IndexOf(": ", System.StringComparison.Ordinal) : -1;
            shown.Summary = colon < 0 ? top : top.Substring(colon + 2);
            foreach (var line in lines.Skip(1).Where(l => !string.IsNullOrEmpty(l)))
                shown.Checks.Add(ReadCheck(line));
            shown.Worst = colon < 0 || shown.Checks.Count == 0 ? Result.Warn : Worst(shown.Checks);
            return shown;
        }

        private static Check ReadCheck(string line)
        {
            Result result;
            if (line.StartsWith("FAIL  ", System.StringComparison.Ordinal)) result = Result.Fail;
            else if (line.StartsWith("WARN  ", System.StringComparison.Ordinal)) result = Result.Warn;
            else if (line.StartsWith("ok    ", System.StringComparison.Ordinal)) result = Result.Pass;
            else if (line.StartsWith("info  ", System.StringComparison.Ordinal)) result = Result.Info;
            else return new Check("", line, Result.Info, null);

            string rest = line.Substring(6);
            int colon = rest.IndexOf(": ", System.StringComparison.Ordinal);
            string area = colon < 0 ? "" : rest.Substring(0, colon);
            rest = colon < 0 ? rest : rest.Substring(colon + 2);
            int dash = rest.IndexOf(" - ", System.StringComparison.Ordinal);
            return dash < 0 ? new Check(area, rest, result, null) : new Check(area, rest.Substring(0, dash), result, rest.Substring(dash + 3));
        }

        private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Mark(Result r)
        {
            switch (r)
            {
                case Result.Fail: return "FAIL  ";
                case Result.Warn: return "WARN  ";
                case Result.Pass: return "ok    ";
                default: return "info  ";
            }
        }
    }
}
