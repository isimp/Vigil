using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A load test runs a quiet baseline, then world-update traffic that rises step by step in one
    /// direction, then a quiet stretch that shows how fast the server catches up, and the same for
    /// the other direction. Both machines run the same schedule from the same start.
    /// </summary>
    public sealed class LoadPlanTests
    {
        [Fact]
        public void Both_directions_run_up_first_then_down_each_followed_by_a_recovery()
        {
            var plan = LoadPlan.For("both");
            var names = plan.Stages.Select(s => s.Name).ToList();
            Assert.Equal("baseline", names.First());
            Assert.Equal("up 50 kB/s", names[1]);
            Assert.Equal("up recovery", names[6]);
            Assert.Equal("down 50 kB/s", names[7]);
            Assert.Equal("down recovery", names.Last());
            Assert.Equal(13, plan.Stages.Count);
        }

        [Fact]
        public void One_direction_only_leaves_the_other_out()
        {
            var plan = LoadPlan.For("down");
            Assert.DoesNotContain(plan.Stages, s => s.Direction == "up");
            Assert.Equal(7, plan.Stages.Count);
        }

        [Fact]
        public void An_unknown_direction_means_both()
        {
            Assert.Equal(13, LoadPlan.For("sideways").Stages.Count);
        }

        [Fact]
        public void The_rate_rises_each_step()
        {
            var rates = LoadPlan.For("up").Stages.Where(s => s.Kind == LoadPlan.Kind.Load).Select(s => s.RateKbps).ToList();
            Assert.Equal(new double[] { 50, 100, 200, 400, 800 }, rates);
        }

        [Fact]
        public void The_stage_at_a_time_follows_the_durations()
        {
            var plan = LoadPlan.For("up");
            Assert.Equal("baseline", plan.At(0).Name);
            Assert.Equal("baseline", plan.At(LoadPlan.BaselineSeconds - 0.01).Name);
            Assert.Equal("up 50 kB/s", plan.At(LoadPlan.BaselineSeconds).Name);
            Assert.Equal("up recovery", plan.At(plan.TotalSeconds - 0.01).Name);
            Assert.Null(plan.At(plan.TotalSeconds));
        }

        [Fact]
        public void Enough_objects_are_changed_ten_times_a_second_to_reach_the_rate()
        {
            Assert.Equal(26, LoadPlan.ObjectsFor(50, payloadBytes: 200));
            Assert.Equal(410, LoadPlan.ObjectsFor(800, payloadBytes: 200));
            Assert.Equal(0, LoadPlan.ObjectsFor(0, payloadBytes: 200));
        }

        [Fact]
        public void A_schedule_has_no_stage_before_it_starts()
        {
            var s = new LoadSchedule(LoadPlan.For("up"));
            Assert.Null(s.Current(100));
            Assert.False(s.Finished(100));
        }

        [Fact]
        public void A_schedule_counts_from_its_start()
        {
            var s = new LoadSchedule(LoadPlan.For("up"));
            s.Start(1000);
            Assert.Equal("baseline", s.Current(1000).Name);
            Assert.Equal("up 50 kB/s", s.Current(1000 + LoadPlan.BaselineSeconds + 1).Name);
            Assert.Equal(1.0, s.SecondsIntoStage(1000 + LoadPlan.BaselineSeconds + 1), 3);
        }

        [Fact]
        public void An_abort_during_a_load_step_jumps_to_that_directions_recovery_and_ends_after_it()
        {
            var plan = LoadPlan.For("both");
            var s = new LoadSchedule(plan);
            s.Start(0);
            double inUp200 = LoadPlan.BaselineSeconds + 2 * LoadPlan.StepSeconds + 5;
            Assert.Equal("up 200 kB/s", s.Current(inUp200).Name);

            s.Abort("server too slow", inUp200);

            Assert.Equal("up recovery", s.Current(inUp200).Name);
            Assert.Equal(0, s.SecondsIntoStage(inUp200), 3);
            Assert.Equal("up recovery", s.Current(inUp200 + LoadPlan.RecoverySeconds - 0.1).Name);
            Assert.Null(s.Current(inUp200 + LoadPlan.RecoverySeconds + 0.1));
            Assert.True(s.Finished(inUp200 + LoadPlan.RecoverySeconds + 0.1));
            Assert.Equal("server too slow", s.AbortReason);
        }

        [Fact]
        public void An_abort_during_a_recovery_lets_that_recovery_finish_and_nothing_after_it()
        {
            var plan = LoadPlan.For("both");
            var s = new LoadSchedule(plan);
            s.Start(0);
            var upRecovery = plan.Stages.First(x => x.Name == "up recovery");
            double t = upRecovery.Start + 3;
            s.Abort("stopped", t);
            Assert.Equal("up recovery", s.Current(t).Name);
            Assert.Equal(3, s.SecondsIntoStage(t), 3);
            Assert.Null(s.Current(upRecovery.Start + upRecovery.Seconds + 0.1));
        }

        [Fact]
        public void An_abort_in_the_baseline_ends_at_once()
        {
            var s = new LoadSchedule(LoadPlan.For("up"));
            s.Start(0);
            s.Abort("stopped", 3);
            Assert.Null(s.Current(3));
            Assert.True(s.Finished(3));
        }

        [Fact]
        public void Only_the_first_abort_reason_is_kept()
        {
            var s = new LoadSchedule(LoadPlan.For("up"));
            s.Start(0);
            s.Abort("first", LoadPlan.BaselineSeconds + 1);
            s.Abort("second", LoadPlan.BaselineSeconds + 2);
            Assert.Equal("first", s.AbortReason);
        }
    }
}
