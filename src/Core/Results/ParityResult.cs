using System;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// A comparison with the server's mods and configs as the panel shows it: a verdict, the counts,
    /// then the mods whose versions differ, the configs with other settings, and the mods only one
    /// side has.
    /// </summary>
    internal static class ParityResult
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly string[] VersionHeaders = { "Mod", "Here", "Server" };
        private static readonly bool[] VersionRight = { false, true, true };
        private static readonly string[] FileHeaders = { "Config file" };
        private static readonly bool[] FileRight = { false };
        private static readonly string[] ModHeaders = { "Mod", "Version" };
        private static readonly bool[] ModRight = { false, true };

        public static Result Build(Parity.Report r, DateTime at)
        {
            var result = new Result("Mods and configs against the server", at);
            int versions = r.VersionsDiffer.Count, configs = r.ConfigsDiffer.Count;
            if (versions > 0)
            {
                result.Badge = versions.ToString(Ci) + (versions == 1 ? " mod differs" : " mods differ") + " in version";
                result.BadgeTone = ResultTone.Caution;
            }
            else if (configs > 0)
            {
                result.Badge = configs.ToString(Ci) + (configs == 1 ? " config differs" : " configs differ");
                result.BadgeTone = ResultTone.Caution;
            }
            else
            {
                result.Badge = "same mods and settings";
                result.BadgeTone = ResultTone.Good;
            }

            result.Add(new VerdictBlock(result.BadgeTone, versions == 0 && configs == 0
                ? "Every mod both sides have is at the same version, and every config both sides have holds the same settings."
                : string.Format(Ci, "{0} of the mods both sides have differ in version; {1} of the configs both sides have hold other settings.", versions, configs)));
            result.Add(new TilesBlock())
                .Add("Same mods", r.ModsSame.ToString(Ci))
                .Add("Versions differ", versions.ToString(Ci))
                .Add("Only on the server", r.OnlyTheirs.Count.ToString(Ci))
                .Add("Only here", r.OnlyMine.Count.ToString(Ci))
                .Add("Configs differ", configs.ToString(Ci));

            if (versions > 0)
            {
                var t = result.Add(new TableBlock("Mods whose versions differ", VersionHeaders, VersionRight));
                foreach (var v in r.VersionsDiffer)
                {
                    t.Rows.Add(new[] { v.Name, v.Mine, v.Theirs });
                }
            }

            if (configs > 0)
            {
                var t = result.Add(new TableBlock("Configs with other settings", FileHeaders, FileRight));
                foreach (var c in r.ConfigsDiffer)
                {
                    t.Rows.Add(new[] { c });
                }
            }

            if (r.OnlyTheirs.Count > 0)
            {
                var t = result.Add(new TableBlock("Mods only the server has", ModHeaders, ModRight));
                foreach (var m in r.OnlyTheirs)
                {
                    t.Rows.Add(new[] { m.Name, m.Version });
                }
            }

            if (r.OnlyMine.Count > 0)
            {
                var t = result.Add(new TableBlock("Mods only this game has", ModHeaders, ModRight));
                foreach (var m in r.OnlyMine)
                {
                    t.Rows.Add(new[] { m.Name, m.Version });
                }
            }

            result.Add(new TextBlock(null, string.Format(Ci, "{0} configs match; {1} are only on the server, {2} only here. Some differ on purpose: settings the server keeps for itself, and mods that run on one side only.",
                r.ConfigsSame, r.ConfigsOnlyTheirs.Count, r.ConfigsOnlyMine)));
            return result;
        }
    }
}
