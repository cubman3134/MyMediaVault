// Real JavaScript engine, compiled only when Jint is installed.
//
// Setup:
//   1. Add Jint.dll (and its dependency, e.g. Esprima.dll for Jint v3) to Assets/Plugins/.
//      Get them from NuGet (https://www.nuget.org/packages/Jint) or NuGetForUnity.
//   2. Project Settings > Player > Scripting Define Symbols: add  ADDONS_JINT
//
// This file is written against Jint v3. If your Jint version differs, adjust the options/calls here.
#if ADDONS_JINT
using System;
using Jint;
using UnityEngine;

namespace Goliath.Addons
{
    public class JintScriptEngine : IScriptEngine
    {
        public object Load(string scriptSource, IAddonContext context)
        {
            try
            {
                var engine = new Engine(options =>
                {
                    options.LimitRecursion(100);
                    options.TimeoutInterval(TimeSpan.FromSeconds(5));
                    options.MaxStatements(2_000_000);
                });

                // Sandboxed host API exposed to the addon as global functions.
                engine.SetValue("log", new Action<string>(context.Log));
                engine.SetValue("httpGet", new Func<string, string>(context.HttpGet));
                engine.SetValue("getStorage", new Func<string, string>(context.GetStorage));
                engine.SetValue("setStorage", new Action<string, string>(context.SetStorage));

                engine.Execute(scriptSource);
                return engine;
            }
            catch (Exception e)
            {
                Debug.LogError($"Goliath.Addons: failed to load script: {e.Message}");
                return null;
            }
        }

        public bool HasFunction(object handle, string functionName)
        {
            var engine = handle as Engine;
            if (engine == null)
            {
                return false;
            }
            try
            {
                var value = engine.GetValue(functionName);
                return !value.IsUndefined() && !value.IsNull();
            }
            catch
            {
                return false;
            }
        }

        public string Invoke(object handle, string functionName, string jsonArgument)
        {
            var engine = handle as Engine;
            if (engine == null)
            {
                return "{}";
            }
            try
            {
                var result = engine.Invoke(functionName, jsonArgument ?? "{}");
                return (result == null || result.IsNull() || result.IsUndefined())
                    ? "{}"
                    : result.ToString();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Goliath.Addons: '{functionName}' threw: {e.Message}");
                return "{}";
            }
        }
    }
}
#endif
