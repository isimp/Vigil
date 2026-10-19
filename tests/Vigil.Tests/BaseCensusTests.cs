using System;
using System.Linq;
using System.Text.RegularExpressions;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A census of what is loaded around the player, for deciding which performance change is worth
    /// making: which characters are loaded and whose game runs them, what each costs in the physics
    /// steps, how many shadow casters a per-layer sun shadow distance would drop, how many building
    /// particle systems keep simulating off screen, and which lights draw shadows. Each section of
    /// the report leads with that finding.
    /// </summary>
    public sealed class BaseCensusTests
    {
        private static CensusScene Scene(float radius = 60) => new CensusScene
        {
            At = new DateTime(2026, 10, 19, 14, 3, 22), Radius = radius, Place = "Meadows, 1.2 km NE of the start",
            ShadowDistance = 150, Cascades = 4, PointLightCap = 40, PointShadowCap = 3, PixelLights = 4,
            WalkSeconds = 1.8, OwnMs = 212, Frames = 64, MaxFrameMs = 4.1,
            TimedSeconds = 5, Steps = 250, FixedUpdateMs = 200, PhysicsEngineMs = 300,
        };

        private static CharacterFacts Wolf(bool yours, bool tamed = true, double ms = 4) =>
            new CharacterFacts { Prefab = "Wolf", Yours = yours, Tamed = tamed, Body = Body.Awake, Ms = ms };

        private static RendererFacts Caster(string layer, float distance, string mod = null) =>
            new RendererFacts { Prefab = "piece_wall", Mod = mod, Layer = layer, Shadows = true, Distance = distance, Drawn = true, Instancing = Instancing.On, Batch = "wall" };

        private static ParticleFacts Fire(ParticleCulling mode = ParticleCulling.AlwaysSimulate, bool inView = false, bool playing = true, bool onPiece = true, int particles = 120) =>
            new ParticleFacts { Prefab = "fire_pit", OnPiece = onPiece, Mode = mode, Playing = playing, InView = inView, UnitySees = inView, Particles = particles };

        // Table rows in the report separate their cells by at least two spaces.
        private static string[] Row(string report, string first) =>
            report.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Select(l => Regex.Split(l, @"\s{2,}")).First(c => c[0] == first);

        private static string Section(string report, string heading)
        {
            var lines = report.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int at = lines.IndexOf("== " + heading + " ==");
            Assert.True(at >= 0, "no section " + heading);
            return string.Join("\n", lines.Skip(at + 1).TakeWhile(l => !l.StartsWith("== ", StringComparison.Ordinal)));
        }

        // ------------------------------------------------------------------ characters

        [Fact]
        public void AnotherPlayersWolvesAreCountedApartFromTheOnesYourGameRuns()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true));
            c.AddCharacter(Wolf(yours: true));
            c.AddCharacter(Wolf(yours: false));

            string report = c.Format(Scene(), null);

            var row = Row(Section(report, "Characters"), "Wolf");
            Assert.Equal("2", row[2]);
            Assert.Equal("1", row[3]);
            Assert.Contains("3 characters within 60 m: 2 run by your game, 1 by other players' games", c.CharactersLead(Scene()));
        }

        [Fact]
        public void TamedCharactersAreCountedAsTamedAndWildOnesAreNot()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true, tamed: true));
            c.AddCharacter(Wolf(yours: true, tamed: false));
            c.AddCharacter(new CharacterFacts { Prefab = "Player", Yours = true, Player = true, Body = Body.Awake, Ms = 2 });

            Assert.Equal("1", Row(c.Format(Scene(), null), "Wolf")[4]);
            Assert.Contains("1 tamed", c.CharactersLead(Scene()));
        }

        [Fact]
        public void AKindsShareIsItsTimeAgainstAllFixedUpdateWorkInTheSameSteps()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true, ms: 30));
            c.AddCharacter(Wolf(yours: false, ms: 10));

            var row = Row(c.Format(Scene(), null), "Wolf");

            // 40 ms over 250 steps, against 200 ms of all FixedUpdate work
            Assert.Equal("0.160", row[6]);
            Assert.Equal("20 %", row[7]);
        }

        [Fact]
        public void TheKindsTakingMostOfThePhysicsStepsLeadAndTheCostOfOneYoursAndOneTheirsIsSaid()
        {
            var c = new BaseCensus();
            c.AddCharacter(new CharacterFacts { Prefab = "Boar", Yours = true, Body = Body.Awake, Ms = 5 });
            c.AddCharacter(Wolf(yours: true, ms: 50));
            c.AddCharacter(Wolf(yours: false, ms: 10));

            string lead = c.CharactersLead(Scene());

            Assert.True(lead.IndexOf("Wolf", StringComparison.Ordinal) < lead.IndexOf("Boar", StringComparison.Ordinal), lead);
            // your two: 55 ms over 250 steps = 0.110 ms each; theirs: 10 ms = 0.040 ms
            Assert.Contains("one your game runs takes 0.110 ms a step, one another game runs 0.040 ms", lead);
            Assert.Contains("Unity's physics step itself took 1.200 ms a step", lead);
        }

        [Fact]
        public void CharactersThatCouldNotBeTimedSaySoInsteadOfShowingNoTime()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true, ms: double.NaN));
            var scene = Scene();
            scene.TimingProblem = "a census hook could not be put in";
            scene.FixedUpdateMs = double.NaN;

            Assert.Contains("not timed: a census hook could not be put in", c.CharactersLead(scene));
            Assert.Equal("-", Row(c.Format(scene, null), "Wolf")[6]);
        }

        [Fact]
        public void AKindNoneOfWhichWasTimedShowsNoTimeWhileTheOthersShowTheirs()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true, ms: 10));
            c.AddCharacter(new CharacterFacts { Prefab = "Player", Yours = false, Player = true, Body = Body.Awake });

            string report = c.Format(Scene(), null);

            Assert.Equal("-", Row(report, "Player")[6]);
            Assert.Equal("0.040", Row(report, "Wolf")[6]);
        }

        [Fact]
        public void AwakeBodiesAreCountedForCharactersAndForOtherMovingObjects()
        {
            var c = new BaseCensus();
            c.AddCharacter(new CharacterFacts { Prefab = "Lox", Yours = false, Body = Body.Awake, Ms = 1 });
            c.AddCharacter(new CharacterFacts { Prefab = "Lox", Yours = false, Body = Body.Asleep, Ms = 1 });
            c.AddMover(new MoverFacts { Prefab = "Cart", Yours = true, Body = Body.Awake, Ms = 2 });
            c.AddMover(new MoverFacts { Prefab = "Cart", Yours = true, Body = Body.Kinematic, Ms = 2 });

            string report = c.Format(Scene(), null);

            Assert.Equal("1", Row(Section(report, "Characters"), "Lox")[5]);
            var cart = Row(Section(report, "Other moving objects"), "Cart");
            Assert.Equal("2", cart[2]);
            Assert.Equal("1", cart[4]);
        }

        // ------------------------------------------------------------------ shadows

        [Fact]
        public void ShadowCastersPastTheirLayersDistanceAreTheOnesTheDistancesWouldDrop()
        {
            var c = new BaseCensus();
            c.AddRenderer(Caster("piece", 50));
            c.AddRenderer(Caster("piece", 70));        // past 60
            c.AddRenderer(Caster("item", 20));
            c.AddRenderer(Caster("item", 26));         // past 25
            c.AddRenderer(Caster("Default_small", 31)); // past 30

            var (dropped, casters) = c.ShadowsDropped(150, BaseCensus.LayerDistances);

            Assert.Equal(3, dropped);
            Assert.Equal(5, casters);
            Assert.Contains("would stop 3 of the 5 shadow casters within the 150 m shadow distance (60 %)", c.ShadowLead(Scene()));
        }

        [Fact]
        public void CastersOnLayersWithoutADistanceKeepTheirShadow()
        {
            var c = new BaseCensus();
            c.AddRenderer(Caster("static_solid", 140));
            c.AddRenderer(Caster("character", 100));

            Assert.Equal(0, c.ShadowsDropped(150, BaseCensus.LayerDistances).Dropped);
        }

        [Fact]
        public void CastersPastTheShadowDistanceCastNoneAlreadyAndAreNotCountedAsSaved()
        {
            var c = new BaseCensus();
            c.AddRenderer(Caster("piece", 40));
            c.AddRenderer(Caster("piece", 160));

            var (dropped, casters) = c.ShadowsDropped(150, BaseCensus.LayerDistances);

            Assert.Equal(0, dropped);
            Assert.Equal(1, casters);
            Assert.Equal("1", Row(Section(c.Format(Scene(), null), "Renderers and shadow casters"), "piece").Last());
        }

        [Fact]
        public void RenderersThatCastNoShadowAreNeverCountedAsShadowCasters()
        {
            var c = new BaseCensus();
            var r = Caster("piece", 90);
            r.Shadows = false;
            c.AddRenderer(r);

            Assert.Equal(0, c.ShadowsDropped(150, BaseCensus.LayerDistances).Casters);
            var row = Row(Section(c.Format(Scene(), null), "Renderers and shadow casters"), "piece");
            Assert.Equal("1", row[1]);
            Assert.Equal("0", row[3]);
        }

        [Fact]
        public void CastersAreSplitIntoTheDistanceBandsTheLayerDistancesNeed()
        {
            Assert.Equal(new[] { 25f, 30f, 40f, 60f, 150f }, BaseCensus.BandEdges(150));
            Assert.Equal(new[] { 25f, 30f, 40f, 60f, 80f }, BaseCensus.BandEdges(80));
            Assert.Equal(new[] { 25f, 30f, 35f }, BaseCensus.BandEdges(35));

            var c = new BaseCensus();
            foreach (float d in new[] { 10f, 26f, 35f, 45f, 61f, 149f, 151f }) c.AddRenderer(Caster("piece", d));
            var row = Row(Section(c.Format(Scene(), null), "Renderers and shadow casters"), "piece");

            // layer, renderers, drawn, casters, then 0-25 .. 60-150, then past 150
            Assert.Equal(new[] { "piece", "7", "7", "6", "1", "1", "1", "1", "2", "1" }, row);
        }

        [Fact]
        public void RenderersOfNoWorldObjectAreShownApartByLayer()
        {
            var c = new BaseCensus();
            c.AddRenderer(Caster("piece", 10));
            c.AddRenderer(new RendererFacts { Layer = "terrain", Shadows = true, Distance = 5, Drawn = true, WorldObject = false });

            string report = c.Format(Scene(), null);

            Assert.Equal("1", Row(Section(report, "Not part of a world object"), "terrain")[1]);
            Assert.DoesNotContain(Section(report, "Renderers and shadow casters").Split('\n'), l => l.Trim().StartsWith("terrain", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ instancing

        [Fact]
        public void RenderersWithoutInstancingWhoseMeshAndMaterialRepeatCouldBeDrawnTogether()
        {
            var c = new BaseCensus();
            for (int i = 0; i < 3; i++) c.AddRenderer(new RendererFacts { Prefab = "chair_bal", Mod = "BalrondFurnitureReborn", Layer = "piece", Distance = 5, Instancing = Instancing.Off, Batch = "chair" });
            c.AddRenderer(new RendererFacts { Prefab = "table_bal", Mod = "BalrondFurnitureReborn", Layer = "piece", Distance = 5, Instancing = Instancing.Off, Batch = "table" });

            Assert.Equal(3, c.CouldGroup);
            Assert.Contains("3 renderers draw without instancing though their mesh and material repeat", c.InstancingLead());
            Assert.Contains("BalrondFurnitureReborn 3", c.InstancingLead());
        }

        [Fact]
        public void RenderersThatAlreadyInstanceOrAreSkinnedNeverCountAsAbleToGroup()
        {
            var c = new BaseCensus();
            for (int i = 0; i < 3; i++) c.AddRenderer(new RendererFacts { Layer = "piece", Distance = 5, Instancing = Instancing.On, Batch = "wall" });
            for (int i = 0; i < 3; i++) c.AddRenderer(new RendererFacts { Layer = "character", Distance = 5, Instancing = Instancing.Skinned, Batch = "body" });

            Assert.Equal(0, c.CouldGroup);
            var row = Row(Section(c.Format(Scene(), null), "By mod"), BaseCensus.NoMod);
            Assert.Equal("3", row[4]); // instancing on
            Assert.Equal("3", row[7]); // skinned
        }

        // ------------------------------------------------------------------ particles

        [Fact]
        public void APieceFireThatAlwaysSimulatesOffScreenCountsAsPausable()
        {
            var c = new BaseCensus();
            c.AddParticles(Fire(particles: 120));
            c.AddParticles(Fire(particles: 80));

            Assert.Equal((2, 200), c.Pausable);
            Assert.Contains("2 particle systems on building pieces keep simulating off screen (always simulate), 200 particles", c.ParticlesLead());
        }

        [Fact]
        public void SystemsOnScreenSetToPauseStoppedOrNotOnPiecesAreNotPausable()
        {
            var c = new BaseCensus();
            c.AddParticles(Fire(inView: true));
            c.AddParticles(Fire(mode: ParticleCulling.Automatic));
            c.AddParticles(Fire(mode: ParticleCulling.PauseAndCatchup));
            c.AddParticles(Fire(mode: ParticleCulling.Pause));
            c.AddParticles(Fire(playing: false));
            c.AddParticles(Fire(onPiece: false));

            Assert.Equal((0, 0), c.Pausable);
        }

        [Fact]
        public void ASystemOnlyAnotherCameraSeesIsCountedApart()
        {
            var c = new BaseCensus();
            var p = Fire();
            p.UnitySees = true;
            c.AddParticles(p);

            Assert.Equal((1, 120), c.Pausable);
            Assert.Contains("1 of them Unity counts as seen by another camera", c.ParticlesLead());
        }

        // ------------------------------------------------------------------ lights

        [Fact]
        public void ALightTheGameSwitchedOffCountsAsOff()
        {
            var c = new BaseCensus();
            c.AddLight(new LightFacts { Prefab = "piece_brazier", Type = "Point", On = true, Shadows = LightShadow.None, Range = 10 });
            c.AddLight(new LightFacts { Prefab = "piece_brazier", Type = "Point", On = false, Shadows = LightShadow.None, Range = 10 });

            Assert.Contains("1 of 2 lights on", c.LightsLead(Scene()));
            Assert.Equal("1", Row(Section(c.Format(Scene(), null), "Lights"), "piece_brazier")[4]);
        }

        [Fact]
        public void ShadowCastingPointLightsNameTheShadowCastersInTheirRange()
        {
            var c = new BaseCensus();
            c.AddLight(new LightFacts { Prefab = "fire_pit", Type = "Point", On = true, Shadows = LightShadow.Soft, Range = 12, CastersInRange = 140 });
            c.AddLight(new LightFacts { Prefab = "fire_pit", Type = "Point", On = true, Shadows = LightShadow.None, Range = 12, CastersInRange = 0 });

            string lead = c.LightsLead(Scene());

            Assert.Contains("1 casting shadows (the game's cap: 3)", lead);
            Assert.Contains("140 shadow casters in their range", lead);
        }

        [Fact]
        public void TheCapsReadUnlimitedWhenTheSettingHasNone()
        {
            var c = new BaseCensus();
            c.AddLight(new LightFacts { Prefab = "torch", Type = "Point", On = true, Shadows = LightShadow.Hard, Range = 8 });
            var scene = Scene();
            scene.PointLightCap = -1;
            scene.PointShadowCap = -1;

            Assert.Contains("(the game's cap: none)", c.LightsLead(scene));
        }

        // ------------------------------------------------------------------ the report

        [Fact]
        public void EachSectionOfTheReportLeadsWithItsFinding()
        {
            var c = new BaseCensus();
            c.AddCharacter(Wolf(yours: true));
            c.AddRenderer(Caster("piece", 70));
            c.AddParticles(Fire());
            c.AddLight(new LightFacts { Prefab = "torch", Type = "Point", On = true, Range = 8 });
            var scene = Scene();

            string report = c.Format(scene, new[] { "material copy line" });

            Assert.StartsWith(c.CharactersLead(scene), Section(report, "Characters").TrimStart('\n'));
            Assert.StartsWith(c.ShadowLead(scene), Section(report, "Renderers and shadow casters").TrimStart('\n'));
            Assert.StartsWith(c.ParticlesLead(), Section(report, "Particle systems").TrimStart('\n'));
            Assert.StartsWith(c.LightsLead(scene), Section(report, "Lights").TrimStart('\n'));
            Assert.Contains("material copy line", Section(report, "Material copies"));
            var order = new[] { "== Characters ==", "== Renderers and shadow casters ==", "== Particle systems ==", "== Lights ==", "== Material copies ==" }
                .Select(h => report.IndexOf(h, StringComparison.Ordinal)).ToArray();
            Assert.Equal(order.OrderBy(i => i), order);
        }

        [Fact]
        public void TheReportSaysWhereWhenAndWhatTheCensusCost()
        {
            string report = new BaseCensus().Format(Scene(), null);

            Assert.StartsWith("Vigil census   2026-10-19 14:03:22", report);
            Assert.Contains("within 60 m of you, Meadows, 1.2 km NE of the start", report);
            Assert.Contains("The census took 1.8 s: 212 ms of Vigil's own work over 64 frames, at most 4.1 ms in one", report);
            Assert.Contains("sun shadows to 150 m in 4 cascades", report);
        }

        [Fact]
        public void ObjectsThatCouldNotBeReadAreCountedInTheHeader()
        {
            var scene = Scene();
            Assert.DoesNotContain("could not be read", new BaseCensus().Format(scene, null));

            scene.Unreadable = 3;

            Assert.Contains("3 objects could not be read and are left out.", new BaseCensus().Format(scene, null));
        }

        [Fact]
        public void NothingWithinTheRadiusSaysSoInEverySection()
        {
            var c = new BaseCensus();

            Assert.Equal("No characters within 60 m.", c.CharactersLead(Scene()));
            Assert.Equal("No shadow casters within 60 m.", c.ShadowLead(Scene()));
            Assert.Equal("No particle systems.", c.ParticlesLead());
            Assert.Equal("No lights.", c.LightsLead(Scene()));
            Assert.Equal("No characters loaded.", c.CharactersLead(Scene(radius: 0)));
        }

        [Fact]
        public void AModsNameIsShownAndUnmatchedObjectsCountForTheGame()
        {
            var c = new BaseCensus();
            c.AddRenderer(Caster("piece", 10, mod: "BalrondFurnitureReborn"));
            c.AddRenderer(Caster("piece", 10));

            string mods = Section(c.Format(Scene(), null), "By mod");

            Assert.Equal("1", Row(mods, "BalrondFurnitureReborn")[1]);
            Assert.Equal("1", Row(mods, BaseCensus.NoMod)[1]);
        }
    }
}
