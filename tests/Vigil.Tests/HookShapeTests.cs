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

        private static string ProjectRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Vigil.csproj"))) return dir.FullName;
            throw new DirectoryNotFoundException("Vigil.csproj not found above " + AppContext.BaseDirectory);
        }
    }
}
