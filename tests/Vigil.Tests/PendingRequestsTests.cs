using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// An admin asks the server's Vigil something (its status, its self-test, a switch of its
    /// recording) and the answer comes back later, over the network. Each answer reaches the part of
    /// the panel or console that asked, once; an answer that never comes ends in a message after a
    /// while instead of leaving the question open for good.
    /// </summary>
    public sealed class PendingRequestsTests
    {
        [Fact]
        public void AnAnswerReachesWhoAskedForIt()
        {
            var p = new PendingRequests(timeoutSeconds: 10);
            string got = null;
            int id = p.Add("status", 0, (ok, reply) => got = reply["text"]);
            Assert.True(p.Complete(id, new Dictionary<string, string> { { "text", "fine" } }, 2));
            Assert.Equal("fine", got);
        }

        [Fact]
        public void TwoQuestionsGetTheirOwnAnswers()
        {
            var p = new PendingRequests(10);
            string a = null, b = null;
            int first = p.Add("status", 0, (ok, r) => a = r["text"]);
            int second = p.Add("selftest", 0, (ok, r) => b = r["text"]);
            Assert.NotEqual(first, second);
            p.Complete(second, new Dictionary<string, string> { { "text", "two" } }, 1);
            p.Complete(first, new Dictionary<string, string> { { "text", "one" } }, 1);
            Assert.Equal("one", a);
            Assert.Equal("two", b);
        }

        [Fact]
        public void AnAnswerArrivesOnlyOnce()
        {
            var p = new PendingRequests(10);
            int calls = 0;
            int id = p.Add("status", 0, (ok, r) => calls++);
            p.Complete(id, new Dictionary<string, string>(), 1);
            Assert.False(p.Complete(id, new Dictionary<string, string>(), 1));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void AnAnswerToNothingAskedIsIgnored()
        {
            Assert.False(new PendingRequests(10).Complete(42, new Dictionary<string, string>(), 1));
        }

        [Fact]
        public void AQuestionLeftUnansweredEndsWithAMessage()
        {
            var p = new PendingRequests(10);
            bool? okSeen = null;
            string text = null;
            p.Add("status", 0, (ok, r) => { okSeen = ok; text = r["text"]; });
            p.Expire(9.9);
            Assert.Null(okSeen);
            p.Expire(10.1);
            Assert.False(okSeen);
            Assert.Equal("the server did not answer \"status\" within 10 s", text);
        }

        [Fact]
        public void ALateAnswerAfterGivingUpIsIgnored()
        {
            var p = new PendingRequests(10);
            int calls = 0;
            int id = p.Add("status", 0, (ok, r) => calls++);
            p.Expire(11);
            Assert.False(p.Complete(id, new Dictionary<string, string>(), 12));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void LosingTheConnectionEndsEveryOpenQuestion()
        {
            var p = new PendingRequests(10);
            var texts = new List<string>();
            p.Add("status", 0, (ok, r) => texts.Add(r["text"]));
            p.Add("selftest", 0, (ok, r) => texts.Add(r["text"]));
            p.Drop("the connection to the server ended");
            Assert.Equal(new[] { "the connection to the server ended", "the connection to the server ended" }, texts);
            Assert.Equal(0, p.Count);
        }

        [Fact]
        public void AnAnswerThatFailsOnTheServerSaysSo()
        {
            var p = new PendingRequests(10);
            bool? okSeen = null;
            int id = p.Add("status", 0, (ok, r) => okSeen = ok);
            p.Complete(id, new Dictionary<string, string> { { "ok", "0" }, { "text", "refused" } }, 1);
            Assert.False(okSeen);
        }
    }
}
