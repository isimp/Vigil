using System;
using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The panel shows each tool's last result under the tool, and the session's last incidents
    /// in the incident list, each of which opens to its own result. The answer at the bottom of
    /// the panel is only for a tool that brought no result, such as one that failed.
    /// </summary>
    public sealed class ResultStoreTests
    {
        private static readonly DateTime At = new DateTime(2026, 10, 13, 15, 0, 0);

        [Fact]
        public void A_tools_new_result_takes_the_place_of_its_last_and_leaves_the_others_alone()
        {
            var store = new ResultStore();
            var census = new Result("Count objects", At);
            var memory = new Result("What fills memory", At);
            var again = new Result("Count objects", At.AddMinutes(1));
            store.Put(ResultStore.Census, census);
            store.Put(ResultStore.Memory, memory);
            store.Put(ResultStore.Census, again);
            Assert.Same(again, store.Last(ResultStore.Census));
            Assert.Same(memory, store.Last(ResultStore.Memory));
            Assert.Null(store.Last(ResultStore.Patches));
        }

        [Fact]
        public void A_closed_result_is_gone_until_the_tool_runs_again()
        {
            var store = new ResultStore();
            store.Put(ResultStore.Trace, new Result("Material copies", At));
            store.Forget(ResultStore.Trace);
            Assert.Null(store.Last(ResultStore.Trace));
            var next = new Result("Material copies", At.AddMinutes(2));
            store.Put(ResultStore.Trace, next);
            Assert.Same(next, store.Last(ResultStore.Trace));
        }

        [Fact]
        public void The_session_keeps_its_last_eight_incidents_newest_first()
        {
            var store = new ResultStore();
            for (int i = 0; i < 10; i++)
            {
                store.AddIncident(new Result("Incident", At.AddSeconds(i)));
            }

            Assert.Equal(ResultStore.IncidentsKept, store.Incidents.Count);
            Assert.Equal(At.AddSeconds(9), store.Incidents[0].At);
            Assert.Equal(At.AddSeconds(2), store.Incidents.Last().At);
        }

        [Fact]
        public void An_answer_shows_only_when_the_tool_brought_no_result()
        {
            var store = new ResultStore();
            var before = store.Last(ResultStore.Patches);
            Assert.False(store.CameSince(ResultStore.Patches, before));
            store.Put(ResultStore.Patches, new Result("Harmony patch map", At));
            Assert.True(store.CameSince(ResultStore.Patches, before));

            var last = store.Last(ResultStore.Patches);
            Assert.False(store.CameSince(ResultStore.Patches, last));
            store.Put(ResultStore.Patches, new Result("Harmony patch map", At.AddMinutes(1)));
            Assert.True(store.CameSince(ResultStore.Patches, last));
        }
    }
}
