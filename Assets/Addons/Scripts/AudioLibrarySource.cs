using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Built-in media source listing audio files from &lt;persistentDataPath&gt;/Music as "audio" items,
    /// which route to <see cref="AudioPlayerView"/>.
    /// </summary>
    public class AudioLibrarySource : IMediaSource
    {
        public static readonly string[] Extensions = { ".ogg", ".wav", ".mp3", ".aiff", ".aif" };

        public string Id => "com.goliath.music";
        public string DisplayName => "Music";

        public static string MusicDir()
        {
            return Path.Combine(Application.persistentDataPath, "Music");
        }

        public static bool IsAudio(string path)
        {
            return Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        public MediaCatalog GetCatalog()
        {
            var items = new List<MediaItem>();
            string dir = MusicDir();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        if (!IsAudio(f))
                        {
                            continue;
                        }
                        items.Add(new MediaItem
                        {
                            id = f,
                            title = Path.GetFileNameWithoutExtension(f),
                            subtitle = Path.GetExtension(f).TrimStart('.').ToUpperInvariant(),
                            type = "audio",
                            url = f
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"AudioLibrarySource: {e.Message}");
            }
            return new MediaCatalog { title = "Music - drop files in " + dir, items = items };
        }

        public MediaCatalog Search(string query) => null;
    }
}
