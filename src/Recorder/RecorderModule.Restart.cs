using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// On a dedicated server: the restart warning. Vigil keeps Vigil-state/online.txt with the
    /// number of players on ("players=N;at=unix seconds", rewritten when it changes and every 30 s),
    /// so the server tooling knows whether anyone would be kicked. Before it stops a server with
    /// players on, the tooling leaves Vigil-state/restart.txt (<see cref="RestartNotice"/>); Vigil
    /// reads and removes it, and puts the game's own centre message on every player's screen (the
    /// "ShowMessage" call every game registers, so players need no Vigil) at once and again with 30
    /// and 10 seconds left.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private double _restartCheckAt, _onlineWrittenAt = double.NegativeInfinity, _restartAt = double.NaN, _restartLeft;
        private int _onlineWritten = -1;
        private string _restartReason;

        private string StateFile(string name) => Path.Combine(Path.Combine(_dir, "Vigil-state"), name);

        /// <summary>Every frame on a dedicated server; the files are looked at once a second.</summary>
        private void UpdateRestart()
        {
            if (!_server || ZNet.instance == null) return;
            double now = Now;
            if (now < _restartCheckAt) return;
            _restartCheckAt = now + 1;

            int players = ZNet.instance.GetPeers().Count(p => p != null && !Bots.IsBot(p));
            if (players != _onlineWritten || now - _onlineWrittenAt >= 30)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StateFile("online.txt")));
                    File.WriteAllText(StateFile("online.txt"), KeyValues.Encode(new Dictionary<string, object>
                    {
                        { "players", players }, { "at", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds },
                    }));
                    _onlineWritten = players;
                    _onlineWrittenAt = now;
                }
                catch (Exception) { }
            }

            string file = StateFile(RestartNotice.FileName);
            if (File.Exists(file))
            {
                RestartNotice notice = null;
                try { notice = RestartNotice.Parse(File.ReadAllText(file)); File.Delete(file); } catch (Exception) { }
                if (notice != null)
                {
                    _restartAt = now + notice.Seconds;
                    _restartReason = notice.Reason;
                    _restartLeft = notice.Seconds;
                    Announce(notice.Seconds);
                }
            }

            if (double.IsNaN(_restartAt)) return;
            double left = _restartAt - now;
            if (RestartNotice.Due(_restartLeft, left)) Announce(left);
            _restartLeft = left;
            if (left <= 0) _restartAt = double.NaN;
        }

        private void Announce(double left)
        {
            string text = RestartNotice.Message(left, _restartReason);
            try
            {
                foreach (var p in ZNet.instance.GetPeers().Where(p => p != null && !Bots.IsBot(p)))
                    ZRoutedRpc.instance?.InvokeRoutedRPC(p.m_uid, "ShowMessage", (int)MessageHud.MessageType.Center, text);
            }
            catch (Exception e) { _host.Log.LogWarning("Vigil: the restart notice could not be sent: " + e.Message); }
            _host.Log.LogInfo("Vigil: told the players: " + text);
            EventLog.Write("restart", "notice", text);
        }
    }
}
