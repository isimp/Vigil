using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A report about a frozen frame should say in plain words what that frame spent its time on,
    /// biggest first, so nobody has to read raw numbers to find the culprit.
    /// </summary>
    public sealed class HitchAnalysisTests
    {
        private static FrameBreakdown Frame(float dt) => new FrameBreakdown { DtMs = dt, FixedSteps = 1 };

        [Fact]
        public void TheWorkThatTookMostOfTheFrameIsNamedFirstWithItsShare()
        {
            var f = Frame(900);
            f.Phases.Add(new Timed("ZNetScene.Update", 120));
            f.Phases.Add(new Timed("ZoneSystem.Update", 700));

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("ZoneSystem.Update", a.Causes.First());
            Assert.Contains("700 ms", a.Causes.First());
            Assert.Contains("78%", a.Causes.First());
            Assert.Contains("ZNetScene.Update", a.Causes[1]);
        }

        [Fact]
        public void WorkInsideOtherTimedWorkIsShownButNotCountedTwice()
        {
            var f = Frame(900);
            f.Phases.Add(new Timed("ZoneSystem.Update", 700));
            f.Phases.Add(new Timed("ZoneSystem.SpawnZone", 650, parent: "ZoneSystem.Update"));

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("ZoneSystem.SpawnZone") && c.Contains("inside ZoneSystem.Update"));
            Assert.Equal(200, a.UnexplainedMs, 1);
        }

        [Fact]
        public void AModsCodeIsNamedWithTheMod()
        {
            var f = Frame(400);
            f.Mods.Add(new Timed("SomeMod :: Thing.Update", 300));

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("SomeMod :: Thing.Update", a.Causes.First());
            Assert.Contains("mod", a.Causes.First());
        }

        [Fact]
        public void SmallWorkIsLeftOutSoTheCulpritStandsOut()
        {
            var f = Frame(1000);
            f.Phases.Add(new Timed("Big", 800));
            f.Phases.Add(new Timed("Tiny", 12));

            var a = HitchAnalysis.Explain(f);

            Assert.DoesNotContain(a.Causes, c => c.Contains("Tiny"));
        }

        [Fact]
        public void AGarbageCollectionDuringTheFrameIsMentioned()
        {
            var f = Frame(600);
            f.GcCollections = 1;

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("garbage collection"));
        }

        [Fact]
        public void TimeSpentOutsideEverythingTimedIsCalledOut()
        {
            var f = Frame(1000);
            f.Phases.Add(new Timed("Small", 50));

            var a = HitchAnalysis.Explain(f);

            Assert.Equal(950, a.UnexplainedMs, 1);
            Assert.Contains(a.Causes, c => c.Contains("outside the timed code") && c.Contains("950 ms"));
        }

        [Fact]
        public void HeavyPagingIsSuggestedWhenTheUntimedPartCoincidesWithIt()
        {
            var f = Frame(1000);
            f.PageFaultsPerSec = 50000;

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("paging"));
        }

        [Fact]
        public void NoPagingHintWithoutPaging()
        {
            var f = Frame(1000);
            f.PageFaultsPerSec = 200;

            var a = HitchAnalysis.Explain(f);

            Assert.DoesNotContain(a.Causes, c => c.Contains("paging"));
        }

        [Fact]
        public void PhysicsCatchingUpIsMentioned()
        {
            var f = Frame(300);
            f.FixedSteps = 6;

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("6 physics steps"));
        }

        [Fact]
        public void RenderingIsNamedWhenItTookTheFrame()
        {
            var f = Frame(500);
            f.RenderMs = 400;

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("rendering", a.Causes.First());
        }

        [Fact]
        public void ALoadingScreenOrTeleportIsSaidUpFront()
        {
            var f = Frame(5000);
            f.Situation = "teleporting";

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("teleporting", a.Headline);
        }

        [Fact]
        public void TheHeadlineGivesTheFrameLengthAndTheBiggestCause()
        {
            var f = Frame(1234);
            f.Phases.Add(new Timed("ZNet.Update", 1000));

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("1234 ms", a.Headline);
            Assert.Contains("ZNet.Update", a.Headline);
        }

        [Fact]
        public void WorkRepeatedManyTimesInOneFrameSaysHowOften()
        {
            // A mod that spends seeds one by one makes the game redo its inventory checks each time.
            var f = Frame(500);
            f.Phases.Add(new Timed("Player.Update", 480));
            f.Phases.Add(new Timed("Player.OnInventoryChanged", 420, parent: "Player.Update", calls: 76));

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("Player.OnInventoryChanged") && c.Contains("420 ms in 76 calls"));
            Assert.Contains("76 calls", a.Causes.First(c => c.Contains("OnInventoryChanged")));
        }

        [Fact]
        public void WorkDoneOnceInAFrameDoesNotMentionACallCount()
        {
            var f = Frame(500);
            f.Phases.Add(new Timed("MapTable.OnWrite", 450, parent: "Player.Update", calls: 1));

            var a = HitchAnalysis.Explain(f);

            string cause = a.Causes.First(c => c.Contains("MapTable.OnWrite"));
            Assert.DoesNotContain("call", cause);
            Assert.Contains("MapTable.OnWrite: 450 ms (", cause);
        }

        [Fact]
        public void A_frame_Vigil_stalled_writing_a_report_says_so_instead_of_blaming_the_engine()
        {
            var f = Frame(205);
            f.OwnMs = 170;
            f.Phases.Add(new Timed("MonoUpdaters.FixedUpdate", 22, calls: 10));
            var a = HitchAnalysis.Explain(f);
            Assert.Contains("Vigil itself", a.Headline);
            Assert.Contains("170 ms", a.Headline);
            Assert.DoesNotContain(a.Causes, c => c.Contains("outside the timed code"));
        }

        [Fact]
        public void A_little_Vigil_work_in_a_frame_is_left_out_like_any_small_work()
        {
            var f = Frame(300);
            f.OwnMs = 2;
            f.Phases.Add(new Timed("Player.Update", 280));
            var a = HitchAnalysis.Explain(f);
            Assert.DoesNotContain(a.Causes, c => c.Contains("Vigil itself"));
            Assert.StartsWith("300 ms frame: Player.Update", a.Headline);
        }

        [Fact]
        public void TheEnginesPhysicsStepIsNamedWithItsStepsAndNoLongerCountsAsOutsideTheTimedCode()
        {
            var f = Frame(500);
            f.FixedSteps = 10;
            f.PhysicsTimed = true;
            f.PhysicsMs = 400;

            var a = HitchAnalysis.Explain(f);

            Assert.Contains("the physics engine: 400 ms in 10 steps (80% of the frame)", a.Causes.First());
            Assert.Equal(100, a.UnexplainedMs, 1);
            Assert.StartsWith("500 ms frame: the physics engine", a.Headline);
        }

        [Fact]
        public void WithThePhysicsStepTimedTheOutsideShareNoLongerGuessesPhysics()
        {
            var f = Frame(500);
            f.PhysicsTimed = true;
            f.PhysicsMs = 5;

            var outside = HitchAnalysis.Explain(f).Causes.Single(c => c.Contains("outside the timed code"));

            Assert.DoesNotContain("physics", outside);
            Assert.Contains("495 ms", outside);
        }

        [Fact]
        public void WithoutThePhysicsTimerPhysicsStaysAmongTheOutsideGuesses()
        {
            var f = Frame(500);
            f.PhysicsMs = 400; // read by nothing: the timer is not in place

            var a = HitchAnalysis.Explain(f);

            Assert.Contains(a.Causes, c => c.Contains("outside the timed code") && c.Contains("physics"));
            Assert.Equal(500, a.UnexplainedMs, 1);
        }

        [Fact]
        public void TimedWorkKnowsHowOftenItRanAndDefaultsToOnce()
        {
            Assert.Equal(1, new Timed("A", 1).Calls);
            Assert.Equal(3, new Timed("A", 1, calls: 3).Calls);
            Assert.Equal(1, new Timed("A", 1, calls: 0).Calls);
        }
    }
}
