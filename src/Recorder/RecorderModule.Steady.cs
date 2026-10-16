using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vigil.Core;

namespace Vigil.Recorder
{
    /// <summary>
    /// "Steady messages, per player" in the panel's Network tab, for the server's admins: the messages
    /// that keep coming at a fixed rate, per sender, as the server's Vigil counts them
    /// (<see cref="SteadyMessages"/>), asked for every 30 s while the card is drawn.
    /// </summary>
    internal sealed partial class RecorderModule
    {
        private const double SteadyEverySeconds = 30;

        private static readonly Ui.Column[] SteadyColumns =
        {
            new Ui.Column("Mod · message", 0, false), new Ui.Column("From", 90, false), new Ui.Column("per s", 50, true),
            new Ui.Column("kB/min", 60, true), new Ui.Column("windows", 70, true),
        };

        private List<SteadyRow> _steadyRows;
        private string _steadyNote = "asking the server ...";
        private double _steadyAskedAt = double.NegativeInfinity;
        private bool _steadyAsking;

        /// <summary>The server's answer: its steady senders.</summary>
        private Dictionary<string, object> SteadyReply()
        {
            var reply = SteadyMessages.EncodeRows(RpcProbe.Steady.Steady());
            reply["ok"] = 1;
            return reply;
        }

        /// <summary>Full self-test: the server answers with its steady senders.</summary>
        private System.Collections.IEnumerator SteadyOnServer(List<SelfTestReport.Check> checks)
        {
            const string what = "the server's steady messages";
            if (!CanAsk(checks, what, Greeting.Steady, adminOnly: true)) yield break;
            var a = Ask(Greeting.Steady, new Dictionary<string, object>());
            while (!a.Done) yield return null;
            if (!a.Ok)
            {
                checks.Add(new SelfTestReport.Check(Here, what, SelfTestReport.Result.Fail, Why(a)));
                yield break;
            }
            var rows = SteadyMessages.DecodeRows(a.Reply);
            checks.Add(new SelfTestReport.Check(Here, what, SelfTestRoutines.Judge(true, a.Seconds),
                rows.Count == 0 ? "none steady in the last 12 minutes" : rows.Count + " steady; the most frequent: " + SteadyMessages.Line(rows[0])));
        }

        private PanelBadge SteadyBadge() => _steadyRows == null ? null : new PanelBadge(_steadyRows.Count.ToString(CultureInfo.InvariantCulture) + " steady", Skin.Tone.Quiet);

        private void DrawSteady()
        {
            string lacking = _host.Link.Server.Lacking(Greeting.Steady, "list the steady messages", adminOnly: true);
            if (lacking != null)
            {
                Ui.Note(char.ToUpperInvariant(lacking[0]) + lacking.Substring(1) + ".");
                return;
            }
            if (!_steadyAsking && Now - _steadyAskedAt >= SteadyEverySeconds)
            {
                _steadyAsking = true;
                _steadyAskedAt = Now;
                _host.Link.Ask(Greeting.Steady, new Dictionary<string, object>(), (ok, reply) =>
                {
                    _steadyAsking = false;
                    if (!ok)
                    {
                        _steadyNote = reply != null && reply.TryGetValue("text", out string t) ? t : "the server did not answer";
                        return;
                    }
                    _steadyRows = SteadyMessages.DecodeRows(reply);
                });
            }
            if (_steadyRows == null)
            {
                Ui.Note(_steadyNote);
                return;
            }
            if (_steadyRows.Count == 0) Ui.Note("None: no message came in most 10 s windows of the last 12 minutes at a similar rate.");
            else Ui.Table(SteadyColumns, _steadyRows.Select(r => new[]
            {
                r.Mod + " · " + r.Message, r.From, r.PerSecond.ToString("0.0", Wc), r.KbPerMinute.ToString("0.0", Wc), r.Seen.ToString(Wc) + " of " + r.Of.ToString(Wc),
            }));
            Ui.Note("Steady: in most 10 s windows of the last 12 minutes, at a similar rate, whatever the players did. A message the server sends to everyone counts once. It cannot tell whether a mod needs to send so often.");
        }
    }
}
