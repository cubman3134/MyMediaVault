using System.Collections.Generic;

namespace Goliath.Addons
{
    /// <summary>
    /// An example media source implemented directly in C# (compiled into the app). It demonstrates the
    /// "add code into the app" path and lets the Library -> Sources pipeline work even before the Jint
    /// JavaScript engine is installed. Real addons distributed to users are the script-backed kind.
    /// </summary>
    public class BuiltinSampleSource : IMediaSource
    {
        public string Id => "com.goliath.builtin-sample";
        public string DisplayName => "Sample (built-in)";

        public MediaCatalog GetCatalog()
        {
            return new MediaCatalog
            {
                title = "Sample (built-in)",
                items = new List<MediaItem>
                {
                    new MediaItem
                    {
                        id = "pride-and-prejudice",
                        title = "Pride and Prejudice",
                        subtitle = "Jane Austen",
                        type = "ebook",
                        url = "Assets/ebook/Books/austen-pride-and-prejudice-illustrations.epub"
                    }
                }
            };
        }

        public MediaCatalog Search(string query) => null;
    }
}
