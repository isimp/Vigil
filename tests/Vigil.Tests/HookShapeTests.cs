using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Vigil's hooks must never change what a game method does. In the HarmonyX 2.9 that
    /// BepInEx ships, rebuilding a method through any transpiler, even one that changes nothing,
    /// turns a "break" out of a try block inside a loop into another round of that loop: with a
    /// transpiler on Terminal.AddString the console printed the same line over and over and the
    /// game froze. So Vigil adds prefixes, postfixes and finalizers only.
    /// tools/HookShapeCheck runs the real hooks on a method of that shape against the real HarmonyX.
    /// </summary>
    public sealed class HookShapeTests
    {
        [Fact]
        public void Vigil_never_adds_a_transpiler()
        {
            var offenders = Directory.GetFiles(Path.Combine(ProjectRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => Regex.IsMatch(Regex.Replace(File.ReadAllText(f), "//.*", ""),
                    @"AddTranspiler\s*\(|transpiler\s*:|HarmonyTranspiler|IEnumerable<CodeInstruction>|ilmanipulator\s*:"))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void The_chest_scroll_patch_takes_CycleChests_player_by_position_as_its_step_is_static()
        {
            // CycleChests' input step is a static method whose one parameter is named __instance; Harmony
            // fills a patch parameter of that name with the object the method runs on, null for a static
            // method, so the player has to be taken as the first argument (__0).
            string code = Regex.Replace(File.ReadAllText(Path.Combine(ProjectRoot(), "src", "Recorder", "ChestScrollPatch.cs")), "//.*", "");
            Assert.Matches(@"BeforeInputStep\(\s*Player\s+__0\s*\)", code);
            Assert.DoesNotMatch(@"BeforeInputStep\([^)]*__instance", code);
        }

        [Fact]
        public void The_EasyRelocate_patches_take_the_arguments_of_its_static_methods_by_position()
        {
            // TryPatch, GhostTint.Apply and GhostBuilder.Create are static: a patch parameter named
            // __instance would be null there, and one named after their own parameters is a guess at
            // names an update may change, so their arguments are taken by position.
            string code = Regex.Replace(File.ReadAllText(Path.Combine(ProjectRoot(), "src", "Recorder", "RelocatePatch.cs")), "//.*", "");
            Assert.Matches(@"BeforeTypeSearch\(\s*string\s+__1\s*\)", code);
            Assert.Matches(@"BeforeTint\(\s*GameObject\s+__0\s*,\s*bool\s+__1\s*\)", code);
            Assert.Matches(@"AfterCreateGhost\(\s*GameObject\s+__result\s*\)", code);
            Assert.Matches(@"BeforeGhostUpdate\(\s*object\s+__instance\s*,\s*Player\s+__0\s*\)", code);
        }

        private static string ProjectRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Vigil.csproj"))) return dir.FullName;
            throw new DirectoryNotFoundException("Vigil.csproj not found above " + AppContext.BaseDirectory);
        }
    }
}
