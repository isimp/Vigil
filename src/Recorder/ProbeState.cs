namespace Vigil.Recorder
{
    /// <summary>
    /// Whether Vigil records right now (Recording > Mode: on, off, auto). Every hook that does
    /// work per call returns at once while it is false, so a switched-off Vigil costs no more
    /// than the empty calls into its hooks. It changes only in Vigil's own per-frame update,
    /// which never runs inside a timed call, so no timer is started on one side of a change and
    /// stopped on the other.
    /// </summary>
    internal static class ProbeState
    {
        public static bool Active = true;
    }
}
