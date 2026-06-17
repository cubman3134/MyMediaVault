using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Picks the script engine for the current build. The real (Jint) engine is only compiled when the
    /// ADDONS_JINT define is set (after adding Jint.dll to Assets/Plugins). Until then a null engine is
    /// used so the project still compiles and C#-backed addons keep working.
    /// </summary>
    public static class ScriptEngineFactory
    {
        public static IScriptEngine Create()
        {
#if ADDONS_JINT
            return new JintScriptEngine();
#else
            return new NullScriptEngine();
#endif
        }
    }

    /// <summary>Fallback used when no JavaScript engine is available. Script addons load to nothing.</summary>
    public class NullScriptEngine : IScriptEngine
    {
        private static bool _warned;

        public object Load(string scriptSource, IAddonContext context)
        {
            if (!_warned && !string.IsNullOrEmpty(scriptSource))
            {
                _warned = true;
                Debug.LogWarning("Goliath.Addons: no JavaScript engine. Add Jint.dll to Assets/Plugins " +
                                 "and set the ADDONS_JINT scripting define to run script addons.");
            }
            return null;
        }

        public bool HasFunction(object handle, string functionName) => false;

        public string Invoke(object handle, string functionName, string jsonArgument) => "{}";
    }
}
