using System;

namespace Vigil.Core
{
    /// <summary>
    /// What IMGUI's layout pass read, kept for the passes that follow it until the next layout pass.
    /// The panel lays a tab out in one pass and draws it in the next, and handles a click during the
    /// first: a badge the click makes appear or go would otherwise be drawn in a pass that never laid
    /// it out. A pass before any layout pass, or after one whose read failed, reads what is there.
    /// </summary>
    internal sealed class LaidOut<T>
    {
        private T _kept;
        private bool _has;

        public T Read(bool layoutPass, Func<T> read)
        {
            if (layoutPass || !_has)
            {
                _has = false;
                _kept = read();
                _has = true;
            }

            return _kept;
        }
    }
}
