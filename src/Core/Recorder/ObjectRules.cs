using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>One row of a part: its label, its words and their tone.</summary>
    internal struct RulesRow
    {
        public string Label, Value;
        public ResultTone Tone;
    }

    /// <summary>What the inspector or a card shows for an object that follows rules of its own: rows, maybe a table, and a note.</summary>
    internal sealed class RulesPart
    {
        public string Title, Note;

        /// <summary>The part's main number where it has one: the comfort level.</summary>
        public int Number;
        public List<RulesRow> Rows = new List<RulesRow>();
        public string[] Head;
        public List<string[]> Table;

        public RulesPart Add(string label, string value, ResultTone tone = ResultTone.Quiet)
        {
            Rows.Add(new RulesRow { Label = label, Value = value, Tone = tone });
            return this;
        }
    }

    internal enum FermenterState { Empty, Fermenting, Ready }

    internal sealed class FermenterFacts
    {
        public string Content, Open;
        public FermenterState State;
        public double Elapsed, Duration;
        public int Blocked;
        public bool Roof;
    }

    internal sealed class BeehiveFacts
    {
        public int Blocked, Honey, MaxHoney;
        public float MaxCover, SecondsPerHoney, Product;
        public bool InBiome, DaylightOnly, Daylight;
        public string Biome;
    }

    internal sealed class PlantFacts
    {
        /// <summary>The game's status name: Healthy, NoSun, NoSpace, WrongBiome, NotCultivated, TooHot, TooCold, NoAttachPiece.</summary>
        public string Status, Kind, SkyBlocker;
        public List<string> InWay = new List<string>();
        public double GrowSeconds, MinSeconds, MaxSeconds, Since;
        public bool DestroyIfCantGrow;
    }

    internal sealed class ComfortPiece
    {
        public string Name, Group;
        public int GroupIndex, Comfort;

        /// <summary>Its comfort part is off (an unlit fire), so the game counts it as 0.</summary>
        public bool Inactive;
    }

    internal sealed class ComfortFacts
    {
        public int Blocked;
        public bool Roof;
        public List<ComfortPiece> Pieces = new List<ComfortPiece>();

        /// <summary>The comfort groups the game has, without None, in its order.</summary>
        public string[] Groups = new string[0];
    }

    internal sealed class SmelterFacts
    {
        public float Fuel;
        public int MaxFuel, Queued, MaxOre;
        public bool RequiresRoof, HaveRoof, SmokeBlocked;

        /// <summary>It has a smoke vent at all (a windmill or spinning wheel has none).</summary>
        public bool HasSmoke = true;
        public string VentBlocker;
    }

    internal sealed class FireFacts
    {
        public float Fuel, MaxFuel;
        public bool InfiniteFuel, UnderWater;
        public string WetReason, BlockedReason;
    }

    internal sealed class TameFacts
    {
        public bool Tamed, Hungry, Alerted;
        public double Remaining, Total;
    }

    internal sealed class BreedFacts
    {
        public bool Tamed, Pregnant, PartnerNear, Hungry, Alerted;
        public int Love, Required, Count, Max;
        public double PregnantLeft;

        /// <summary>The share of checks the game skips (<c>Procreation.m_pregnancyChance</c>: a roll at or under it skips the check).</summary>
        public float SkipShare;
    }

    /// <summary>
    /// The rules some objects follow, in words, by the game's code (assembly_valheim 1.0.20). Cover is
    /// the share of the 17 rays <c>Cover.GetCoverForPoint</c> casts that hit something solid. A
    /// fermenter needs 70 % cover and a roof, checked every 10 s, or its owner restarts the batch; a
    /// beehive works under its <c>m_maxCover</c>, in its biome and, for most, by daylight only; a crop
    /// grows when its time is up and it is healthy, and withers then if its kind does when it cannot;
    /// comfort counts only in shelter (80 % cover and a roof): 1, one for the shelter, and the best piece
    /// of each comfort group within 10 m, one of each name; rested lasts 300 s and 60 s per level above 1.
    /// </summary>
    internal static class ObjectRules
    {
        public const int Rays = 17;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static RulesPart Fermenting(FermenterFacts f)
        {
            var p = new RulesPart { Title = "Fermenting" };
            float cover = f.Blocked / (float)Rays;
            bool exposed = cover < 0.7f;
            switch (f.State)
            {
                case FermenterState.Empty:
                    p.Add("Now", "empty");
                    break;
                case FermenterState.Ready:
                    p.Add("Now", "ready: " + f.Content, ResultTone.Good);
                    break;
                default:
                    if (exposed || !f.Roof)
                    {
                        string why = exposed ? "cover " + Percent(cover) + ", it needs 70 %" : "no roof";
                        if (exposed && !f.Roof) why += " and a roof";
                        p.Add("Now", "starting over: " + why + "; the game restarts the batch every 10 s until then", ResultTone.Bad);
                    }
                    else
                    {
                        double done = f.Duration > 0 ? Math.Min(1, Math.Max(0, f.Elapsed / f.Duration)) : 0;
                        p.Add("Now", "fermenting " + f.Content + " · " + Percent(done) + " · ready in " + Left(f.Duration - f.Elapsed), ResultTone.Good);
                    }
                    break;
            }
            p.Add("Cover", Percent(cover) + " (" + f.Blocked.ToString(Ci) + " of 17 rays) · roof " + (f.Roof ? "yes" : "no"));
            p.Add("Needs", "70 % cover and a roof, checked every 10 s; if either fails, the game starts the batch over");
            if (!string.IsNullOrEmpty(f.Open)) p.Add("Open rays", f.Open);
            return p;
        }

        public static RulesPart Bees(BeehiveFacts b)
        {
            var p = new RulesPart { Title = "Bees" };
            float cover = b.Blocked / (float)Rays;
            int most = 0;
            while ((most + 1) / (float)Rays < b.MaxCover) most++;
            bool covered = b.MaxCover > 0f && cover >= b.MaxCover;
            if (covered) p.Add("Now", "not working: too covered", ResultTone.Caution);
            else if (!b.InBiome) p.Add("Now", "not working: wrong biome, it needs " + b.Biome, ResultTone.Caution);
            else if (b.DaylightOnly && !b.Daylight) p.Add("Now", "resting: bees work in daylight only", ResultTone.Caution);
            else if (b.Honey >= b.MaxHoney) p.Add("Now", "full: " + b.Honey.ToString(Ci) + " of " + b.MaxHoney.ToString(Ci) + ", take the honey", ResultTone.Caution);
            else p.Add("Now", "working · next honey in " + Left(b.SecondsPerHoney - b.Product), ResultTone.Good);
            if (b.MaxCover > 0f) p.Add("Cover", Percent(cover) + " (" + b.Blocked.ToString(Ci) + " of 17 rays) · needs under " + Percent(b.MaxCover) + " (at most " + most.ToString(Ci) + ")");
            p.Add("Also", "biome " + (b.InBiome ? "yes" : "no") + " · daylight " + (!b.DaylightOnly || b.Daylight ? "yes" : "no") + " · honey " + b.Honey.ToString(Ci) + " of " + b.MaxHoney.ToString(Ci)
                + ", one every " + SpawnLog.Ago(b.SecondsPerHoney) + " while working");
            return p;
        }

        public static RulesPart Growing(PlantFacts f)
        {
            var p = new RulesPart { Title = "Growing" };
            bool healthy = f.Status == "Healthy";
            p.Add("Now", StatusWords(f.Status), healthy ? ResultTone.Good : ResultTone.Caution);
            if (f.Status == "NoSpace" && f.InWay.Count > 0) p.Add("In its way", string.Join(" · ", f.InWay.ToArray()) + "; unhealthy plants do not count");
            p.Add("Sky", f.SkyBlocker == null ? "open (nothing above within 100 m)" : "blocked by " + f.SkyBlocker, f.SkyBlocker == null ? ResultTone.Quiet : ResultTone.Caution);
            double left = f.GrowSeconds - f.Since;
            string range = Math.Abs(f.MaxSeconds - f.MinSeconds) < 1 ? f.Kind + " " + SpawnLog.Ago(f.MinSeconds) : f.Kind + " " + SpawnLog.Ago(f.MinSeconds) + " to " + SpawnLog.Ago(f.MaxSeconds);
            string needs = " · this one needs " + SpawnLog.Ago(f.GrowSeconds) + ", " + range;
            string when;
            if (left <= 0) when = healthy ? "now: it grows at the next check, within 10 s" : "now if it were healthy; " + (f.DestroyIfCantGrow ? "it withers at the next check" : "it waits until it is");
            else if (healthy) when = "in " + Left(left);
            else when = "in " + Left(left) + " if it is healthy then; " + (f.DestroyIfCantGrow ? "if not, it withers (this kind does)" : "if not, it waits until it is");
            p.Add("Ready", when + needs);
            return p;
        }

        public static RulesPart Rested(ComfortFacts c)
        {
            var p = new RulesPart { Title = "Rested and comfort", Head = new[] { "Piece (10 m)", "Group", "Adds" }, Table = new List<string[]>() };
            float cover = c.Blocked / (float)Rays;
            bool shelter = cover >= 0.8f && c.Roof;
            if (shelter) p.Add("Shelter", "yes · cover " + Percent(cover) + " (" + c.Blocked.ToString(Ci) + " of 17) · roof yes", ResultTone.Good);
            else
            {
                string why = cover < 0.8f ? "cover " + Percent(cover) + " (" + c.Blocked.ToString(Ci) + " of 17), it needs 80 %" + (c.Roof ? " and a roof" : " and a roof, which it lacks") : "cover " + Percent(cover) + " (" + c.Blocked.ToString(Ci) + " of 17), no roof";
                p.Add("Shelter", "no: " + why, ResultTone.Caution);
            }

            // The game's order: by group, the most comfort first, then by name; a piece counts unless the one before it shares its group (other than None) or its name.
            var sorted = c.Pieces.OrderBy(x => x.GroupIndex).ThenByDescending(x => x.Comfort).ThenByDescending(x => x.Name, StringComparer.Ordinal).ToList();
            int sum = 0;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Count; i++)
            {
                var x = sorted[i];
                string adds;
                if (i > 0 && x.GroupIndex != 0 && x.GroupIndex == sorted[i - 1].GroupIndex) adds = "0 · not the best of its group";
                else if (i > 0 && x.Name == sorted[i - 1].Name) adds = "0 · the same piece counts once";
                else
                {
                    adds = x.Inactive ? "0 · not active (an unlit fire counts nothing)" : x.Comfort.ToString(Ci);
                    sum += x.Comfort;
                    if (!x.Inactive) counted.Add(x.Group);
                }
                p.Table.Add(new[] { x.Name, x.Group, shelter ? adds : "0 · out of shelter" });
            }
            int comfort = 1 + (shelter ? 1 + sum : 0);
            p.Number = comfort;
            p.Add("Comfort", comfort.ToString(Ci) + " → rested " + SpawnLog.Ago(300 + (comfort - 1) * 60.0));
            var missing = c.Groups.Where(g => !counted.Contains(g)).ToArray();
            p.Note = "Out of shelter comfort stays 1." + (missing.Length > 0 ? " Missing groups: " + string.Join(", ", missing) + "." : "");
            return p;
        }

        public static RulesPart Smelting(SmelterFacts s)
        {
            var p = new RulesPart { Title = "Smelting" };
            if (s.MaxFuel > 0 && s.Fuel <= 0f) p.Add("Now", "stopped: no fuel", ResultTone.Caution);
            else if (s.MaxOre > 0 && s.Queued <= 0) p.Add("Now", "idle: nothing queued");
            else if (s.RequiresRoof && !s.HaveRoof) p.Add("Now", "stopped: it needs a roof", ResultTone.Bad);
            else if (s.SmokeBlocked) p.Add("Now", "stopped: its smoke is blocked", ResultTone.Bad);
            else p.Add("Now", "working", ResultTone.Good);
            var fill = new List<string>();
            if (s.MaxFuel > 0) fill.Add("fuel " + Math.Round(s.Fuel).ToString("0", Ci) + " of " + s.MaxFuel.ToString(Ci));
            if (s.MaxOre > 0) fill.Add("ore " + s.Queued.ToString(Ci) + " queued of " + s.MaxOre.ToString(Ci));
            if (fill.Count > 0) p.Add("Fill", string.Join(" · ", fill.ToArray()));
            if (s.HasSmoke) p.Add("Smoke", s.SmokeBlocked ? "blocked at the vent" + (string.IsNullOrEmpty(s.VentBlocker) ? "" : " by " + s.VentBlocker) : "smoke gets out", s.SmokeBlocked ? ResultTone.Bad : ResultTone.Quiet);
            if (s.RequiresRoof) p.Add("Roof", s.HaveRoof ? "yes" : "no, and it needs one");
            return p;
        }

        public static RulesPart Burning(FireFacts f)
        {
            var p = new RulesPart { Title = "Fire" };
            if (!string.IsNullOrEmpty(f.BlockedReason)) p.Add("Now", "out: " + f.BlockedReason, ResultTone.Bad);
            else if (f.UnderWater) p.Add("Now", "out: under water", ResultTone.Bad);
            else if (f.Fuel <= 0f && !f.InfiniteFuel) p.Add("Now", "out: no fuel", ResultTone.Caution);
            else if (!string.IsNullOrEmpty(f.WetReason)) p.Add("Now", "burning, low flame: " + f.WetReason + " (a wet fire keeps burning)", ResultTone.Caution);
            else p.Add("Now", "burning", ResultTone.Good);
            if (!f.InfiniteFuel && f.MaxFuel > 0f) p.Add("Fuel", "fuel " + Math.Round(f.Fuel).ToString("0", Ci) + " of " + Math.Round(f.MaxFuel).ToString("0", Ci));
            return p;
        }

        public static RulesPart Taming(TameFacts t)
        {
            var p = new RulesPart { Title = "Taming" };
            if (t.Tamed) return p.Add("Taming", "tamed", ResultTone.Good);
            double done = t.Total > 0 ? Math.Min(1, Math.Max(0, 1 - t.Remaining / t.Total)) : 0;
            string head = Percent(done) + " · " + Left(t.Remaining) + " left · ";
            if (t.Hungry) return p.Add("Taming", head + "paused: hungry, feed it", ResultTone.Caution);
            if (t.Alerted) return p.Add("Taming", head + "paused: alarmed, it calms only when nothing threatens it", ResultTone.Caution);
            return p.Add("Taming", head + "going on", ResultTone.Good);
        }

        public static RulesPart Breeding(BreedFacts b)
        {
            var p = new RulesPart { Title = "Breeding" };
            if (!b.Tamed) return p.Add("Breeding", "only tamed ones breed");
            if (b.Pregnant) p.Add("Breeding", "expecting: born in " + Left(b.PregnantLeft), ResultTone.Good);
            else if (b.Hungry) p.Add("Breeding", "paused: hungry", ResultTone.Caution);
            else if (b.Alerted) p.Add("Breeding", "paused: alarmed", ResultTone.Caution);
            else if (b.Count >= b.Max) p.Add("Breeding", "none: " + b.Count.ToString(Ci) + " of " + b.Max.ToString(Ci) + " of its kind within 10 m, young included", ResultTone.Caution);
            else if (!b.PartnerNear) p.Add("Breeding", "none: no partner within 3 m", ResultTone.Caution);
            else p.Add("Breeding", "trying: love " + b.Love.ToString(Ci) + " of " + b.Required.ToString(Ci), ResultTone.Good);
            float counts = 1f - b.SkipShare;
            string share = Math.Abs(counts - 0.5f) < 0.01f ? "half the checks count" : Percent(counts) + " of the checks count";
            p.Add("Also", "partner within 3 m " + (b.PartnerNear ? "yes" : "no") + " · love " + b.Love.ToString(Ci) + " of " + b.Required.ToString(Ci) + " · " + share + ", one every 10 to 15 s");
            return p;
        }

        private static string StatusWords(string status) => status switch
        {
            "Healthy" => "healthy",
            "NoSun" => "no sun",
            "NoSpace" => "no space",
            "WrongBiome" => "wrong biome",
            "NotCultivated" => "needs cultivated ground",
            "TooHot" => "too hot: it needs a shield generator here",
            "TooCold" => "too cold: it needs a shield generator here",
            "NoAttachPiece" => "nothing to grow on",
            _ => status ?? "unknown",
        };

        /// <summary>A time still to wait, rounded up: "45 s", "15 min", "1 h 12 min".</summary>
        public static string Left(double seconds)
        {
            if (seconds <= 0) return "0 s";
            if (seconds < 60) return Math.Ceiling(seconds).ToString("0", Ci) + " s";
            int minutes = (int)Math.Ceiling(seconds / 60);
            return minutes < 60 ? minutes.ToString(Ci) + " min" : (minutes / 60).ToString(Ci) + " h " + (minutes % 60).ToString(Ci) + " min";
        }

        private static string Percent(double v) => Math.Round(v * 100).ToString("0", Ci) + " %";
    }
}
