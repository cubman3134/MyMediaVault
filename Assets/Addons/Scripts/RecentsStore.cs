using System;
using System.Collections.Generic;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Persisted list of recently opened media (any type), shown on the hub home. Players call
    /// <see cref="Add"/> when they open something; the hub reads <see cref="Recent"/>.
    /// </summary>
    public static class RecentsStore
    {
        private const string Key = "media.recent";

        [Serializable]
        private class Wrapper
        {
            public List<MediaItem> items = new List<MediaItem>();
        }

        public static List<MediaItem> Recent => Load().items;

        public static void Add(MediaItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.url))
            {
                return;
            }
            var wrapper = Load();
            wrapper.items.RemoveAll(x => x.url == item.url && x.type == item.type);
            wrapper.items.Insert(0, item);
            if (wrapper.items.Count > 50)
            {
                wrapper.items.RemoveRange(50, wrapper.items.Count - 50);
            }
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(wrapper));
            PlayerPrefs.Save();
        }

        private static Wrapper Load()
        {
            string json = PlayerPrefs.GetString(Key, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var wrapper = JsonUtility.FromJson<Wrapper>(json);
                    if (wrapper != null && wrapper.items != null)
                    {
                        return wrapper;
                    }
                }
                catch { /* fall through */ }
            }
            return new Wrapper();
        }
    }
}
