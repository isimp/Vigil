using System;
using System.Collections.Generic;
using System.Linq;

namespace Vigil.Core
{
    internal enum GreetingState { NotAsked, Waiting, Answered, NoAnswer }

    /// <summary>
    /// What the player's Vigil knows about the server's, from the greeting it sends on connecting:
    /// the server's version, the capabilities it names ("load,players,recording,status,selftest")
    /// and whether this player is one of its admins. A feature asks <see cref="Lacking"/> for the
    /// capability it needs rather than for an equal version, so a server one version behind still
    /// serves what it has. An answer counts whenever it comes, also after the wait was given up.
    /// </summary>
    internal sealed class Greeting
    {
        public const string Load = "load", Players = "players", Recording = "recording", Status = "status", SelfTest = "selftest", Link = "link", Reports = "reports", Share = "share", Parity = "parity";

        /// <summary>A server that keeps the spawn logs players send and shows them merged to its admins.</summary>
        public const string SpawnLog = "spawnlog";

        /// <summary>A server that reads the whole world for its admins: objects of removed mods, load-test entries in the player history.</summary>
        public const string Leftovers = "leftovers";

        /// <summary>A server that keeps a week of alerts for its admins (drops, falling behind, crashes and more) and hands out the new ones.</summary>
        public const string Alerts = "alerts";

        /// <summary>What this Vigil can do, as it tells the other side.</summary>
        public const string Own = Load + "," + Players + "," + Recording + "," + Status + "," + SelfTest + "," + Link + "," + Reports + "," + Parity + "," + Leftovers + "," + Alerts;

        /// <summary>Whether any player, not only an admin, may ask the server's Vigil this: to take a shared report or spawn log, or for its mods and configs.</summary>
        public static bool ForEveryone(string command) => command == Share || command == SpawnLog || command == Parity;

        public const double WaitSeconds = 10;

        private readonly List<string> _caps = new List<string>();
        private double _askedAt;

        public GreetingState State { get; private set; }

        public bool IsAdmin { get; private set; }

        public string Version { get; private set; }

        public void Asked(double now)
        {
            if (State == GreetingState.Answered) return;
            State = GreetingState.Waiting;
            _askedAt = now;
        }

        public void Answer(string version, string capabilities, bool admin, double now)
        {
            Version = string.IsNullOrWhiteSpace(version) ? "(unknown version)" : version.Trim();
            IsAdmin = admin;
            _caps.Clear();
            foreach (var c in (capabilities ?? "").Split(','))
            {
                string name = c.Trim().ToLowerInvariant();
                if (name.Length > 0 && !_caps.Contains(name)) _caps.Add(name);
            }
            State = GreetingState.Answered;
        }

        /// <summary>Gives up waiting once <see cref="WaitSeconds"/> have passed without an answer.</summary>
        public void Tick(double now)
        {
            if (State == GreetingState.Waiting && now - _askedAt > WaitSeconds) State = GreetingState.NoAnswer;
        }

        /// <summary>A new connection: nothing is known about the server any more.</summary>
        public void Reset()
        {
            State = GreetingState.NotAsked;
            IsAdmin = false;
            Version = null;
            _caps.Clear();
        }

        public bool Has(string name) => State == GreetingState.Answered && _caps.Contains(name);

        public string Describe()
        {
            switch (State)
            {
                case GreetingState.NotAsked: return "not connected to a dedicated server";
                case GreetingState.Waiting: return "waiting for the server's Vigil to answer";
                case GreetingState.NoAnswer: return "the server has no Vigil, or one too old to answer";
                default:
                    return "Vigil " + Version + " on the server, " + (IsAdmin ? "you are an admin" : "you are not an admin")
                        + (_caps.Count > 0 ? ": " + string.Join(", ", _caps.ToArray()) : "");
            }
        }

        /// <summary>
        /// Null when the server can do it for this player; otherwise what to tell the player.
        /// <paramref name="what"/> completes "the server's Vigil cannot ...".
        /// </summary>
        public string Lacking(string name, string what, bool adminOnly = false)
        {
            if (State != GreetingState.Answered) return Describe();
            if (!_caps.Contains(name)) return "the server's Vigil " + Version + " cannot " + what + " yet; update it";
            if (adminOnly && !IsAdmin) return "only the server's admins (adminlist.txt) can " + what;
            return null;
        }
    }
}
