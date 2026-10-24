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

        public const string Hold = "HoldConfigSaves", HoldOnServer = "HoldConfigSavesOnServer";

        public const string HoldText =
            "On a player's game: while the game starts, a BepInEx config save only marks its file, and each file is written once when the menu shows " +
            "(BepInEx rewrites a mod's whole settings file each time the mod binds a setting). A file another mod writes itself stays as it wrote it. Read at the next start.";

        public const string HoldOnServerText = "The same on a dedicated server, until its world is up. Read at the next start.";

        public const string Lookups = "RememberFailedLookups", LookupsOnServer = "RememberFailedLookupsOnServer";

        public const string LookupsText =
            "On a player's game: while the game starts, a search for an assembly that is not installed (a mod checking for an optional mod) is not run again " +
            "once it found nothing: BepInEx searches every folder under the plugins folder for it on every ask. From the menu on, every search runs. Read at the next start.";

        public const string LookupsOnServerText = "The same on a dedicated server, until its world is up. Read at the next start.";

        public const string FolderName = "Vigil", BundlesFolder = "bundles";
    }
}
