namespace Vigil.Core
{
    /// <summary>
    /// The Startup settings in Vigil's config. The patcher reads them before any plugin loads and Vigil
    /// binds the same entries later, so both take their names and texts from here.
    /// </summary>
    internal static class StartupSettings
    {
        public const string ConfigFileName = "isimp.Vigil.cfg";
        public const string Section = "Startup";
        public const string Timeline = "Timeline", Cache = "BundleCache", CacheOnServer = "BundleCacheOnServer", KeepDays = "BundleCacheKeepDays";

        public const string TimelineText =
            "Times the game's start: each plugin, StartupAccelerator's patching, the menu, and the join to a world (Vigil-startup.txt, Health > Startup). " +
            "Read at the next start.";

        public const string CacheText =
            "On a player's game: the first time a mod loads an LZMA asset bundle (which Unity has to unpack whole on every load), Unity writes an LZ4 copy of it " +
            "into BepInEx/cache/Vigil/bundles; later starts load that copy, which reads only what is used. Read at the next start.";

        public const string CacheOnServerText = "The same bundle cache on a dedicated server. Read at the next start.";

        public const string KeepDaysText = "Days a fast copy may go unused before it is deleted at a start.";

        public const string FolderName = "Vigil", BundlesFolder = "bundles";
    }
}
