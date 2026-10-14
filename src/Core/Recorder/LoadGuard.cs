using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// Stops a load test once the server or the player's game is clearly overwhelmed: the test is
    /// there to find the limit, not to wreck the session. Checked once a second.
    /// </summary>
    public sealed class LoadGuard
    {
        public double MaxServerFrameMs = 200;
        public int ServerFrameSeconds = 3;
        public double MaxStaleSeconds = 20;
        public double MinClientFps = 5;
        public int ClientFpsSeconds = 5;
        public double MaxRttMs = 10000;

        private int _serverSlow, _clientSlow;

        /// <summary>Why the test should stop now, or null. Values not known yet (NaN) never stop it.</summary>
        public string Check(double serverFrameMs, double staleSeconds, double clientFps, double rttMs)
        {
            var ci = CultureInfo.InvariantCulture;
            _serverSlow = serverFrameMs > MaxServerFrameMs ? _serverSlow + 1 : 0;
            _clientSlow = clientFps < MinClientFps ? _clientSlow + 1 : 0;
            if (staleSeconds > MaxStaleSeconds)
                return string.Format(ci, "world updates {0:0} s old (limit {1:0} s)", staleSeconds, MaxStaleSeconds);
            if (rttMs > MaxRttMs)
                return string.Format(ci, "a round trip of {0:0} ms (limit {1:0} ms)", rttMs, MaxRttMs);
            if (_serverSlow >= ServerFrameSeconds)
                return string.Format(ci, "the server took over {0:0} ms per frame for {1} seconds in a row", MaxServerFrameMs, ServerFrameSeconds);
            if (_clientSlow >= ClientFpsSeconds)
                return string.Format(ci, "the player's game ran under {0:0} fps for {1} seconds in a row", MinClientFps, ClientFpsSeconds);
            return null;
        }
    }

    /// <summary>Whether the server takes on a load test.</summary>
    public static class LoadAdmission
    {
        // A dedicated server builds the objects around the world's center itself (its reference
        // point never moves) and removes the ones it cannot build, the test's objects included.
        public const double CenterGuardMeters = 400;

        /// <summary>Why the test is refused, or null when it may run.</summary>
        public static string Refusal(int otherPlayers, bool force, bool running, double metersFromWorldCenter)
        {
            var ci = CultureInfo.InvariantCulture;
            if (running) return "a load test is already running";
            if (metersFromWorldCenter < CenterGuardMeters)
                return string.Format(ci, "too close to the world center ({0:0} m, at least {1:0} m needed): the server removes objects there that it cannot build",
                    metersFromWorldCenter, CenterGuardMeters);
            if (otherPlayers > 0 && !force)
                return otherPlayers + " other player" + (otherPlayers == 1 ? " is" : "s are") + " online and would feel it; add 'force' to run it anyway";
            return null;
        }
    }

    /// <summary>
    /// When each number of a beacon was written. The beacon object carries a number that goes up
    /// ten times a second; the number another machine reports holding tells how old its copy is.
    /// </summary>
    public sealed class SeqClock
    {
        private readonly int[] _seqs;
        private readonly double[] _times;

        public SeqClock(int capacity)
        {
            _seqs = new int[capacity];
            _times = new double[capacity];
        }

        public int Latest { get; private set; }

        public int Next(double now)
        {
            Latest++;
            int i = Latest % _seqs.Length;
            _seqs[i] = Latest; _times[i] = now;
            return Latest;
        }

        /// <summary>Seconds since <paramref name="seq"/> was written; for a number too old to remember, the oldest age known.</summary>
        public double Age(int seq, double now)
        {
            if (seq <= 0 || seq > Latest) return double.NaN;
            int i = seq % _seqs.Length;
            if (_seqs[i] == seq) return now - _times[i];
            int oldest = System.Math.Max(1, Latest - _seqs.Length + 1);
            int j = oldest % _seqs.Length;
            return _seqs[j] == oldest ? now - _times[j] : double.NaN;
        }
    }
}
