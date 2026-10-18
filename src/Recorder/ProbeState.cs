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

        /// <summary>
        /// A player is joining this server: from their connection until the game takes them in
        /// (unless recording is switched off). The message-handler timer runs then too, so the
        /// first join on an empty server is timed, which recording, switched on at the next frame,
        /// would miss. It is set inside the game's connection call; that timer keeps its start per
        /// message, so a change between its two halves only skips that one message.
        /// </summary>
        public static bool Joining;
    }
}
