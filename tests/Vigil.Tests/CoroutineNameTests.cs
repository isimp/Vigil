using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Work that runs as a coroutine shows up under the method that started it, so a report can
    /// say "ConfigSync.sendZPackage" rather than a compiler-made class name.
    /// </summary>
    public sealed class CoroutineNameTests
    {
        [Fact]
        public void ACoroutineIsNamedByTheMethodThatWroteIt()
        {
            Assert.Equal("ConfigSync.sendZPackage", CoroutineName.Describe("<sendZPackage>d__41", "ConfigSync"));
        }

        [Fact]
        public void AHandWrittenEnumeratorKeepsItsOwnName()
        {
            Assert.Equal("Loader.Steps", CoroutineName.Describe("Steps", "Loader"));
            Assert.Equal("Steps", CoroutineName.Describe("Steps", null));
        }

        [Fact]
        public void AGenericCoroutineLosesTheCompilerSuffixToo()
        {
            Assert.Equal("Sync.Send", CoroutineName.Describe("<Send>d__7`1", "Sync"));
        }

        [Fact]
        public void ACompilerClassWithNoMethodNameIsNotEmptied()
        {
            Assert.Equal("Mod.<>c", CoroutineName.Describe("<>c", "Mod"));
        }

        [Fact]
        public void ANameWithoutTheMarkersIsLeftAlone()
        {
            Assert.Equal("Odd.<Broken", CoroutineName.Describe("<Broken", "Odd"));
        }
    }
}
