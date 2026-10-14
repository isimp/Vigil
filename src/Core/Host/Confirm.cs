namespace Vigil.Core
{
    /// <summary>
    /// A button that asks twice before something that cannot be undone: the first press arms it for
    /// what it is about ("12 objects"), a second press within the time does it, and a press later,
    /// or about something else, arms it again instead.
    /// </summary>
    internal sealed class Confirm
    {
        private readonly double _seconds;
        private string _what;
        private double _armedAt;

        public Confirm(double seconds) => _seconds = seconds;

        /// <summary>True when this press confirms; false when it only armed the button.</summary>
        public bool Press(string what, double now)
        {
            if (Armed(what, now))
            {
                Disarm();
                return true;
            }

            _what = what;
            _armedAt = now;
            return false;
        }

        /// <summary>Whether a press now would confirm, for the button to say so.</summary>
        public bool Armed(string what, double now) => _what != null && _what == what && now - _armedAt <= _seconds;

        public void Disarm() => _what = null;
    }
}
