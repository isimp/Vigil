using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>The rules the self-test's routines share: which report to fetch, the test incident, how a round trip is judged.</summary>
    internal static class SelfTestRoutines
    {
        /// <summary>A question answered later than this is slow.</summary>
        public const double SlowSeconds = 5;

        /// <summary>The smallest report that cannot change while it is fetched: no summary or self-test, which a running session rewrites; null for none.</summary>
        public static ReportCatalog.Entry SmallestFixed(IEnumerable<ReportCatalog.Entry> entries) =>
            entries.Where(e => e.Kind != "summary" && e.Kind != "self-test" && e.Size > 0).OrderBy(e => e.Size).FirstOrDefault();

        public static string TestIncidentName(DateTime at) => "Vigil-incident-" + at.ToString("HHmmss", CultureInfo.InvariantCulture) + "-selftest.txt";

        public static string TestIncidentText(DateTime at) =>
            "Vigil incident: selftest   " + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" +
            "What triggered it: a self-test checking that shared reports reach the server whole\r\n" +
            "Nothing went wrong; this report only travels to the server and back.\r\n";

        public static SelfTestReport.Result Judge(bool answered, double seconds) =>
            !answered ? SelfTestReport.Result.Fail : seconds > SlowSeconds ? SelfTestReport.Result.Warn : SelfTestReport.Result.Pass;
    }
}
