using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Finding a type by its full name among the loaded assemblies. Harmony's own lookup lists every type
    /// of every assembly again on each call, and twice over for a name it cannot find, which costs seconds
    /// with a modded game's hundred and more assemblies and logs an error for each assembly with a type
    /// that cannot load. Vigil's lookup asks each assembly for the one name and keeps the answer.
    /// </summary>
    public sealed class TypeLookupTests
    {
        private static Assembly[] Here() => new[] { typeof(object).Assembly, typeof(Uri).Assembly, typeof(TypeLookupTests).Assembly };

        [Fact]
        public void Vigil_never_asks_Harmony_to_find_a_type_by_name()
        {
            // AccessTools.TypeByName, and every AccessTools call given "Type:Member" as text, run Harmony's own search.
            string src = Path.Combine(ProjectRoot(), "src");
            var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                .SelectMany(f => System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(f), "//.*", "")
                    .Split('\n')
                    .Select((line, i) => new { File = Path.GetFileName(f), Line = i + 1, Text = line }))
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"AccessTools\s*\.\s*(TypeByName\b|\w+\s*\(\s*""|\w+\s*\(\s*\$"")"))
                .Select(l => l.File + ":" + l.Line)
                .ToList();
            Assert.Empty(offenders);
        }

        private static string ProjectRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Vigil.csproj"))) return dir.FullName;
            throw new DirectoryNotFoundException("Vigil.csproj not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void A_type_is_found_by_its_full_name()
        {
            var lookup = new TypeLookup(Here);
            Assert.Equal(typeof(StartupTimeline), lookup.Find("Vigil.Core.StartupTimeline"));
            Assert.Equal(typeof(Uri), lookup.Find("System.Uri"));
        }

        [Fact]
        public void A_type_in_no_namespace_is_found_by_its_name()
        {
            var lookup = new TypeLookup(Here);
            Assert.Equal(typeof(GlobalStandIn), lookup.Find("GlobalStandIn"));
        }

        [Fact]
        public void A_name_no_assembly_has_gives_nothing_and_throws_nothing()
        {
            var lookup = new TypeLookup(Here);
            Assert.Null(lookup.Find("Seasonality.SeasonalTimer"));
            Assert.Null(lookup.Find(""));
            Assert.Null(lookup.Find(null));
        }

        [Fact]
        public void A_name_found_once_is_answered_without_asking_any_assembly_again()
        {
            var lookup = new TypeLookup(Here);
            lookup.Find("Vigil.Core.StartupTimeline");
            int first = lookup.Lookups;
            for (int i = 0; i < 50; i++) Assert.NotNull(lookup.Find("Vigil.Core.StartupTimeline"));
            Assert.Equal(first, lookup.Lookups);
        }

        [Fact]
        public void A_name_not_found_asks_each_assembly_once_until_more_assemblies_load()
        {
            var assemblies = Here().ToList();
            var lookup = new TypeLookup(() => assemblies.ToArray());
            for (int i = 0; i < 50; i++) Assert.Null(lookup.Find("LaterStandIn.Thing"));
            Assert.Equal(assemblies.Count, lookup.Lookups);
            assemblies.Add(Compile("namespace LaterStandIn { public class Thing { } }", "Later" + Guid.NewGuid().ToString("N")));
            Assert.NotNull(lookup.Find("LaterStandIn.Thing"));
            Assert.Equal(assemblies.Count, lookup.Lookups);
        }

        [Fact]
        public void An_assembly_with_types_that_cannot_load_still_gives_the_ones_that_can()
        {
            // "Broken" builds on an assembly that is never loaded, as a mod's support for another mod that is not installed does.
            CompileBytes("namespace Absent { public class Gone { } }", "Absent" + Guid.NewGuid().ToString("N"), out var missingRef);
            var broken = Compile("namespace Broken { public class Fine { } public class NeedsAbsent : Absent.Gone { } }", "Broken" + Guid.NewGuid().ToString("N"), missingRef);
            var lookup = new TypeLookup(() => new[] { broken });
            Assert.NotNull(lookup.Find("Broken.Fine"));
            Assert.Null(lookup.Find("Broken.NeedsAbsent"));
        }

        private static Assembly Compile(string source, string name, MetadataReference extra = null) =>
            Assembly.Load(CompileBytes(source, name, out _, extra));

        private static byte[] CompileBytes(string source, string name, out MetadataReference reference, MetadataReference extra = null)
        {
            var refs = new[] { typeof(object).Assembly.Location }
                .Concat(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll"))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            if (extra != null) refs.Add(extra);
            var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            reference = compilation.ToMetadataReference();
            return ms.ToArray();
        }
    }
}

/// <summary>A type in no namespace, as most of the game's are.</summary>
internal sealed class GlobalStandIn
{
}
