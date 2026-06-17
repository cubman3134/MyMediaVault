namespace Goliath.Addons
{
    /// <summary>
    /// A pure-C# (AOT/IL2CPP-safe) script engine. Implementations interpret addon scripts; nothing is
    /// JIT-compiled or dynamically linked, so the same code runs on iOS / Android / TV / desktop.
    /// The contract is deliberately JSON-in / JSON-out so no complex objects cross the boundary.
    /// </summary>
    public interface IScriptEngine
    {
        /// <summary>Loads an addon's script and returns an opaque per-addon handle (null on failure).</summary>
        object Load(string scriptSource, IAddonContext context);

        /// <summary>True if the loaded addon defines a callable function with this name.</summary>
        bool HasFunction(object handle, string functionName);

        /// <summary>Calls a function with a JSON string argument and returns its JSON string result.</summary>
        string Invoke(object handle, string functionName, string jsonArgument);
    }

    /// <summary>
    /// The sandboxed host API handed to each addon. Capabilities are gated by the addon's declared
    /// permissions in its manifest. Exposed to scripts as global functions (log, httpGet, ...).
    /// </summary>
    public interface IAddonContext
    {
        void Log(string message);
        string HttpGet(string url);                 // requires "network" permission
        string GetStorage(string key);              // addon-scoped key/value storage
        void SetStorage(string key, string value);
    }

    /// <summary>A browsable media provider surfaced in the Library. May be script- or C#-backed.</summary>
    public interface IMediaSource
    {
        string Id { get; }
        string DisplayName { get; }
        MediaCatalog GetCatalog();
        MediaCatalog Search(string query);          // may return null if unsupported
    }
}
