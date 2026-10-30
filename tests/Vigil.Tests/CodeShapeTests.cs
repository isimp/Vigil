using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MetadataReader = System.Reflection.Metadata.MetadataReader;
using PEReader = System.Reflection.PortableExecutable.PEReader;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vigil.Recorder;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The player wants Vigil's patches on other mods to keep working through an update of that mod
    /// that leaves the code they rely on as it was, and to step aside when that code really changes.
    /// A mod that gains new parts elsewhere gets its references renumbered inside unchanged code, so
    /// a check over the raw bytes saw a change that was none.
    /// </summary>
    public sealed class CodeShapeTests
    {
        private sealed class Shape
        {
            public bool ExtraFirst;
            public string Callee = "Helper";
            public int Constant = 500;
            public string Text = "x";
            public bool BranchToEnd = true;
        }

        /// <summary>A mod with type T: optionally new parts placed first, two helpers and Subject, which calls one of them.</summary>
        private static byte[] Build(Shape s)
        {
            // New parts placed first renumber what Subject refers to, and their text moves Subject's text.
            string extra = s.ExtraFirst
                ? "public static int Added; public static string AddedText() { return \"a much longer text placed first\"; } public static void AddedStep() { Added++; }"
                : "";
            string branch = s.BranchToEnd ? "if (b) Seen = 0; else Seen = 1;" : "if (b) Seen = 0; Seen = 1;";
            string source = "namespace Ns { public static class U { public static void Helper() { } } public static class T { " + extra +
                " public static int Seen; public static string Last;" +
                " public static void Helper() { Seen++; } public static void Other() { Seen--; }" +
                " public static void Subject(bool b) { " + s.Callee + "(); Last = \"" + s.Text + "\"; Seen += " + s.Constant + "; " + branch + " } } }";
            var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }
                .Concat(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll").Select(p => MetadataReference.CreateFromFile(p)));
            var compilation = CSharpCompilation.Create("Shape" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
            using (var ms = new MemoryStream())
            {
                var result = compilation.Emit(ms);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                return ms.ToArray();
            }
        }

        private static ulong HashOf(byte[] image)
        {
            var module = ModuleDefinition.ReadModule(new MemoryStream(image));
            return CodeShape.Hash(new[] { module.GetType("Ns.T").Methods.Single(m => m.Name == "Subject") });
        }

        private static byte[] RawBytes(byte[] image)
        {
            using (var pe = new PEReader(new MemoryStream(image)))
            {
                MetadataReader md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                var handle = md.MethodDefinitions.Single(h => md.GetString(md.GetMethodDefinition(h).Name) == "Subject");
                return System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, md.GetMethodDefinition(handle).RelativeVirtualAddress).GetILBytes();
            }
        }

        [Fact]
        public void Code_that_only_had_its_references_renumbered_by_an_update_counts_as_unchanged()
        {
            byte[] before = Build(new Shape()), after = Build(new Shape { ExtraFirst = true });
            // The update renumbered what Subject refers to: its bytes differ.
            Assert.NotEqual(RawBytes(before), RawBytes(after));
            Assert.Equal(HashOf(before), HashOf(after));
        }

        [Fact]
        public void Calling_another_method_counts_as_changed()
        {
            Assert.NotEqual(HashOf(Build(new Shape())), HashOf(Build(new Shape { Callee = "Other" })));
        }

        [Fact]
        public void Calling_a_method_of_the_same_name_on_another_type_counts_as_changed()
        {
            Assert.NotEqual(HashOf(Build(new Shape())), HashOf(Build(new Shape { Callee = "U.Helper" })));
        }

        [Fact]
        public void Another_number_counts_as_changed()
        {
            Assert.NotEqual(HashOf(Build(new Shape())), HashOf(Build(new Shape { Constant = 600 })));
        }

        [Fact]
        public void Another_text_counts_as_changed()
        {
            Assert.NotEqual(HashOf(Build(new Shape())), HashOf(Build(new Shape { Text = "y" })));
        }

        [Fact]
        public void Another_jump_target_counts_as_changed()
        {
            Assert.NotEqual(HashOf(Build(new Shape())), HashOf(Build(new Shape { BranchToEnd = false })));
        }

        [Fact]
        public void The_methods_of_a_loaded_mod_are_read_from_its_file()
        {
            byte[] image = Build(new Shape { ExtraFirst = true });
            string path = Path.Combine(Path.GetTempPath(), "vigil-codeshape-" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(path, image);
            try
            {
                var loaded = Assembly.LoadFrom(path).GetType("Ns.T").GetMethod("Subject");
                Assert.Equal(HashOf(image), CodeShape.Hash(new MethodBase[] { loaded }));
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [Fact]
        public void A_method_without_a_file_cannot_be_checked()
        {
            var made = new System.Reflection.Emit.DynamicMethod("Made", typeof(void), Type.EmptyTypes);
            made.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
            Assert.ThrowsAny<InvalidOperationException>(() => CodeShape.Hash(new MethodBase[] { made }));
        }
    }
}
