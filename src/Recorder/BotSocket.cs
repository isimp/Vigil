using System.Collections.Generic;
using Vigil.Core;
using UnityEngine;

namespace Vigil.Recorder
{
    /// <summary>
    /// The connection of a simulated player. Everything the server sends it is taken at a fixed
    /// rate (LinkModel); the game reads the queue that builds up and sends world updates only
    /// while it is short, as with a real connection. The server's connection check is answered:
    /// its ping is the number 0 followed by true, the answer 0 followed by false. Nothing else
    /// ever comes back; the simulated player's own updates go into the server directly.
    /// </summary>
    internal sealed class BotSocket : ISocket
    {
        private readonly LinkModel _link;
        private readonly Queue<ZPackage> _incoming = new Queue<ZPackage>();
        private readonly string _name;
        private bool _connected = true;
        private long _statsSent;

        public BotSocket(string name, double linkKbps)
        {
            _name = name;
            _link = new LinkModel(linkKbps);
        }

        private static double Now => Time.realtimeSinceStartupAsDouble;
        public long TotalBytes => _link.TotalBytes;
        public int Queued => _link.Queued(Now);

        public bool IsConnected() => _connected;

        public void Send(ZPackage pkg)
        {
            if (!_connected || pkg == null) return;
            int size = pkg.Size();
            _link.Send(size, Now);
            // A ping is an int and a bool: five bytes.
            if (size != 5) return;
            try
            {
                var copy = new ZPackage(pkg.GetArray());
                if (copy.ReadInt() != 0 || !copy.ReadBool()) return;
                var pong = new ZPackage();
                pong.Write(0);
                pong.Write(false);
                _incoming.Enqueue(new ZPackage(pong.GetArray()));
            }
            catch (System.Exception) { }
        }

        public ZPackage Recv() => _incoming.Count > 0 ? _incoming.Dequeue() : null;
        public int GetSendQueueSize() => _link.Queued(Now);
        public int GetCurrentSendRate() => (int)(_link.RateKbps * 1024);
        public bool IsHost() => false;
        public void Dispose() { _connected = false; }
        public bool GotNewData() => _incoming.Count > 0;
        public void Close() { _connected = false; }
        public string GetEndPointString() => _name;

        public void GetAndResetStats(out int totalSent, out int totalRecv)
        {
            totalSent = (int)(_link.TotalBytes - _statsSent);
            _statsSent = _link.TotalBytes;
            totalRecv = 0;
        }

        public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
        {
            localQuality = 1; remoteQuality = 1; ping = 0;
            outByteSec = (float)(_link.RateKbps * 1024); inByteSec = 0;
        }

        public ISocket Accept() => null;
        public int GetHostPort() => 0;
        public bool Flush() => true;
        public string GetHostName() => _name;
        public void VersionMatch() { }
    }
}
