using System;
using System.Collections.Generic;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Wraps a script-backed addon as an <see cref="IMediaSource"/>. It calls the addon's JavaScript
    /// functions (getCatalog / search) with a JSON argument and parses the JSON they return.
    ///
    /// Expected addon JS:
    ///   function getCatalog(argJson) { ...; return JSON.stringify({ title, items: [...] }); }
    ///   function search(argJson)     { var q = JSON.parse(argJson).query; ...; return JSON.stringify(...); }
    /// </summary>
    public class ScriptMediaSource : IMediaSource
    {
        private readonly IScriptEngine _engine;
        private readonly object _handle;
        private readonly AddonManifest _manifest;

        public ScriptMediaSource(IScriptEngine engine, object handle, AddonManifest manifest)
        {
            _engine = engine;
            _handle = handle;
            _manifest = manifest;
        }

        public string Id => _manifest.id;
        public string DisplayName => string.IsNullOrEmpty(_manifest.name) ? _manifest.id : _manifest.name;

        public MediaCatalog GetCatalog()
        {
            return Parse(_engine.Invoke(_handle, "getCatalog", "{}"));
        }

        public MediaCatalog Search(string query)
        {
            if (!_engine.HasFunction(_handle, "search"))
            {
                return null;
            }
            string arg = JsonUtility.ToJson(new SearchArg { query = query });
            return Parse(_engine.Invoke(_handle, "search", arg));
        }

        private static MediaCatalog Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new MediaCatalog { items = new List<MediaItem>() };
            }
            try
            {
                var catalog = JsonUtility.FromJson<MediaCatalog>(json);
                if (catalog == null)
                {
                    return new MediaCatalog { items = new List<MediaItem>() };
                }
                if (catalog.items == null)
                {
                    catalog.items = new List<MediaItem>();
                }
                return catalog;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Goliath.Addons: could not parse catalog JSON: {e.Message}");
                return new MediaCatalog { items = new List<MediaItem>() };
            }
        }

        [Serializable]
        private class SearchArg
        {
            public string query;
        }
    }
}
