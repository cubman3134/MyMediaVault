using System;
using System.Collections.Generic;

namespace Goliath.Addons
{
    /// <summary>
    /// Routes a <see cref="MediaItem"/> to whatever component can play/open its <c>type</c>. Players
    /// register themselves (e.g. the ebook reader registers "ebook"); the hub just calls Open(). This
    /// is what makes the Library a real media hub: adding a video/audio/game player is one Register
    /// call and needs no changes here or in the hub UI.
    /// </summary>
    public static class MediaRouter
    {
        private static readonly Dictionary<string, Action<MediaItem>> Players =
            new Dictionary<string, Action<MediaItem>>();

        /// <summary>Registers (or replaces) the opener for a media type, e.g. "ebook", "video".</summary>
        public static void Register(string mediaType, Action<MediaItem> opener)
        {
            if (!string.IsNullOrEmpty(mediaType) && opener != null)
            {
                Players[mediaType] = opener;
            }
        }

        public static bool CanOpen(string mediaType)
        {
            return mediaType != null && Players.ContainsKey(mediaType);
        }

        /// <summary>Opens the item with its registered player. Returns false if no player handles its type.</summary>
        public static bool Open(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.type))
            {
                return false;
            }
            if (Players.TryGetValue(item.type, out var opener))
            {
                opener(item);
                return true;
            }
            return false;
        }
    }
}
