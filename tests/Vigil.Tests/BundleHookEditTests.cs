using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Vigil.Startup;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The edit the patcher makes to Unity's bundle loading before any plugin starts: each of the four
    /// internal methods that load a bundle from memory or a stream first asks Vigil's hook, which either
    /// answers with the fast copy or lets the method go on as it was. A stand-in for Unity's assembly is
    /// compiled here, edited the same way, loaded and called, so the tests run the edited code itself.
    /// </summary>
    public sealed class BundleHookEditTests
    {
        private const string Source = @"
namespace UnityEngine
{
    public class AssetBundle
    {
        public string From;
        public AssetBundle(string from) { From = from; }
        internal static AssetBundle LoadFromMemory_Internal(byte[] binary, uint crc) { return new AssetBundle(""memory "" + binary.Length + "" "" + crc); }
        internal static AssetBundleCreateRequest LoadFromMemoryAsync_Internal(byte[] binary, uint crc) { return new AssetBundleCreateRequest(""memory async""); }
        internal static AssetBundle LoadFromStreamInternal(System.IO.Stream stream, uint crc, uint managedReadBufferSize) { return new AssetBundle(""stream "" + managedReadBufferSize); }
        internal static AssetBundleCreateRequest LoadFromStreamAsyncInternal(System.IO.Stream stream, uint crc, uint managedReadBufferSize) { return new AssetBundleCreateRequest(""stream async""); }
        internal static AssetBundle LoadFromFile_Internal(string path, uint crc, ulong offset) { return new AssetBundle(""file "" + path); }
        public static AssetBundle LoadFromMemory(byte[] binary) { return LoadFromMemory_Internal(binary, 0u); }
        public static AssetBundle LoadFromMemory(byte[] binary, uint crc) { return LoadFromMemory_Internal(binary, crc); }
        public static AssetBundleCreateRequest LoadFromMemoryAsync(byte[] binary) { return LoadFromMemoryAsync_Internal(binary, 0u); }
        public static AssetBundle LoadFromStream(System.IO.Stream stream) { return LoadFromStreamInternal(stream, 0u, 0u); }
        public static AssetBundle LoadFromStream(System.IO.Stream stream, uint crc, uint size) { return LoadFromStreamInternal(stream, crc, size); }
        public static AssetBundleCreateRequest LoadFromStreamAsync(System.IO.Stream stream) { return LoadFromStreamAsyncInternal(stream, 0u, 0u); }
    }

    public class AssetBundleCreateRequest
    {
        public string From;
        public AssetBundleCreateRequest(string from) { From = from; }
    }
}

namespace Vigil.Startup
{
    public static class BundleHook
    {
        public static bool Redirect;
        public static int Asked;
        public static bool TryMemory(byte[] binary, uint crc, out UnityEngine.AssetBundle result) { Asked++; result = Redirect ? new UnityEngine.AssetBundle(""copy"") : null; return Redirect; }
        public static bool TryMemoryAsync(byte[] binary, uint crc, out UnityEngine.AssetBundleCreateRequest result) { Asked++; result = Redirect ? new UnityEngine.AssetBundleCreateRequest(""copy async"") : null; return Redirect; }
        public static bool TryStream(System.IO.Stream stream, uint crc, uint managedReadBufferSize, out UnityEngine.AssetBundle result) { Asked++; result = Redirect ? new UnityEngine.AssetBundle(""copy"") : null; return Redirect; }
        public static bool TryStreamAsync(System.IO.Stream stream, uint crc, uint managedReadBufferSize, out UnityEngine.AssetBundleCreateRequest result) { Asked++; result = Redirect ? new UnityEngine.AssetBundleCreateRequest(""copy async"") : null; return Redirect; }
    }
}";

        private static byte[] Compile(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var refs = new[] { typeof(object), typeof(Stream) }.Select(t => t.Assembly.Location).Distinct()
                .Concat(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "System.Runtime.dll"))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("StandIn" + Guid.NewGuid().ToString("N"), new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            return ms.ToArray();
        }

        /// <summary>Edits the stand-in with its own hook type and loads the result.</summary>
        private static (Assembly assembly, string problem) Edited(string source)
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(Compile(source)));
            string problem = BundleHookEdit.Apply(module, module.GetType("Vigil.Startup", "BundleHook"));
            using var output = new MemoryStream();
            module.Write(output);
            return (Assembly.Load(output.ToArray()), problem);
        }

        private static string Call(Assembly a, string method, params object[] args)
        {
            var t = a.GetType("UnityEngine.AssetBundle");
            var m = t.GetMethods().Single(x => x.Name == method && x.GetParameters().Length == args.Length);
            object r = m.Invoke(null, args);
            return (string)r.GetType().GetField("From").GetValue(r);
        }

        private static void Hook(Assembly a, bool redirect) => a.GetType("Vigil.Startup.BundleHook").GetField("Redirect").SetValue(null, redirect);

        private static int Asked(Assembly a) => (int)a.GetType("Vigil.Startup.BundleHook").GetField("Asked").GetValue(null);

        [Fact]
        public void A_load_from_memory_goes_on_as_it_was_when_the_hook_has_no_copy()
        {
            var (a, problem) = Edited(Source);
            Assert.Null(problem);
            Assert.Equal("memory 3 7", Call(a, "LoadFromMemory", new byte[3], 7u));
            Assert.Equal(1, Asked(a));
        }

        [Fact]
        public void A_load_from_memory_returns_the_hooks_copy_when_it_has_one()
        {
            var (a, _) = Edited(Source);
            Hook(a, true);
            Assert.Equal("copy", Call(a, "LoadFromMemory", new byte[3]));
        }

        [Fact]
        public void A_load_from_a_stream_asks_the_hook_and_otherwise_goes_on_with_its_own_arguments()
        {
            var (a, _) = Edited(Source);
            Assert.Equal("stream 42", Call(a, "LoadFromStream", new MemoryStream(), 0u, 42u));
            Hook(a, true);
            Assert.Equal("copy", Call(a, "LoadFromStream", new MemoryStream()));
            Assert.Equal(2, Asked(a));
        }

        [Fact]
        public void The_waiting_loads_ask_the_hook_too()
        {
            var (a, _) = Edited(Source);
            Assert.Equal("memory async", Call(a, "LoadFromMemoryAsync", new byte[1]));
            Assert.Equal("stream async", Call(a, "LoadFromStreamAsync", new MemoryStream()));
            Hook(a, true);
            Assert.Equal("copy async", Call(a, "LoadFromMemoryAsync", new byte[1]));
            Assert.Equal("copy async", Call(a, "LoadFromStreamAsync", new MemoryStream()));
        }

        [Fact]
        public void Loads_from_a_file_are_not_touched()
        {
            var (a, _) = Edited(Source);
            Hook(a, true);
            var t = a.GetType("UnityEngine.AssetBundle");
            var m = t.GetMethod("LoadFromFile_Internal", BindingFlags.Static | BindingFlags.NonPublic);
            object r = m.Invoke(null, new object[] { "x.bundle", 0u, 0ul });
            Assert.Equal("file x.bundle", (string)r.GetType().GetField("From").GetValue(r));
            Assert.Equal(0, Asked(a));
        }

        [Fact]
        public void A_Unity_without_one_of_the_methods_is_left_as_it_was_and_the_reason_is_given()
        {
            string changed = Source.Replace("LoadFromStreamInternal(System.IO.Stream stream, uint crc, uint managedReadBufferSize) { return", "LoadFromStreamInternal(System.IO.Stream stream, uint crc) { return")
                .Replace("return LoadFromStreamInternal(stream, 0u, 0u);", "return LoadFromStreamInternal(stream, 0u);")
                .Replace("return LoadFromStreamInternal(stream, crc, size);", "return LoadFromStreamInternal(stream, crc);")
                .Replace("\"stream \" + managedReadBufferSize", "\"stream\"");
            var (a, problem) = Edited(changed);
            Assert.NotNull(problem);
            Assert.Contains("LoadFromStreamInternal", problem);
            Hook(a, true);
            Assert.Equal("memory 3 0", Call(a, "LoadFromMemory", new byte[3]));
            Assert.Equal(0, Asked(a));
        }

        [Fact]
        public void The_game_s_Unity_has_all_four_methods_the_edit_needs()
        {
            string lib = Path.Combine(Path.GetDirectoryName(typeof(BundleHookEditTests).Assembly.Location), "..", "..", "..", "..", "..", "lib", "UnityEngine.AssetBundleModule.dll");
            using var module = ModuleDefinition.ReadModule(Path.GetFullPath(lib));
            Assert.Null(BundleHookEdit.FindProblem(module));
        }

        [Fact]
        public void The_edit_refers_to_the_hook_in_Vigils_patcher_by_assembly_name()
        {
            using var module = ModuleDefinition.ReadModule(new MemoryStream(Compile(Source)));
            var hook = BundleHookEdit.HookType(module, "Vigil.Preloader", new Version(0, 39, 0, 0));
            Assert.Equal("Vigil.Startup.BundleHook", hook.FullName);
            Assert.Equal("Vigil.Preloader", ((AssemblyNameReference)hook.Scope).Name);
            Assert.Contains(module.AssemblyReferences, r => r.Name == "Vigil.Preloader");
        }
    }
}
