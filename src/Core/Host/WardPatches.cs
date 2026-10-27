using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Vigil.Core
{
    /// <summary>
    /// The game patch "Wards repair the whole connected base", with "Wards repair by themselves":
    /// repair started at one ward (Protective Wards' alt-use) starts it on every active ward chained to
    /// it by overlapping areas, and each ward repairs its own area, one damaged piece a turn, until
    /// none is left. Each ward's turn runs on the game that owns the ward, so the work goes on while
    /// any player is near; the dedicated server loads no bases. Protective Wards' own repair loop
    /// reaches only the wards overlapping the one used, one piece every 10 s for all of them, and
    /// stops when its player walks away.
    /// </summary>
    internal static class WardRepair
    {
        public const int MinSeconds = 2;
        public const int MaxSeconds = 120;
        public const int DefaultSeconds = 10;

        /// <summary>The hover's count when the ward is not repairing (no line shown).</summary>
        public const int NotRepairing = -1;

        public enum Step
        {
            Idle,
            RepairOne,
            Finish,
        }

        public enum Start
        {
            /// <summary>Both switches off: Protective Wards' own repair runs.</summary>
            ProtectiveWards,

            /// <summary>Only wards repairing by themselves: the used ward starts at once.</summary>
            ThisWard,

            WholeNetwork,
        }

        public static int ClampSeconds(int seconds) => Math.Max(MinSeconds, Math.Min(MaxSeconds, seconds));

        /// <summary>
        /// Seconds a ward keeps its list of the pieces in its area. Finding them goes through every
        /// loaded piece of the game, so a turn checks the kept list for damage instead; a piece built
        /// since is seen at the next look.
        /// </summary>
        public const double PieceListSeconds = 30;

        /// <summary>Whether a ward's list of pieces is due to be looked up again.</summary>
        public static bool PiecesDue(double builtAt, double now) => now - builtAt >= PieceListSeconds;

        /// <summary>
        /// The used ward and every active ward reached from it through overlapping areas, each once,
        /// the used ward first. A switched-off ward neither joins nor carries the chain on.
        /// </summary>
        public static List<T> Network<T>(T start, IEnumerable<T> wards, Func<T, bool> active, Func<T, T, bool> overlap)
            where T : class
        {
            var all = wards.Where(w => w != null && w != start && active(w)).Distinct().ToList();
            var reached = new List<T> { start };
            for (int i = 0; i < reached.Count; i++)
            {
                var from = reached[i];
                for (int j = all.Count - 1; j >= 0; j--)
                {
                    if (!overlap(from, all[j])) continue;
                    reached.Add(all[j]);
                    all.RemoveAt(j);
                }
            }
            return reached;
        }

        /// <summary>What a ward does on its turn, on the game that owns it.</summary>
        public static Step Next(bool owner, bool active, bool started, bool auto, int damaged)
        {
            if (!owner) return Step.Idle;
            if (!active) return started ? Step.Finish : Step.Idle;
            if (damaged > 0 && (started || auto)) return Step.RepairOne;
            return started ? Step.Finish : Step.Idle;
        }

        /// <summary>The pieces the ward's hover shows as left after its turn, or <see cref="NotRepairing"/>.</summary>
        public static int LeftAfter(Step step, int damaged) => step == Step.RepairOne && damaged > 1 ? damaged - 1 : NotRepairing;

        /// <summary>What a ward's alt-use (Protective Wards' "start repair") does by the server's switches.</summary>
        public static Start OnStart(bool network, bool auto) => network ? Start.WholeNetwork : auto ? Start.ThisWard : Start.ProtectiveWards;

        /// <summary>The switches and pace this game works by.</summary>
        internal readonly struct Switches
        {
            public Switches(bool network, bool auto, double seconds)
            {
                Network = network;
                Auto = auto;
                Seconds = seconds;
            }

            public bool Network { get; }

            public bool Auto { get; }

            public double Seconds { get; }
        }

        /// <summary>
        /// The self-test's trial: switches on for the self-test's own ward on this game alone, at a pace
        /// of its own, for a few seconds; it ends by itself, never switches off what the server has on,
        /// and leaves every other ward to the server's switches.
        /// </summary>
        internal sealed class Trial
        {
            private double _until = double.NegativeInfinity;
            private bool _network, _auto;
            private double _seconds;

            public void Start(bool network, bool auto, double seconds, double now, double lasts)
            {
                _network = network;
                _auto = auto;
                _seconds = seconds;
                _until = now + lasts;
            }

            public void End() => _until = double.NegativeInfinity;

            public bool Active(double now) => now < _until;

            public Switches Effective(bool network, bool auto, double seconds, double now, bool testWard) =>
                testWard && Active(now) ? new Switches(network || _network, auto || _auto, _seconds) : new Switches(network, auto, seconds);
        }

        /// <summary>
        /// The self-test's verdict on starting repair: every ward of the chain that this game owns has
        /// to have started; the others are asked through their owners' games, which the test cannot see.
        /// </summary>
        public static WardVerdict ChainCheck(int chain, int ownedHere, int startedHere, int damaged)
        {
            if (chain == 0) return new WardVerdict(SelfTestReport.Result.Fail, "the ward's alt-use did not reach Vigil's repair");
            string head = (chain == 1 ? "1 ward" : chain + " wards in the chain") + ", " + damaged + (damaged == 1 ? " damaged piece" : " damaged pieces");
            if (startedHere < ownedHere)
            {
                return new WardVerdict(SelfTestReport.Result.Fail, head + "; only " + startedHere + " of the " + ownedHere + " owned by this game started");
            }
            if (chain == 1 && ownedHere == 1) return new WardVerdict(SelfTestReport.Result.Pass, head + "; it started");
            int elsewhere = chain - ownedHere;
            string asked = elsewhere == 0 ? "" : elsewhere == 1 ? ", 1 asked through its owner's game" : ", " + elsewhere + " asked through their owners' games";
            return new WardVerdict(SelfTestReport.Result.Pass, head + "; the " + ownedHere + " owned by this game started" + asked);
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool network, bool auto, bool found, string version, string problem)
        {
            if (!network && !auto) return "off: the server's switches";
            if (!found) return "not needed: Protective Wards is not installed";
            if (problem != null) return "stepped aside: " + problem;
            var what = new List<string>();
            if (network) what.Add("the whole connected base");
            if (auto) what.Add("wards repair by themselves");
            return "working · Protective Wards " + version + " · " + string.Join(", ", what);
        }
    }

    /// <summary>
    /// The game patch "Server password for wards": a player without access to an active ward is asked
    /// for a password at it (Protective Wards' own prompt); the server's password, kept only in the
    /// server's config, adds them to that ward's permitted list as a ward's own password would. A ward
    /// with its own password still takes that; a switched-off ward keeps Protective Wards' own
    /// add-yourself. After a wrong guess the player waits <see cref="WaitSeconds"/> before the server's
    /// password is checked for them again.
    /// </summary>
    internal static class WardPassword
    {
        /// <summary>Protective Wards' own limit for a typed password.</summary>
        public const int MaxLength = 64;

        public const double WaitSeconds = 2;

        public enum Answer
        {
            /// <summary>Protective Wards answers: the ward's own password, already permitted, or no server password.</summary>
            ProtectiveWards,
            LetIn,
            Wrong,
        }

        /// <summary>True when <paramref name="given"/> is the set password, compared in time that does not depend on where they differ.</summary>
        public static bool Matches(string set, string given)
        {
            if (string.IsNullOrEmpty(set) || string.IsNullOrEmpty(given) || given.Length > MaxLength || set.Length > MaxLength) return false;
            byte[] a = Encoding.UTF8.GetBytes(set);
            byte[] b = Encoding.UTF8.GetBytes(given);
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                diff |= (i < a.Length ? a[i] : 0) ^ (i < b.Length ? b[i] : 0);
            }
            return diff == 0;
        }

        /// <summary>Whether the player is asked for a password at the ward.</summary>
        public static bool Offer(bool serverHasOne, bool playerWard, bool enabled, bool hasAccess, long playerId, bool wardOffers)
        {
            if (wardOffers) return true;
            return serverHasOne && playerWard && enabled && !hasAccess && playerId != 0;
        }

        /// <summary>
        /// Whether a ward keeps the player from opening a door, by Protective Wards' own rule: an
        /// active player ward whose area holds the door, without access to it or a ward connected to
        /// it, unless the player built the door. <paramref name="wardsLockDoors"/>: its "Doors" setting
        /// is on or the door asks the game's ward check itself.
        /// </summary>
        public static bool LocksDoor(bool wardsLockDoors, bool active, bool inside, bool hasAccess, bool madeDoor) =>
            wardsLockDoors && active && inside && !hasAccess && !madeDoor;

        /// <summary>
        /// The ward the player is asked about at a door: the first that locks it, as Protective Wards
        /// stops at the first, or null when none locks it or that one takes no password.
        /// </summary>
        public static T DoorWard<T>(IEnumerable<T> wards, Func<T, bool> locks, Func<T, bool> asks) where T : class
        {
            var first = wards.FirstOrDefault(locks);
            return first != null && asks(first) ? first : null;
        }

        /// <summary>Whether a use of a door opens the password prompt: alt-use, not held. Plain use stays the door's.</summary>
        public static bool DoorUseAsks(bool hold, bool alt) => alt && !hold;

        /// <summary>The hover line at a door the player is asked about, in Protective Wards' own words.</summary>
        public const string DoorHoverLine = "\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] $pw_ward_password_enter";

        /// <summary>
        /// The self-test's verdict on its locked door: the hover line and the prompt for the door's
        /// ward come exactly when the server has a password (<paramref name="opened"/> null: which
        /// prompt opened cannot be seen).
        /// </summary>
        public static WardVerdict DoorPromptCheck(bool serverHasOne, bool hoverLine, bool? opened)
        {
            if (!serverHasOne)
            {
                return hoverLine || opened == true
                    ? new WardVerdict(SelfTestReport.Result.Fail, "the door asks for a password, though the server has none")
                    : new WardVerdict(SelfTestReport.Result.Pass, "no password line, as the server has no password");
            }
            if (!hoverLine) return new WardVerdict(SelfTestReport.Result.Fail, "the door's hover has no password line");
            if (opened == null) return new WardVerdict(SelfTestReport.Result.Info, "the hover line is there; which prompt opened cannot be seen in this Protective Wards");
            return opened.Value
                ? new WardVerdict(SelfTestReport.Result.Pass, "the hover line is there and alt-use opened the password prompt for the door's ward")
                : new WardVerdict(SelfTestReport.Result.Fail, "alt-use at the door opened no password prompt for its ward");
        }

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool serverHasOne, bool found, string version, string problem)
        {
            if (!found) return "not needed: Protective Wards is not installed";
            if (!serverHasOne) return "off: the server has no ward password";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Protective Wards " + version + " · players without access are asked for the server's password";
        }

        /// <summary>What the door part does on this game, in words: as the ward part, unless only the door part stepped aside.</summary>
        public static string DoorStateLine(bool serverHasOne, bool found, string version, string problem, string doorProblem)
        {
            string ward = StateLine(serverHasOne, found, version, problem);
            if (!ward.StartsWith("working", StringComparison.Ordinal)) return ward;
            return doorProblem != null
                ? "stepped aside: " + doorProblem
                : "working · Protective Wards " + version + " · alt-use at a door a ward locks asks for that ward's password";
        }

        /// <summary>The self-test's three guesses at its own stranger's ward, in order.</summary>
        public enum Step
        {
            /// <summary>A wrong password: refused.</summary>
            Wrong,

            /// <summary>The right password at once after the wrong one: refused, the player still waiting.</summary>
            Early,

            /// <summary>The right password after the wait: the player is let in.</summary>
            Right,
        }

        /// <summary>
        /// What the server types for the self-test's step, or null with the reason. Only a ward the
        /// self-test placed (and the server recorded) takes a guess, so the right password never puts
        /// the tester on a real ward's list.
        /// </summary>
        public static string ServerGuess(Step step, bool testWard, string password, out string why)
        {
            why = null;
            if (!testWard) why = "only a ward the self-test placed takes a self-test guess";
            else if (string.IsNullOrEmpty(password)) why = "no password set on the server: nobody is asked";
            else if (password.Length > MaxLength) why = "the server's password is longer than " + MaxLength + " characters: nobody can type it";
            if (why != null) return null;
            if (step != Step.Wrong) return password;
            const string wrong = "vigil self-test";
            return password == wrong ? wrong + "!" : wrong;
        }

        /// <summary>The self-test's verdict on one guess, from what the server's check decided (null: it never decided).</summary>
        public static WardVerdict StepCheck(Step step, Answer? judged, bool admin)
        {
            if (admin) return new WardVerdict(SelfTestReport.Result.Info, "the server counts you as a ward admin (god mode opens every ward), so no guess was tried");
            if (judged == null) return new WardVerdict(SelfTestReport.Result.Fail, "the guess did not reach the server's check");
            if (judged == Answer.ProtectiveWards) return new WardVerdict(SelfTestReport.Result.Fail, "the guess went past the server's check to Protective Wards");
            switch (step)
            {
                case Step.Wrong:
                    return judged == Answer.Wrong
                        ? new WardVerdict(SelfTestReport.Result.Pass, "a wrong password was refused")
                        : new WardVerdict(SelfTestReport.Result.Fail, "a wrong password let you in");
                case Step.Early:
                    return judged == Answer.Wrong
                        ? new WardVerdict(SelfTestReport.Result.Pass, "the right password right after a wrong one was refused: the " + WaitSeconds + " s wait holds")
                        : new WardVerdict(SelfTestReport.Result.Fail, "the right password right after a wrong one let you in without the wait");
                default:
                    return judged == Answer.LetIn
                        ? new WardVerdict(SelfTestReport.Result.Pass, "the right password put you on the ward's list")
                        : new WardVerdict(SelfTestReport.Result.Fail, "the right password was refused");
            }
        }

        /// <summary>The server's check of a typed password, with each player's wait after a wrong guess.</summary>
        internal sealed class Gate
        {
            private readonly Dictionary<long, double> _wrongAt = new Dictionary<long, double>();

            public Answer Judge(string set, string given, long playerId, bool hasAccess, bool wardHasOwn, double now)
            {
                if (string.IsNullOrEmpty(set) || hasAccess) return Answer.ProtectiveWards;
                bool waiting = _wrongAt.TryGetValue(playerId, out double at) && now - at < WaitSeconds;
                if (!waiting)
                {
                    if (Matches(set, given))
                    {
                        _wrongAt.Remove(playerId);
                        return Answer.LetIn;
                    }
                    _wrongAt[playerId] = now;
                }
                return wardHasOwn ? Answer.ProtectiveWards : Answer.Wrong;
            }
        }
    }

    /// <summary>
    /// Where the ward self-test places its own wards: a spot on the ground the player's game has
    /// loaded (the player's zone and the zones around it), whose area keeps clear of every real
    /// ward's area by a margin, so the test ward joins no real chain and gives the tester no
    /// connected access. Distances are across the ground only, which is what Protective Wards'
    /// cylinder areas use and never less than its sphere distance, so a clear spot is clear in
    /// either shape. The server picks the spot, as it knows every ward of the world.
    /// </summary>
    internal static class WardTestSite
    {
        private static readonly char[] ItemSeparators = { '|' };

        internal readonly struct Spot : IEquatable<Spot>
        {
            public Spot(float x, float z)
            {
                X = x;
                Z = z;
            }

            public float X { get; }

            public float Z { get; }

            public bool Equals(Spot other) => X.Equals(other.X) && Z.Equals(other.Z);

            public override bool Equals(object obj) => obj is Spot other && Equals(other);

            public override int GetHashCode() => (X.GetHashCode() * 397) ^ Z.GetHashCode();

            public static bool operator ==(Spot a, Spot b) => a.Equals(b);

            public static bool operator !=(Spot a, Spot b) => !a.Equals(b);
        }

        /// <summary>A real ward's area across the ground.</summary>
        internal readonly struct Circle
        {
            public Circle(float x, float z, float radius)
            {
                X = x;
                Z = z;
                Radius = radius;
            }

            public float X { get; }

            public float Z { get; }

            public float Radius { get; }
        }

        /// <summary>An object the self-test placed, by its ZDO id's two parts.</summary>
        internal readonly struct Id : IEquatable<Id>
        {
            public Id(long user, uint id)
            {
                User = user;
                Number = id;
            }

            public long User { get; }

            public uint Number { get; }

            public bool Equals(Id other) => User == other.User && Number == other.Number;

            public override bool Equals(object obj) => obj is Id other && Equals(other);

            public override int GetHashCode() => (User.GetHashCode() * 397) ^ (int)Number;

            public static bool operator ==(Id a, Id b) => a.Equals(b);

            public static bool operator !=(Id a, Id b) => !a.Equals(b);
        }

        /// <summary>
        /// Grid points of the player's loaded zones, <paramref name="inset"/> inside their edge and at
        /// least <paramref name="minDistance"/> from the player, nearest first.
        /// </summary>
        public static List<Spot> Candidates(float px, float pz, float zoneSize, int activeArea, float step, float minDistance, float inset)
        {
            int zx = (int)Math.Floor((px + zoneSize / 2) / zoneSize);
            int zz = (int)Math.Floor((pz + zoneSize / 2) / zoneSize);
            float half = (activeArea + 0.5f) * zoneSize - inset;
            int reach = (int)Math.Floor(half / step);
            var spots = new List<Spot>();
            for (int i = -reach; i <= reach; i++)
            {
                for (int j = -reach; j <= reach; j++)
                {
                    float x = zx * zoneSize + i * step, z = zz * zoneSize + j * step;
                    if (Distance(x, z, px, pz) >= minDistance) spots.Add(new Spot(x, z));
                }
            }
            return spots.OrderBy(s => Distance(s.X, s.Z, px, pz)).ToList();
        }

        /// <summary>
        /// The radius the self-test keeps on its own wards. Protective Wards gives every ward its full
        /// radius (32 m), which near a base leaves no clear spot; the test's ward only has to cover its
        /// wall, <see cref="WallOffset"/> away.
        /// </summary>
        public const float TestRadius = 5f;

        /// <summary>How far the repair test's wall stands from its ward.</summary>
        public const float WallOffset = 3f;

        public const string WardPrefab = "guard_stone";

        public const string WallPrefab = "woodwall";

        /// <summary>The password test's door, in its stranger's ward; the stranger's, as a door opens for its builder.</summary>
        public const string DoorPrefab = "wood_door";

        /// <summary>The made-up creator of the password test's ward: no player has this id.</summary>
        public const long Stranger = 1;

        /// <summary>
        /// Whether the server records an object a player's test says it placed: a ward made by the
        /// asker or the made-up stranger, a wall made by the asker, or a door made by the stranger,
        /// owned by the asker's game. A real ward or piece of anyone else is never recorded, so the
        /// server never removes it or types the password at it.
        /// </summary>
        public static bool Accepts(string prefab, long creator, long asker, bool ownedByAsker)
        {
            if (!ownedByAsker || asker == 0) return false;
            if (prefab == WardPrefab) return creator == asker || creator == Stranger;
            if (prefab == DoorPrefab) return creator == Stranger;
            return prefab == WallPrefab && creator == asker;
        }

        /// <summary>Whether a recorded object may be removed: it still is what the test placed.</summary>
        public static bool MayRemove(string prefab, long creator, long recordedFor) =>
            (prefab == WardPrefab && (creator == recordedFor || creator == Stranger)) || (prefab == WallPrefab && creator == recordedFor)
            || (prefab == DoorPrefab && creator == Stranger);

        /// <summary>Whether the server types a guess at the ward: recorded for the asker and made by the made-up stranger.</summary>
        public static bool TakesGuess(bool recorded, long creator) => recorded && creator == Stranger;

        /// <summary>The most room any spot has to the nearest ward's area (infinite without wards).</summary>
        public static float BestClearance(IEnumerable<Spot> candidates, IEnumerable<Circle> wards)
        {
            var all = wards.ToList();
            if (all.Count == 0) return float.PositiveInfinity;
            float best = float.NegativeInfinity;
            foreach (var spot in candidates)
            {
                float room = all.Min(w => (float)Distance(spot.X, spot.Z, w.X, w.Z) - w.Radius);
                if (room > best) best = room;
            }
            return best;
        }

        /// <summary>Why no spot was picked, with how near the best one came.</summary>
        public static string NoSpotLine(float best, float testRadius, float margin, int spots) =>
            "no spot in the zones around you keeps a test ward clear of the switched-on wards: the best is "
            + Math.Max(0f, best).ToString("0", CultureInfo.InvariantCulture) + " m from a ward's area, the test needs "
            + (testRadius + margin).ToString("0", CultureInfo.InvariantCulture) + " m (" + spots + " spots looked at)";

        /// <summary>The first spot whose area of <paramref name="testRadius"/> stays <paramref name="margin"/> clear of every ward's, or null.</summary>
        public static Spot? Pick(IEnumerable<Spot> candidates, IEnumerable<Circle> wards, float testRadius, float margin)
        {
            var all = wards.ToList();
            foreach (var spot in candidates)
            {
                if (all.All(w => Distance(spot.X, spot.Z, w.X, w.Z) > testRadius + w.Radius + margin)) return spot;
            }
            return null;
        }

        /// <summary>
        /// Spots as "x,z|x,z": the link's encoding (<see cref="KeyValues"/>) turns ';' and '=' inside a
        /// value into ',' and ':', so a list sent over it must not use those between items.
        /// </summary>
        public static string FormatSpots(IEnumerable<Spot> spots) =>
            string.Join("|", spots.Select(s => s.X.ToString("R", CultureInfo.InvariantCulture) + "," + s.Z.ToString("R", CultureInfo.InvariantCulture)));

        /// <summary>The spots in <see cref="FormatSpots"/>'s form; anything else is left out.</summary>
        public static List<Spot> ParseSpots(string text)
        {
            var spots = new List<Spot>();
            foreach (string pair in (text ?? "").Split(ItemSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] xz = pair.Split(',');
                if (xz.Length == 2 && float.TryParse(xz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(xz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    spots.Add(new Spot(x, z));
                }
            }
            return spots;
        }

        public static string FormatIds(IEnumerable<Id> ids) =>
            string.Join("|", ids.Select(i => i.User.ToString(CultureInfo.InvariantCulture) + ":" + i.Number.ToString(CultureInfo.InvariantCulture)));

        /// <summary>The ids in <see cref="FormatIds"/>'s form; anything else is left out.</summary>
        public static List<Id> ParseIds(string text)
        {
            var ids = new List<Id>();
            foreach (string part in (text ?? "").Split(ItemSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] two = part.Split(':');
                if (two.Length == 2 && long.TryParse(two[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long user)
                    && uint.TryParse(two[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number))
                {
                    ids.Add(new Id(user, number));
                }
            }
            return ids;
        }

        private static double Distance(float ax, float az, float bx, float bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
    }

    /// <summary>A self-test line's result and words.</summary>
    internal readonly struct WardVerdict
    {
        public WardVerdict(SelfTestReport.Result result, string text)
        {
            Result = result;
            Text = text;
        }

        public SelfTestReport.Result Result { get; }

        public string Text { get; }
    }

    /// <summary>
    /// Protective Wards' members the ward patches use, found by name from its types (2.1.0's shape).
    /// Null when a member is missing or changed shape: the patch steps aside and says so.
    /// </summary>
    internal static class WardShape
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly string[] AnswerTakes = { "Int64", "ZDOID" };

        internal sealed class RepairShape
        {
            /// <summary><c>ProtectiveWards.PassiveRepairEffect(PrivateArea, Player)</c>, the repair loop alt-use starts.</summary>
            public MethodInfo StartRepair;

            /// <summary><c>CanBeRepaired(Piece, PrivateArea)</c>: damaged, placed by a player or allowed, station near when asked.</summary>
            public MethodInfo CanBeRepaired;

            /// <summary><c>AreWardsOverlapping(PrivateArea, PrivateArea)</c>, by its configured distance.</summary>
            public MethodInfo Overlapping;

            /// <summary><c>IsActivePlayerWard(PrivateArea)</c>: switched on, a player's ward.</summary>
            public MethodInfo Active;

            /// <summary><c>IsPlayerWardPrefab(PrivateArea)</c>.</summary>
            public MethodInfo PlayerWard;

            /// <summary><c>wardIsRepairing</c>, the hover's count of pieces left by ward.</summary>
            public FieldInfo Repairing;
        }

        internal sealed class PasswordShape
        {
            /// <summary><c>WardPasswordProtection.ShouldOfferPasswordEntry(PrivateArea, Player)</c>, asked by the hover and by use.</summary>
            public MethodInfo Offers;

            /// <summary><c>RPC_SubmitWardPasswordServer(long, ZPackage)</c>, the server's handler of a typed password.</summary>
            public MethodInfo Submitted;

            /// <summary><c>SendPasswordEntryResult(long, ZDOID, PasswordEntryResult)</c>.</summary>
            public MethodInfo Answer;

            public Type Results;

            public int LetIn;

            public int Wrong;

            /// <summary><c>IsPasswordProtectionActive(ZDO)</c>: the ward has a password of its own.</summary>
            public MethodInfo OwnPassword;

            /// <summary><c>ProtectiveWards.TryGetRoutedPlayer(long, long, out RoutedPlayerContext)</c>.</summary>
            public MethodInfo RoutedPlayer;

            /// <summary><c>ProtectiveWards.HasAccessToWardOrConnectedWard(PrivateArea, Player)</c>.</summary>
            public MethodInfo Access;

            /// <summary><c>WardZdoUtils.TryGetWard(ZDOID, out ZDO)</c>.</summary>
            public MethodInfo WardById;

            /// <summary><c>WardZdoUtils.HasDirectAccessToWardZdo(ZDO, long)</c>.</summary>
            public MethodInfo DirectAccess;

            /// <summary><c>WardZdoUtils.AddPermitted(ZDO, long, string)</c>.</summary>
            public MethodInfo Permit;

            /// <summary><c>WardZdoUtils.GetAllWards()</c>, for the self-test's spot; null: the self-test steps aside.</summary>
            public MethodInfo AllWards;

            /// <summary><c>WardZdoUtils.GetConfiguredWardRange(ZDO)</c>, a ward's radius as set.</summary>
            public MethodInfo Range;

            /// <summary><c>WardZdoUtils.GetWardDefaultRadius()</c>, the radius a ward without its own has.</summary>
            public MethodInfo DefaultRadius;

            /// <summary><c>ProtectiveWards.HasWardAdminAccess(long)</c>: the player passes every ward (admins, by its setting).</summary>
            public MethodInfo Admin;
        }

        internal sealed class DoorShape
        {
            /// <summary><c>WardPasswordProtection.TryHandleInteraction(PrivateArea, Player)</c>: opens the prompt when it is offered.</summary>
            public MethodInfo Prompt;

            /// <summary><c>IsActivePlayerWard(PrivateArea)</c>.</summary>
            public MethodInfo Active;

            /// <summary><c>IsPointInsideWardArea(PrivateArea, Vector3, float)</c>.</summary>
            public MethodInfo Inside;

            /// <summary><c>IsObjectOwnedByPlayerWithWardAccess(Component, Player)</c>: the player built it.</summary>
            public MethodInfo MadeBy;

            /// <summary><c>wardAccessProtectDoors</c>, its "Doors" setting (a ConfigEntry of bool).</summary>
            public FieldInfo LocksDoors;

            /// <summary><c>s_promptWardID</c>, the ward the open prompt is for; null: the self-test cannot see it.</summary>
            public FieldInfo PromptWard;

            /// <summary><c>ClosePrompt()</c>; null: the self-test leaves the prompt to the player.</summary>
            public MethodInfo ClosePrompt;
        }

        public static DoorShape FindDoor(Func<string, Type> type)
        {
            var pw = type("ProtectiveWards.ProtectiveWards");
            var protection = type("ProtectiveWards.WardPasswordProtection");
            if (pw == null || protection == null) return null;
            var shape = new DoorShape
            {
                Prompt = Method(protection, "TryHandleInteraction", typeof(bool), "PrivateArea", "Player"),
                Active = Method(pw, "IsActivePlayerWard", typeof(bool), "PrivateArea"),
                Inside = Method(pw, "IsPointInsideWardArea", typeof(bool), "PrivateArea", "Vector3", "Single"),
                MadeBy = Method(pw, "IsObjectOwnedByPlayerWithWardAccess", typeof(bool), "Component", "Player"),
                LocksDoors = pw.GetField("wardAccessProtectDoors", Static),
                PromptWard = protection.GetField("s_promptWardID", Static),
                ClosePrompt = Method(protection, "ClosePrompt", typeof(void)),
            };
            var setting = shape.LocksDoors?.FieldType;
            bool boolSetting = setting != null && setting.IsGenericType && setting.Name == "ConfigEntry`1"
                && setting.GetGenericArguments()[0] == typeof(bool) && setting.GetProperty("Value") != null;
            if (shape.PromptWard != null && shape.PromptWard.FieldType.Name != "ZDOID") shape.PromptWard = null;
            return shape.Prompt != null && shape.Active != null && shape.Inside != null && shape.MadeBy != null && boolSetting ? shape : null;
        }

        public static RepairShape FindRepair(Func<string, Type> type)
        {
            var pw = type("ProtectiveWards.ProtectiveWards");
            if (pw == null) return null;
            var shape = new RepairShape
            {
                StartRepair = Method(pw, "PassiveRepairEffect", typeof(IEnumerator), "PrivateArea", "Player"),
                CanBeRepaired = Method(pw, "CanBeRepaired", typeof(bool), "Piece", "PrivateArea"),
                Overlapping = Method(pw, "AreWardsOverlapping", typeof(bool), "PrivateArea", "PrivateArea"),
                Active = Method(pw, "IsActivePlayerWard", typeof(bool), "PrivateArea"),
                PlayerWard = Method(pw, "IsPlayerWardPrefab", typeof(bool), "PrivateArea"),
                Repairing = pw.GetField("wardIsRepairing", Static),
            };
            bool dictionary = shape.Repairing != null && typeof(IDictionary).IsAssignableFrom(shape.Repairing.FieldType)
                && shape.Repairing.FieldType.IsGenericType && shape.Repairing.FieldType.GetGenericArguments()[1] == typeof(int);
            return shape.StartRepair != null && shape.CanBeRepaired != null && shape.Overlapping != null && shape.Active != null
                && shape.PlayerWard != null && dictionary ? shape : null;
        }

        public static PasswordShape FindPassword(Func<string, Type> type)
        {
            var pw = type("ProtectiveWards.ProtectiveWards");
            var protection = type("ProtectiveWards.WardPasswordProtection");
            var zdos = type("ProtectiveWards.WardZdoUtils");
            if (pw == null || protection == null || zdos == null) return null;
            var shape = new PasswordShape
            {
                Offers = Method(protection, "ShouldOfferPasswordEntry", typeof(bool), "PrivateArea", "Player"),
                Submitted = Method(protection, "RPC_SubmitWardPasswordServer", typeof(void), "Int64", "ZPackage"),
                OwnPassword = Method(protection, "IsPasswordProtectionActive", typeof(bool), "ZDO"),
                RoutedPlayer = Method(pw, "TryGetRoutedPlayer", typeof(bool), "Int64", "Int64", "RoutedPlayerContext&"),
                Access = Method(pw, "HasAccessToWardOrConnectedWard", typeof(bool), "PrivateArea", "Player"),
                WardById = Method(zdos, "TryGetWard", typeof(bool), "ZDOID", "ZDO&"),
                DirectAccess = Method(zdos, "HasDirectAccessToWardZdo", typeof(bool), "ZDO", "Int64"),
                Permit = Method(zdos, "AddPermitted", typeof(bool), "ZDO", "Int64", "String"),
                AllWards = zdos.GetMethods(Static).FirstOrDefault(m => m.Name == "GetAllWards" && m.GetParameters().Length == 0 && typeof(IEnumerable).IsAssignableFrom(m.ReturnType)),
                Range = Method(zdos, "GetConfiguredWardRange", typeof(float), "ZDO"),
                DefaultRadius = Method(zdos, "GetWardDefaultRadius", typeof(float)),
                Admin = Method(pw, "HasWardAdminAccess", typeof(bool), "Int64"),
            };
            shape.Answer = protection.GetMethods(Static).FirstOrDefault(m => m.Name == "SendPasswordEntryResult" && m.ReturnType == typeof(void)
                && Names(m.GetParameters()).Take(2).SequenceEqual(AnswerTakes) && m.GetParameters().Length == 3 && m.GetParameters()[2].ParameterType.IsEnum);
            shape.Results = shape.Answer?.GetParameters()[2].ParameterType;
            if (shape.Results == null || !Enum.IsDefined(shape.Results, "Success") || !Enum.IsDefined(shape.Results, "IncorrectPassword")) return null;
            shape.LetIn = Convert.ToInt32(Enum.Parse(shape.Results, "Success"), System.Globalization.CultureInfo.InvariantCulture);
            shape.Wrong = Convert.ToInt32(Enum.Parse(shape.Results, "IncorrectPassword"), System.Globalization.CultureInfo.InvariantCulture);
            return shape.Offers != null && shape.Submitted != null && shape.OwnPassword != null && shape.RoutedPlayer != null && shape.Access != null
                && shape.WardById != null && shape.DirectAccess != null && shape.Permit != null ? shape : null;
        }

        private static MethodInfo Method(Type owner, string name, Type returns, params string[] parameters) =>
            owner.GetMethods(Static).FirstOrDefault(m => m.Name == name && m.ReturnType == returns && Names(m.GetParameters()).SequenceEqual(parameters));

        private static IEnumerable<string> Names(ParameterInfo[] parameters) => parameters.Select(p => p.ParameterType.Name);
    }
}
