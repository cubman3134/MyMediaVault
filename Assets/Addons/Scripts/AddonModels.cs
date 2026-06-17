using System;
using System.Collections.Generic;

namespace Goliath.Addons
{
    /// <summary>
    /// The JSON manifest that describes an addon (manifest.json at the root of an .addon package).
    /// Kept JsonUtility-friendly: public fields, no properties, no dictionaries.
    /// </summary>
    [Serializable]
    public class AddonManifest
    {
        public string id;            // unique, reverse-DNS recommended e.g. "com.you.my-source"
        public string name;          // display name
        public string version;       // semver, e.g. "1.0.0"
        public string type;          // addon type, e.g. "media-source"
        public string entry;         // script file name, defaults to "main.js"
        public string author;
        public string description;
        public string[] permissions; // declared capabilities, e.g. ["network"]
        public string minAppVersion;
    }

    /// <summary>A single piece of media a source can offer.</summary>
    [Serializable]
    public class MediaItem
    {
        public string id;
        public string title;
        public string subtitle;
        public string type;          // "ebook", "video", "audio", "link", ...
        public string thumbnailUrl;
        public string url;           // resolvable location: file path, http(s) url, magnet, ...
        public string mime;
    }

    /// <summary>A list of media items returned by a source.</summary>
    [Serializable]
    public class MediaCatalog
    {
        public string title;
        public List<MediaItem> items = new List<MediaItem>();
    }
}
