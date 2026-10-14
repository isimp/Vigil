using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Removing objects from the world cannot be undone, so the panel's remove button asks twice:
    /// the first press only arms it and says what a second press will do, a second press within a
    /// few seconds does it, and a press after that, or after the list changed, starts over.
    /// </summary>
    public sealed class ConfirmTests
    {
        [Fact]
        public void TheFirstPressOnlyArms()
        {
            var c = new Confirm(seconds: 5);
            Assert.False(c.Press("12 objects", 0));
            Assert.True(c.Armed("12 objects", 1));
        }

        [Fact]
        public void ASecondPressSoonAfterDoesIt()
        {
            var c = new Confirm(5);
            c.Press("12 objects", 0);
            Assert.True(c.Press("12 objects", 4));
            Assert.False(c.Armed("12 objects", 4.1));
        }

        [Fact]
        public void ASecondPressTooLateStartsOver()
        {
            var c = new Confirm(5);
            c.Press("12 objects", 0);
            Assert.False(c.Armed("12 objects", 5.1));
            Assert.False(c.Press("12 objects", 6));
            Assert.True(c.Press("12 objects", 7));
        }

        [Fact]
        public void AChangedListStartsOver()
        {
            var c = new Confirm(5);
            c.Press("12 objects", 0);
            Assert.False(c.Press("13 objects", 1));
            Assert.True(c.Armed("13 objects", 1));
            Assert.False(c.Armed("12 objects", 1));
        }

        [Fact]
        public void LettingGoDisarms()
        {
            var c = new Confirm(5);
            c.Press("12 objects", 0);
            c.Disarm();
            Assert.False(c.Press("12 objects", 1));
        }
    }
}
