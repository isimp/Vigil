using System;
using System.Collections;
using System.Collections.Generic;
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

        /// <summary>What the patch does on this game, in words.</summary>
        public static string StateLine(bool serverHasOne, bool found, string version, string problem)
        {
            if (!found) return "not needed: Protective Wards is not installed";
            if (!serverHasOne) return "off: the server has no ward password";
            if (problem != null) return "stepped aside: " + problem;
            return "working · Protective Wards " + version + " · players without access are asked for the server's password";
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
