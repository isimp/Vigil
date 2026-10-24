using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "Away players don't hold up sleep". The game skips the night once every
    /// player's character is in bed (<c>Game.EverybodyIsTryingToSleep</c>, asked by the server every
    /// 2 s). With the server's switch on, a player whose game menu has stayed open for the server's
    /// minutes is away (<see cref="AwayClock"/>), their game tells the server (<see cref="AwayReporter"/>,
    /// <see cref="AwayBook"/>), and the night is skipped once everyone else is in bed and somebody
    /// really is (<see cref="Judge"/>). An away player out of bed gets no morning: no Rested, no
    /// "good morning", no night counted (<see cref="LeftOutOfSleep"/>). The sleepers are told who was
    /// not counted (<see cref="Notice"/>).
    /// </summary>
    internal static class AwaySleep
    {
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

        public const int MinMinutes = 1, MaxMinutes = 60;

        /// <summary>The admin's request for the server's self-test of the sleep check.</summary>
        public const string CheckCommand = "awaycheck";

        /// <summary>The server's minutes in seconds, kept between <see cref="MinMinutes"/> and <see cref="MaxMinutes"/>.</summary>
        public static double LimitSeconds(int minutes) => Math.Max(MinMinutes, Math.Min(MaxMinutes, minutes)) * 60.0;

        /// <summary>Whether the night is skipped for these players, and who of them is away and not counted.</summary>
        public static SleepVerdict Judge(IEnumerable<Sleeper> players)
        {
            var list = (players ?? Enumerable.Empty<Sleeper>()).ToList();
            bool skip = list.Any(p => p.InBed) && list.All(p => p.InBed || p.Away);
            var away = skip ? list.Where(p => !p.InBed).Select(p => p.Name).ToList() : new List<string>();
            return new SleepVerdict(skip, away);
        }

        /// <summary>Whether this game leaves its player out of the server's "sleep now": away and not in bed, with the server's switch on.</summary>
        public static bool LeftOutOfSleep(bool sleep, bool on, bool away, bool inBed) => sleep && on && away && !inBed;

        /// <summary>The line for the players in bed when the night is skipped with away players left out; null when nobody was.</summary>
        public static string Notice(IReadOnlyList<string> awayNames)
        {
            if (awayNames == null || awayNames.Count == 0) return null;
            string names = awayNames.Count == 1 ? awayNames[0]
                : string.Join(", ", awayNames.Take(awayNames.Count - 1).ToArray()) + " and " + awayNames[awayNames.Count - 1];
            return "Skipping the night: " + names + (awayNames.Count == 1 ? " is" : " are") + " away and not counted";
        }

        /// <summary>
        /// What the patch does on this game, in words: off, stepped aside (<paramref name="problem"/>),
        /// not on a server that takes the reports (<paramref name="lacking"/>), or how the player counts now.
        /// </summary>
        public static string StateLine(bool on, string problem, string lacking, bool inWorld, double menuSeconds, double limitSeconds, bool away)
        {
            if (!on) return "off: the server's switch";
            if (problem != null) return "stepped aside: " + problem;
            if (!inWorld) return "join a world";
            if (lacking != null) return "not here: " + lacking;
            if (away) return "working · away: not counted for sleep while the game menu stays open";
            if (double.IsNaN(menuSeconds)) return "working · you count for sleep; away after " + Clock(limitSeconds) + " in the game menu";
            return "working · in the game menu " + Clock(menuSeconds) + " of " + Clock(limitSeconds);
        }

        /// <summary>One case of the server's self-test: the game's answer to "is everybody asleep" against the one expected.</summary>
        public static (bool Passed, string Text) CheckLine(string label, bool expected, bool got) =>
            (expected == got, label + ": " + Words(got) + (expected == got ? ", as expected" : ", expected " + Words(expected)));

        private static string Words(bool skip) => skip ? "skip" : "no skip";

        /// <summary>Minutes and seconds, such as 5:00.</summary>
        public static string Clock(double seconds)
        {
            int s = (int)Math.Max(0, Math.Floor(seconds));
            return (s / 60).ToString(Ic) + ":" + (s % 60).ToString("00", Ic);
        }
    }

    /// <summary>A player as the server's sleep check sees them: in bed, and whether their game reported them away.</summary>
    internal readonly struct Sleeper
    {
        public Sleeper(string name, bool inBed, bool away)
        {
            Name = name;
            InBed = inBed;
            Away = away;
        }

        public string Name { get; }

        public bool InBed { get; }

        public bool Away { get; }
    }

    /// <summary>The sleep check's answer: whether the night is skipped, and the away players it left out.</summary>
    internal sealed class SleepVerdict
    {
        public SleepVerdict(bool skip, List<string> awayNames)
        {
            Skip = skip;
            AwayNames = awayNames;
        }

        public bool Skip { get; }

        public List<string> AwayNames { get; }
    }

    /// <summary>
    /// On a player's game: how long the game menu has been open without a break, on the real clock
    /// (a paused world stops the game's own clock). Away once it has stayed open for the limit;
    /// closing it, or leaving the world, makes the player present again and starts the minutes over.
    /// Input does not matter: a player moving the mouse in the menu is not playing either.
    /// </summary>
    internal sealed class AwayClock
    {
        private double _openedAt = double.NaN;

        public bool Away { get; private set; }

        public void Update(bool inWorld, bool menuOpen, double now, double limitSeconds)
        {
            if (!inWorld || !menuOpen)
            {
                _openedAt = double.NaN;
                Away = false;
                return;
            }

            if (double.IsNaN(_openedAt)) _openedAt = now;
            Away = now - _openedAt >= limitSeconds;
        }

        /// <summary>Seconds the menu has been open; NaN while it is closed.</summary>
        public double MenuSeconds(double now) => double.IsNaN(_openedAt) ? double.NaN : now - _openedAt;
    }

    /// <summary>One away report: away or back, and whether it came from a self-test, which the server keeps but never counts.</summary>
    internal readonly struct AwayReport
    {
        public AwayReport(bool away, bool trial)
        {
            Away = away;
            Trial = trial;
        }

        public bool Away { get; }

        public bool Trial { get; }
    }

    /// <summary>
    /// On a player's game: what to tell the server, only when it changes. Away is told while the
    /// server's switch is on, or for a self-test. A server that does not take the reports hears
    /// nothing, and a new connection starts from "present", so an away player is told again.
    /// </summary>
    internal sealed class AwayReporter
    {
        private AwayReport _told = new AwayReport(false, false);

        /// <summary>The report to send now, or null when the server already has it.</summary>
        public AwayReport? Next(bool on, bool away, bool trial, bool serverTakes)
        {
            if (!serverTakes)
            {
                _told = new AwayReport(false, false);
                return null;
            }

            var wanted = away && (on || trial) ? new AwayReport(true, trial) : new AwayReport(false, false);
            if (wanted.Away == _told.Away && wanted.Trial == _told.Trial) return null;
            _told = wanted;
            return wanted;
        }
    }

    /// <summary>On the server: the last away report of each connected player's game, by the connection's id.</summary>
    internal sealed class AwayBook
    {
        private readonly Dictionary<long, Entry> _entries = new Dictionary<long, Entry>();

        public sealed class Entry
        {
            public string Name;
            public bool Away, Trial;
        }

        public int Count => _entries.Count;

        public void Report(long uid, string name, bool away, bool trial)
        {
            if (!away)
            {
                _entries.Remove(uid);
                return;
            }

            _entries[uid] = new Entry { Name = name, Away = true, Trial = trial };
        }

        /// <summary>Whether this player counts as away for sleep; a self-test's report does not.</summary>
        public bool IsAway(long uid) => _entries.TryGetValue(uid, out var e) && e.Away && !e.Trial;

        public bool TryGet(long uid, out Entry entry) => _entries.TryGetValue(uid, out entry);

        /// <summary>Forgets every player not among <paramref name="connected"/>.</summary>
        public void KeepOnly(IEnumerable<long> connected)
        {
            var keep = new HashSet<long>(connected ?? Enumerable.Empty<long>());
            foreach (long uid in _entries.Keys.Where(k => !keep.Contains(k)).ToList()) _entries.Remove(uid);
        }
    }
}
