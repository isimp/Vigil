using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A deep capture must not freeze the game: its timers go in a few per frame within a small
    /// time budget, methods that cannot matter frame to frame are left out, and methods called so
    /// often that the timer would cost more than they do are dropped once that is known.
    /// </summary>
    public sealed class DeepPlanTests
    {
        [Theory]
        [InlineData("Awake")]
        [InlineData("Start")]
        [InlineData("OnDestroy")]
        [InlineData("OnEnable")]
        [InlineData("OnDisable")]
        [InlineData(".ctor")]
        [InlineData(".cctor")]
        [InlineData("LoadPlayerData")]
        [InlineData("SaveWorld")]
        [InlineData("SetupLocations")]
        [InlineData("InitTerminal")]
        [InlineData("RegisterRPCs")]
        [InlineData("CopyOtherDB")]
        public void WorkThatRunsOnceIsNotTimed(string method)
        {
            Assert.False(DeepPlan.WorthTiming(method));
        }

        [Theory]
        [InlineData("Update")]
        [InlineData("FixedUpdate")]
        [InlineData("CustomFixedUpdate")]
        [InlineData("UpdateAI")]
        [InlineData("GetHoverText")]
        [InlineData("IsOwner")]
        [InlineData("Interact")]
        [InlineData("Starter")]
        [InlineData("Loader")]
        [InlineData("Initiative")]
        public void WorkThatCanRunEveryFrameIsTimed(string method)
        {
            Assert.True(DeepPlan.WorthTiming(method));
        }

        [Fact]
        public void AMethodCalledVeryOftenThatCostsAlmostNothingIsDropped()
        {
            Assert.True(DeepPlan.TooHot(callsPerFrame: 1449, microsPerCall: 0.35));
        }

        [Fact]
        public void AMethodCalledOftenButCostlyIsKept()
        {
            Assert.False(DeepPlan.TooHot(callsPerFrame: 1449, microsPerCall: 25));
        }

        [Fact]
        public void ACheapMethodCalledRarelyIsKept()
        {
            Assert.False(DeepPlan.TooHot(callsPerFrame: 3, microsPerCall: 0.2));
        }

        [Fact]
        public void EachFrameDoesOnlyAsMuchAsTheBudgetAllows()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2, 3, 4, 5, 6 });
            double clock = 0;
            var done = new List<int>();

            int n = q.Step(i => { done.Add(i); clock += 3; }, () => clock, budgetMs: 6);

            Assert.Equal(2, n);
            Assert.Equal(new[] { 1, 2 }, done);
            Assert.Equal(4, q.Count);
        }

        [Fact]
        public void EveryFrameMakesProgressEvenWhenOneItemTakesLongerThanTheBudget()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2 });
            double clock = 0;

            int n = q.Step(i => clock += 50, () => clock, budgetMs: 6);

            Assert.Equal(1, n);
            Assert.Equal(1, q.Count);
        }

        [Fact]
        public void OverManyFramesTheCostStaysWithinTheBudgetEvenWhenOneItemCostsMore()
        {
            // One hook costs 10 ms; the budget is 4 ms a frame on average.
            var q = new BudgetedQueue<int>();
            q.Add(new int[1000]);
            double clock = 0;
            for (int frame = 0; frame < 100; frame++) q.Step(i => clock += 10, () => clock, budgetMs: 4);

            Assert.InRange(clock / 100, 3.5, 4.5);
        }

        [Fact]
        public void AFrameAfterAnExpensiveItemCanSkipWorkToCatchUp()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2, 3 });
            double clock = 0;
            q.Step(i => clock += 10, () => clock, budgetMs: 4);

            Assert.Equal(0, q.Step(i => clock += 10, () => clock, budgetMs: 4));
        }

        [Fact]
        public void UnusedBudgetDoesNotPileUpIntoALaterBurst()
        {
            var q = new BudgetedQueue<int>();
            double clock = 0;
            q.Add(new[] { 1 });
            q.Step(i => clock += 1, () => clock, budgetMs: 6);
            q.Add(new int[20]);

            Assert.Equal(6, q.Step(i => clock += 1, () => clock, budgetMs: 6));
        }

        [Fact]
        public void TimeSpentElsewhereInTheFrameDoesNotCountAgainstTheBudget()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2 });
            double clock = 1000;

            Assert.Equal(2, q.Step(i => clock += 1, () => clock, budgetMs: 6));
        }

        [Fact]
        public void TheWorkFinishesOverSeveralFrames()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2, 3, 4, 5 });
            var done = new List<int>();
            int frames = 0;
            while (q.Count > 0)
            {
                double clock = 0;
                q.Step(i => { done.Add(i); clock += 4; }, () => clock, budgetMs: 6);
                frames++;
            }

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, done);
            Assert.Equal(3, frames);
        }

        [Fact]
        public void AnEmptyQueueDoesNothing()
        {
            var q = new BudgetedQueue<int>();

            Assert.Equal(0, q.Step(i => { }, () => 0, 6));
        }

        [Fact]
        public void WorkStillQueuedWhenTheCaptureEndsIsDroppedNotDone()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2, 3 });
            q.Clear();
            var done = new List<int>();

            Assert.Equal(0, q.Count);
            Assert.Equal(0, q.Step(i => done.Add(i), () => 0, 6));
            Assert.Empty(done);
        }

        // ------------------------------------------------------------------ leaving the world

        [Fact]
        public void LeavingTheWorldWritesTheReportOfACaptureThatIsMeasuringAndTakesEveryTimerOut()
        {
            Assert.Equal((true, true), DeepPlan.OnLeaving(DeepPhase.Running));
        }

        [Fact]
        public void ACaptureStillPuttingItsTimersInIsDroppedWithoutAReportWhenTheWorldIsLeft()
        {
            Assert.Equal((false, true), DeepPlan.OnLeaving(DeepPhase.Installing));
        }

        [Fact]
        public void TimersStillComingOutAllComeOutAtOnceWhenTheWorldIsLeft()
        {
            Assert.Equal((false, true), DeepPlan.OnLeaving(DeepPhase.Removing));
        }

        [Fact]
        public void LeavingTheWorldWithoutACaptureChangesNothing()
        {
            Assert.Equal((false, false), DeepPlan.OnLeaving(DeepPhase.Idle));
        }

        [Fact]
        public void TakingEveryTimerOutAtOnceDoesAllTheWorkWhateverTheBudget()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2, 3, 4, 5 });
            double clock = 0;
            var done = new List<int>();
            q.Step(i => { done.Add(i); clock += 50; }, () => clock, budgetMs: 6);

            int n = q.Drain(i => { done.Add(i); clock += 50; });

            Assert.Equal(4, n);
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, done);
            Assert.Equal(0, q.Count);
        }

        [Fact]
        public void WorkQueuedAfterTakingEverythingOutAtOnceGetsItsNormalBudgetAgain()
        {
            var q = new BudgetedQueue<int>();
            q.Add(new[] { 1, 2 });
            double clock = 0;
            q.Step(i => clock += 50, () => clock, budgetMs: 6); // one item paid for with the next frames' budget
            q.Drain(i => clock += 50);
            q.Add(new[] { 3, 4, 5 });

            int n = q.Step(i => clock += 3, () => clock, budgetMs: 6);

            Assert.Equal(2, n);
        }

        [Fact]
        public void TheEventSaysWhatLeavingTheWorldDidToTheCaptureAndWhatItCost()
        {
            Assert.Equal("deep capture ended as you left the world: report written, 1312 timers taken out in 5.8 s",
                DeepPlan.LeftLine(DeepPhase.Running, 1312, 5812));
            Assert.Equal("deep capture dropped as you left the world before it measured: 640 timers taken out in 850 ms",
                DeepPlan.LeftLine(DeepPhase.Installing, 640, 850.4));
            Assert.Equal("deep capture's last 1 timer taken out at once as you left the world, in 4 ms",
                DeepPlan.LeftLine(DeepPhase.Removing, 1, 4.2));
        }
    }
}
