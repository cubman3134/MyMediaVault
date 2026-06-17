using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// A built-in media source that lists ROM files dropped into the games folder
    /// (&lt;persistentDataPath&gt;/Libretro/roms, matching the SK.Libretro convention) as "rom" items.
    /// Tapping one routes to the libretro player (see <see cref="RetroPlayer"/>).
    /// </summary>
    public class RomLibrarySource : IMediaSource
    {
        public static readonly string[] Extensions =
        {
            ".nes", ".fds", ".sfc", ".smc", ".gb", ".gbc", ".gba",
            ".md", ".gen", ".smd", ".sms", ".gg", ".n64", ".z64", ".v64",
            ".pce", ".a26", ".ws", ".wsc", ".col", ".zip"
        };

        public string Id => "com.goliath.roms";
        public string DisplayName => "Games (ROMs)";

        public static string RomsDir()
        {
            return Path.Combine(Application.persistentDataPath, "Libretro", "roms");
        }

        public static bool IsRom(string path)
        {
            string ext = Path.GetExtension(path);
            return Array.IndexOf(Extensions, ext.ToLowerInvariant()) >= 0;
        }

        public MediaCatalog GetCatalog()
        {
            var items = new List<MediaItem>();
            string dir = RomsDir();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        if (!IsRom(f))
                        {
                            continue;
                        }
                        items.Add(new MediaItem
                        {
                            id = f,
                            title = Path.GetFileNameWithoutExtension(f),
                            subtitle = Path.GetExtension(f).TrimStart('.').ToUpperInvariant(),
                            type = "rom",
                            url = f
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"RomLibrarySource: {e.Message}");
            }
            return new MediaCatalog { title = "Games (ROMs) - drop files in " + dir, items = items };
        }

        public MediaCatalog Search(string query) => null;
    }
}
