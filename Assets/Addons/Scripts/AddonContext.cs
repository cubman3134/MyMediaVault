using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Concrete host API for one addon. Every capability is permission-gated and addon-scoped, so an
    /// addon can only touch its own storage and only reach the network if it declared "network".
    /// </summary>
    public class AddonContext : IAddonContext
    {
        private readonly string _addonId;
        private readonly HashSet<string> _permissions;
        private readonly string _storageDir;

        public AddonContext(AddonManifest manifest, string storageDir)
        {
            _addonId = manifest != null ? manifest.id : "unknown";
            _permissions = new HashSet<string>(manifest != null && manifest.permissions != null
                ? manifest.permissions
                : new string[0]);
            _storageDir = storageDir;
            try { Directory.CreateDirectory(_storageDir); } catch { /* best effort */ }
        }

        public void Log(string message)
        {
            Debug.Log($"[addon:{_addonId}] {message}");
        }

        public string HttpGet(string url)
        {
            if (!_permissions.Contains("network"))
            {
                Debug.LogWarning($"[addon:{_addonId}] denied httpGet (missing \"network\" permission)");
                return string.Empty;
            }
            try
            {
                // Synchronous for now (Jint is synchronous). Keep addon network calls light; a future
                // version can move script execution to a worker thread or expose async/promises.
                using (var client = new System.Net.WebClient())
                {
                    return client.DownloadString(url);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[addon:{_addonId}] httpGet failed: {e.Message}");
                return string.Empty;
            }
        }

        public string GetStorage(string key)
        {
            try
            {
                var path = Path.Combine(_storageDir, Sanitize(key) + ".txt");
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch { return string.Empty; }
        }

        public void SetStorage(string key, string value)
        {
            try
            {
                File.WriteAllText(Path.Combine(_storageDir, Sanitize(key) + ".txt"), value ?? string.Empty);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[addon:{_addonId}] setStorage failed: {e.Message}");
            }
        }

        private static string Sanitize(string key)
        {
            return Regex.Replace(key ?? "key", "[^a-zA-Z0-9_-]", "_");
        }
    }
}
