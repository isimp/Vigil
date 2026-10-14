using System;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Vigil's own work shows in the per-mod timing like any mod's, one row per area and call, so
    /// the spawn marks' cost is visible again since they run inside Vigil; the recorder's own
    /// frame work stays in its own column and is not counted twice. A dedicated server, which has
    /// no panel, builds and keeps no panel results.
    /// </summary>
    public sealed class OwnTimeTests
    {
        [Fact]
        public void Vigil_s_own_work_is_named_like_a_mod_s_callback_by_area()
        {
            Assert.Equal("Vigil", OwnTime.Mod);
            Assert.Equal("Vigil :: Spawn marks.Update", OwnTime.Label("Spawn marks", "Update"));
            Assert.Equal("Vigil :: Panel.OnGUI", OwnTime.Label("Panel", "OnGUI"));
        }

        [Fact]
        public void The_recorder_s_own_frame_work_is_left_out_of_its_row()
        {
            Assert.Equal(700, OwnTime.Counted(1000, 2000, 300));
            Assert.Equal(1000, OwnTime.Counted(1000, 2000, 0));
            Assert.Equal(0, OwnTime.Counted(1000, 2000, 5000));
            Assert.Equal(1000, OwnTime.Counted(1000, 2000, -5));
        }

        [Fact]
        public void A_store_that_is_not_shown_keeps_no_results()
        {
            var store = new ResultStore { Shown = false };
            store.Put(ResultStore.Census, new Result("census", DateTime.Now));
            store.AddIncident(new Result("hitch", DateTime.Now));
            Assert.Null(store.Last(ResultStore.Census));
            Assert.Empty(store.Incidents);
            Assert.True(new ResultStore().Shown);
        }
    }
}
