using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The deep capture's report gives each mod's own patch code: the time of its prefixes, postfixes
    /// and finalizers on the costliest game methods, per frame, the timers' own cost taken off, the
    /// costliest mod first with its costliest patch; and which mods change the game's code with
    /// transpilers, which can only be timed inside the methods they change.
    /// </summary>
    public sealed class PatchTimeTests
    {
        private static readonly List<PatchTiming> Timed = new List<PatchTiming>
        {
            new PatchTiming("Seasonality", "Player.UpdateEnvStatusEffects postfix", 2000, 60.0),
            new PatchTiming("Protective Wards", "WearNTear.UpdateWear postfix", 40000, 300.0),
            new PatchTiming("Protective Wards", "Piece.SetCreator prefix", 160, 12.0),
            new PatchTiming("Seasonality", "EnvMan.IsCold postfix", 6000, 18.0),
        };

        [Fact]
        public void Each_mod_gets_its_own_patch_time_per_frame_the_costliest_first_with_its_costliest_patch()
        {
            var rows = PatchTime.ByMod(Timed, frames: 400, perCallMs: 0.0002);
            Assert.Equal(new[] { "Protective Wards", "Seasonality" }, new[] { rows[0].Mod, rows[1].Mod });
            Assert.Equal((300.0 - 40000 * 0.0002 + 12.0 - 160 * 0.0002) / 400, rows[0].MsPerFrame, 9);
            Assert.Equal(40160 / 400.0, rows[0].CallsPerFrame, 9);
            Assert.Equal("WearNTear.UpdateWear postfix", rows[0].Top);
            Assert.Equal((300.0 - 40000 * 0.0002) / 400, rows[0].TopMsPerFrame, 9);
        }

        [Fact]
        public void A_line_reads_like_the_rest_of_the_report()
        {
            var row = PatchTime.ByMod(Timed, frames: 400, perCallMs: 0)[0];
            Assert.Equal("      0.780 ms  100.4 calls  Protective Wards   top: WearNTear.UpdateWear postfix 0.750 ms", PatchTime.Line(row));
        }

        [Fact]
        public void The_timers_cost_never_takes_a_patch_below_nothing()
        {
            var rows = PatchTime.ByMod(new[] { new PatchTiming("Tiny", "Thing.Do prefix", 1000, 0.05) }, frames: 10, perCallMs: 0.001);
            Assert.Equal(0.0, rows[0].MsPerFrame, 9);
        }

        [Fact]
        public void Transpilers_are_listed_by_mod_with_how_many_methods_they_change()
        {
            var counts = new Dictionary<string, int> { ["Jotunn"] = 1, ["ValheimCommunityPatch"] = 14, ["Get Out Of The Water"] = 2 };
            Assert.Equal("ValheimCommunityPatch 14 methods, Get Out Of The Water 2, Jotunn 1", PatchTime.TranspilerLine(counts));
            Assert.Equal("none", PatchTime.TranspilerLine(new Dictionary<string, int>()));
        }
    }
}
