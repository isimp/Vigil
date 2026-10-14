using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A log flooded with one message ("Missing prefab hash: 439296140" many times a second) costs
    /// frame time. Messages that differ only in numbers or ids count as the same message, so the
    /// flood shows up as one line with a big count instead of thousands of different lines.
    /// </summary>
    public sealed class LogTextTests
    {
        [Fact]
        public void MessagesDifferingOnlyInNumbersCountAsOne()
        {
            Assert.Equal(LogText.Normalize("Missing prefab hash: 439296140"), LogText.Normalize("Missing prefab hash: -12"));
        }

        [Fact]
        public void TimestampsDoNotSplitTheSameMessage()
        {
            Assert.Equal(
                LogText.Normalize("10/08/2026 17:38:13: Missing prefab hash: 439296140"),
                LogText.Normalize("10/08/2026 17:39:59: Missing prefab hash: 1"));
        }

        [Fact]
        public void DifferentMessagesStayDifferent()
        {
            Assert.NotEqual(LogText.Normalize("Added ore Wood"), LogText.Normalize("Added ore RoundLog"));
        }

        [Fact]
        public void OnlyTheFirstLineOfALongMessageIsKeptShort()
        {
            string n = LogText.Normalize("Exception: boom\n  at A.B()\n  at C.D()" + new string('x', 500));

            Assert.DoesNotContain("\n", n);
            Assert.True(n.Length <= 160);
            Assert.StartsWith("Exception: boom", n);
        }

        [Fact]
        public void AVeryLongSingleLineIsCutShort()
        {
            string n = LogText.Normalize("Huge message " + new string('y', 1000));

            Assert.Equal(160, n.Length);
            Assert.StartsWith("Huge message yyy", n);
        }

        [Fact]
        public void NothingToReadBecomesAnEmptyMessage()
        {
            Assert.Equal("", LogText.Normalize(null));
            Assert.Equal("", LogText.Normalize("   "));
        }
    }
}
