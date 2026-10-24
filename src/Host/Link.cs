using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// Vigil's conversation between a player's game and the dedicated server, over the game's routed
/// messages. Once in a world on a server, the player's Vigil greets the server's and learns its
/// version, what it can do and whether this player is one of its admins (<see cref="Server"/>).
/// An admin's game can then ask the server's Vigil for things (<see cref="Ask"/>); the server
/// checks the sender against its admin list before every one and answers with named values.
/// The greeting is open to every player: it names nothing but the server's Vigil.
/// </summary>
internal sealed class Link
{
    internal const string RpcHello = "Vigil_Hello", RpcHelloReply = "Vigil_HelloReply", RpcAdmin = "Vigil_Admin", RpcAdminReply = "Vigil_AdminReply";

    private readonly bool _server;
    private readonly Func<string, Dictionary<string, string>, ZNetPeer, Dictionary<string, object>> _dispatch;
    private readonly Action<string> _log;
    private readonly PendingRequests _pending = new(10);

    /// <summary>On the server: capabilities that depend on its settings (",share" while it takes shared reports), added to the greeting.</summary>
    public static Func<string> MoreCaps;
    private ZRoutedRpc _registeredOn;

    /// <param name="server">True on a dedicated server, which answers; false on a player's game, which asks.</param>
    /// <param name="dispatch">On a server: the answer to an admin's command, null when no area knows it.</param>
    /// <param name="log">Writes a line to the game's log.</param>
    public Link(bool server, Func<string, Dictionary<string, string>, ZNetPeer, Dictionary<string, object>> dispatch, Action<string> log)
    {
        _server = server;
        _dispatch = dispatch;
        _log = log;
    }

    /// <summary>What this game knows about the server's Vigil.</summary>
    public Greeting Server { get; } = new();

    private static double Now => Time.realtimeSinceStartupAsDouble;

    /// <summary>Every frame: registers the messages on each new connection, greets the server once in a world, and ends unanswered questions.</summary>
    public void Update()
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc != _registeredOn)
        {
            if (_registeredOn != null)
            {
                _pending.Drop("the connection to the server ended");
                Server.Reset();
            }

            _registeredOn = rpc;
            if (rpc != null)
            {
                rpc.Register<string>(RpcHello, (sender, line) => Safe(() => OnHello(sender)));
                rpc.Register<string>(RpcHelloReply, (sender, line) => Safe(() => OnHelloReply(line)));
                rpc.Register<string>(RpcAdmin, (sender, line) => Safe(() => OnAdmin(sender, line)));
                rpc.Register<string>(RpcAdminReply, (sender, line) => Safe(() => OnAdminReply(line)));
            }
        }

        if (_server || rpc == null || ZNet.instance == null)
        {
            return;
        }

        var serverPeer = ZNet.instance.IsServer() ? null : ZNet.instance.GetServerPeer();
        if (serverPeer == null || !serverPeer.IsReady())
        {
            if (Server.State != GreetingState.NotAsked)
            {
                Server.Reset();
                _pending.Drop("the connection to the server ended");
            }

            return;
        }

        if (Server.State == GreetingState.NotAsked && Player.m_localPlayer != null)
        {
            Server.Asked(Now);
            rpc.InvokeRoutedRPC(RpcHello, KeyValues.Encode(new Dictionary<string, object> { { "v", VigilVersion.Value }, { "caps", Greeting.Own } }));
        }

        Server.Tick(Now);
        _pending.Expire(Now);
    }

    /// <summary>
    /// Asks the server's Vigil to do <paramref name="command"/>; the answer, or the reason there is
    /// none, comes to <paramref name="answer"/> on a later frame. The caller checks first that the
    /// server can (<see cref="Greeting.Lacking"/>).
    /// </summary>
    public void Ask(string command, Dictionary<string, object> args, Action<bool, Dictionary<string, string>> answer)
    {
        if (_registeredOn == null || Server.State != GreetingState.Answered)
        {
            answer?.Invoke(false, new Dictionary<string, string> { { "text", Server.Describe() } });
            return;
        }

        int id = _pending.Add(command, Now, answer);
        var values = new Dictionary<string, object>(args ?? new Dictionary<string, object>()) { ["id"] = id, ["cmd"] = command };
        _registeredOn.InvokeRoutedRPC(RpcAdmin, KeyValues.Encode(values));
    }

    private void OnHello(long sender)
    {
        if (!_server)
        {
            return;
        }

        var peer = ZNet.instance?.GetPeer(sender);
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcHelloReply, KeyValues.Encode(new Dictionary<string, object>
        {
            { "v", VigilVersion.Value }, { "caps", Greeting.Own + (MoreCaps?.Invoke() ?? "") }, { "admin", IsAdmin(peer) ? 1 : 0 },
        }));
    }

    private void OnHelloReply(string line)
    {
        if (_server)
        {
            return;
        }

        var d = KeyValues.Decode(line);
        d.TryGetValue("v", out string version);
        d.TryGetValue("caps", out string caps);
        d.TryGetValue("admin", out string admin);
        Server.Answer(version, (caps ?? "").Replace(':', ','), admin == "1", Now);
    }

    private void OnAdmin(long sender, string line)
    {
        if (!_server)
        {
            return;
        }

        var d = KeyValues.Decode(line);
        d.TryGetValue("id", out string id);
        d.TryGetValue("cmd", out string command);
        var peer = ZNet.instance?.GetPeer(sender);
        Dictionary<string, object> reply;
        if (!IsAdmin(peer) && !Greeting.ForEveryone(command))
        {
            reply = new Dictionary<string, object> { { "ok", 0 }, { "text", "refused: only the server's admins (adminlist.txt) can ask its Vigil for that" } };
        }
        else
        {
            reply = _dispatch(command ?? "", d, peer)
                ?? new Dictionary<string, object> { { "ok", 0 }, { "text", "the server's Vigil " + VigilVersion.Value + " does not know \"" + command + "\"" } };
            if (command != "status" && command != "link" && command != "report" && command != "share" && command != "parity" && command != Greeting.SpawnLog && command != SpawnView.Command && command != Greeting.Leftovers && command != Greeting.Alerts && command != Greeting.Steady && command != Greeting.Away)
            {
                _log("Vigil: " + (peer?.m_playerName ?? "an admin") + " asked for " + command);
            }
        }

        reply["id"] = id;
        reply["v"] = VigilVersion.Value;
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcAdminReply, KeyValues.Encode(reply));
    }

    private void OnAdminReply(string line)
    {
        if (_server)
        {
            return;
        }

        var d = KeyValues.Decode(line);
        if (d.TryGetValue("id", out string id) && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            _pending.Complete(n, d, Now);
        }
    }

    private static bool IsAdmin(ZNetPeer peer)
    {
        try
        {
            return peer?.m_socket != null && ZNet.instance.IsAdmin(peer.m_socket.GetHostName());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Safe(Action a)
    {
        try
        {
            a();
        }
        catch (Exception e)
        {
            _log("Vigil: a message from the other side failed: " + e.GetType().Name + " " + e.Message);
        }
    }
}
