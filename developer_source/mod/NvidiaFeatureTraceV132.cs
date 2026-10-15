using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;

namespace SonsVRMod
{
    /// <summary>
    /// Passive instrumentation of NVIDIA's UNITY wrapper entry points. A Harmony hook
    /// observes C# calls through GraphicsDevice; absence of hooks/calls is NOT proof
    /// that no native DLSS feature executes (HDRP/native paths can bypass wrappers).
    /// Never creates/destroys/evaluates a feature itself.
    /// </summary>
    internal static class NvidiaFeatureTraceV132
    {
        private static bool _initialized;
        private static HarmonyLib.Harmony _harmony;
        private static long _created, _evaluated, _destroyed;
        private static int _patchCount;
        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("zer0int.sonsvr.v132.passive-nvidia-feature-trace");
                Type graphics = Find("UnityEngine.NVIDIA.GraphicsDevice");
                Type dlss = Find("UnityEngine.Rendering.HighDefinition.DLSSPass");
                DumpSurface(graphics, "NVIDIA_GRAPHICS_DEVICE");
                DumpSurface(dlss, "HDRP_DLSS_PASS");
                if (graphics == null) return;
                foreach (MethodInfo method in graphics.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                                 BindingFlags.Static | BindingFlags.Instance))
                {
                    string prefix = method.Name == "CreateFeature" ? nameof(Created) :
                        method.Name == "ExecuteDLSS" ? nameof(Evaluated) :
                        method.Name == "DestroyFeature" ? nameof(Destroyed) : null;
                    if (prefix == null || method.IsAbstract || method.ContainsGenericParameters) continue;
                    try
                    {
                        MethodInfo cb = typeof(NvidiaFeatureTraceV132).GetMethod(prefix,
                            BindingFlags.Static | BindingFlags.NonPublic);
                        _harmony.Patch(method, prefix: new HarmonyMethod(cb));
                        _patchCount++;
                        Log("HOOK_OK " + method.Name + "(" + method.GetParameters().Length + " args)");
                    }
                    catch (Exception e) { Log("HOOK_FAILED " + method.Name + " " + e.GetType().Name + ":" + e.Message); }
                }
                Log("HOOK_SUMMARY=" + _patchCount + " (no hooks does NOT establish absence of DLSS)");
            }
            catch (Exception e) { Log("INIT_FAILED " + e.GetType().Name + ":" + e.Message); }
        }
        private static void Created() { Interlocked.Increment(ref _created); }
        private static void Evaluated() { Interlocked.Increment(ref _evaluated); }
        private static void Destroyed() { Interlocked.Increment(ref _destroyed); }
        internal static void Report(string stage)
        {
            Log("COUNTS " + stage + " hooks=" + _patchCount +
                " CreateFeature=" + Interlocked.Read(ref _created) +
                " ExecuteDLSS=" + Interlocked.Read(ref _evaluated) +
                " DestroyFeature=" + Interlocked.Read(ref _destroyed) +
                " NOTE: counts reflect hooked wrapper calls only; not an NGX device trace");
        }
        private static Type Find(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type t = a.GetType(name, false); if (t != null) return t; }
                catch { }
            }
            return null;
        }
        private static void DumpSurface(Type type, string label)
        {
            if (type == null) { Log(label + " NOT_FOUND"); return; }
            Log(label + " type=" + type.FullName + " assembly=" + type.Assembly.GetName().Name);
            int n = 0;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                string name = m.Name.ToLowerInvariant();
                if (name.Contains("feature") || name.Contains("dlss") || name.Contains("execute") ||
                    name.Contains("optimal") || name.Contains("render"))
                {
                    if (n++ < 45) Log(label + " METHOD " + m);
                }
            }
            n = 0;
            foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                string name = f.Name.ToLowerInvariant();
                if ((name.Contains("dlss") || name.Contains("feature") || name.Contains("context") || name.Contains("quality")) && n++ < 25)
                    Log(label + " FIELD " + f.FieldType.Name + " " + f.Name);
            }
        }
        private static void Log(string s) { MelonLogger.Warning("[V132 NVIDIA-FEATURE] " + s); }
    }
}
