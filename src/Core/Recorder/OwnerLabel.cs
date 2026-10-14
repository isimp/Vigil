using System.Collections.Generic;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>Whose game runs an object: another player's, this one, the server or none.</summary>
    internal enum OwnerRole
    {
        Player,
        You,
        Server,
        Nobody,
    }

    /// <summary>
    /// The words of the label under the crosshair: what the player looks at, then whose game runs
    /// it, that connection's ping when known and, for another player, how far they are from it.
    /// The parts are kept apart so the label can colour the owner and the ping.
    /// </summary>
    internal sealed class OwnerLabel
    {
        public string Thing { get; private set; }

        public string Owner { get; private set; }

        /// <summary>Null when the ping is not known or does not apply (the server, nobody).</summary>
        public string Ping { get; private set; }

        /// <summary>Null unless another player runs it and where they are is known.</summary>
        public string Distance { get; private set; }

        /// <summary>The second line: owner, ping and distance joined.</summary>
        public string Line
        {
            get
            {
                var parts = new List<string> { Owner };
                if (Ping != null) parts.Add(Ping);
                if (Distance != null) parts.Add(Distance);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>The ping's colour by the HUD line's levels: amber from half the report limit, red at it; fine when either is not known.</summary>
        public HudLine.Level PingLevel { get; private set; }

        public static OwnerLabel Build(string thing, string owner, OwnerRole role, double pingMs, double distanceM, double pingLimitMs = double.NaN)
        {
            bool pinged = (role == OwnerRole.Player || role == OwnerRole.You) && !double.IsNaN(pingMs);
            return new OwnerLabel
            {
                Thing = string.IsNullOrEmpty(thing) ? "something" : thing,
                Owner = role switch
                {
                    OwnerRole.You => "run by your game",
                    OwnerRole.Server => "run by the server",
                    OwnerRole.Nobody => "no game runs it",
                    _ => "run by " + owner,
                },
                Ping = pinged ? "ping " + pingMs.ToString("0", CultureInfo.InvariantCulture) + " ms" : null,
                Distance = role == OwnerRole.Player && !double.IsNaN(distanceM) ? distanceM.ToString("0", CultureInfo.InvariantCulture) + " m from it" : null,
                PingLevel = pinged && !double.IsNaN(pingLimitMs) ? HudLine.AtOrAbove(pingMs, pingLimitMs, pingLimitMs / 2) : HudLine.Level.Fine,
            };
        }
    }
}
