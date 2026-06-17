using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Built-in media source listing video files from &lt;persistentDataPath&gt;/Videos as "video" items,
    /// which route to <see cref="VideoPlayerView"/>.
    /// </summary>
    public class VideoLibrarySource : IMediaSource
    {
        public static readonly string[] Extensions =
        {
            ".mp4", ".m4v", ".mov", ".webm", ".ogv", ".avi", ".wmv", ".mkv", ".mpg", ".mpeg"
        };

        public string Id => "com.goliath.videos";
        public string DisplayName => "Videos";

        public static string VideosDir()
        {
            return Path.Combine(Application.persistentDataPath, "Videos");
        }

        public static bool IsVideo(string path)
        {
            return Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        public MediaCatalog GetCatalog()
        {
            var items = new List<MediaItem>();
            string dir = VideosDir();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        if (!IsVideo(f))
                        {
                            continue;
                        }
                        items.Add(new MediaItem
                        {
                            id = f,
                            title = Path.GetFileNameWithoutExtension(f),
                            subtitle = Path.GetExtension(f).TrimStart('.').ToUpperInvariant(),
                            type = "video",
                            url = f
                        });
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VideoLibrarySource: {e.Message}");
            }
            return new MediaCatalog { title = "Videos - drop files in " + dir, items = items };
        }

        public MediaCatalog Search(string query) => null;
    }
}
