# Vigil

Vigil is a private tool for my group's Valheim server. It is on Hexium only so that our mod manager can install it for the group's players. It is not meant for anyone else's use, it is not maintained for others, no support or requests are taken, and it may change or disappear without notice.

What it does, briefly: it records frame times, network numbers and what each mod costs, so that lag and dropped connections can be explained afterwards; it gives the server's admins a panel (Shift+F9) to look at the server, its players' connections and the world; it marks where creatures can still spawn around a base; and it times the game's start and keeps fast-loading copies of the asset bundles mods pack with LZMA. Most of it needs a copy on the dedicated server as well.

## AI notice

Most of Vigil was written by Claude Code (Anthropic), which did the heavy lifting on implementation and design. Heads-up so you can judge for yourself.

## More

How each part works is in the [guide](https://github.com/isimp/Vigil/blob/main/docs/guide.md), and the files, columns and inner workings are in the [technical notes](https://github.com/isimp/Vigil/blob/main/docs/technical.md). Building and releasing are in [releasing](https://github.com/isimp/Vigil/blob/main/docs/releasing.md).
