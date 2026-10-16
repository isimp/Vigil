using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// A report, an alert's file or the recordings folder is handed over as its path on the clipboard:
    /// the button says so, and reads "Copied" for a moment after a press, on that button only.
    /// </summary>
    public sealed class PathCopyTests
    {
        private const string Report = @"C:\Users\x\AppData\LocalLow\IronGate\Valheim\Vigil\report.txt";

        [Fact]
        public void A_button_offers_to_copy_its_path_until_pressed()
        {
            Assert.Equal("Copy path", PathCopy.Label(null, 0f, Report, 10f));
            Assert.Equal("Copy the recordings folder's path", PathCopy.Label(null, 0f, Report, 10f, "Copy the recordings folder's path"));
        }

        [Fact]
        public void The_pressed_button_reads_copied_for_two_seconds()
        {
            Assert.Equal("Copied", PathCopy.Label(Report, 10f, Report, 10f));
            Assert.Equal("Copied", PathCopy.Label(Report, 10f, Report, 11.9f));
            Assert.Equal("Copy path", PathCopy.Label(Report, 10f, Report, 12f));
            Assert.Equal("Copy path", PathCopy.Label(Report, 10f, Report, 9f));
        }

        [Fact]
        public void Only_the_button_whose_path_was_copied_reads_copied()
        {
            Assert.Equal("Copy path", PathCopy.Label(Report, 10f, Report + ".old", 10.5f));
        }

        [Theory]
        [InlineData("Copy path", "copy")]
        [InlineData("Copied", "check")]
        public void The_copy_words_have_icons(string label, string icon)
        {
            Assert.Equal(icon, ButtonIcon.For(label));
        }
    }
}
