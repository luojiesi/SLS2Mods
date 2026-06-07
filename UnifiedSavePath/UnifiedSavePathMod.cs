using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace UnifiedSavePath;

[ModInitializer("Initialize")]
public static class UnifiedSavePathMod
{
    public static void Initialize()
    {
        // On Linux, Harmony installs its detours through MonoMod's native helper
        // (mm-exhelper.so, written to /tmp at runtime). That helper needs the
        // stack-unwinding routines from libgcc_s (e.g. _Unwind_RaiseException).
        // The Godot/.NET host does not load libgcc_s with global symbol
        // visibility, so the helper fails to resolve the symbol and Harmony's
        // PatchAll() throws:
        //   DllNotFoundException: ... undefined symbol: _Unwind_RaiseException
        // Loading libgcc_s with RTLD_GLOBAL before patching makes the symbol
        // available to the helper. This is a no-op on Windows and macOS.
        EnsureUnwindSymbolsLoaded();

        var harmony = new Harmony("com.unifiedsavepath.sts2");
        harmony.PatchAll(typeof(UnifiedSavePathMod).Assembly);

        // Also force the backing field to false in case it was already set
        UserDataPathProvider.IsRunningModded = false;
    }

    /// <summary>
    /// Ensures libgcc_s (which provides the C++ stack-unwinding symbols MonoMod's
    /// native detour helper depends on) is loaded with global symbol visibility.
    /// Only does anything on Linux; harmless elsewhere.
    /// </summary>
    private static void EnsureUnwindSymbolsLoaded()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        const int RTLD_NOW = 2;
        const int RTLD_GLOBAL = 0x100;

        foreach (var soname in new[] { "libgcc_s.so.1", "libgcc_s.so" })
        {
            try
            {
                if (LinuxNative.dlopen(soname, RTLD_NOW | RTLD_GLOBAL) != IntPtr.Zero)
                {
                    Log.Info($"[UnifiedSavePath] Preloaded {soname} for Harmony/MonoMod native detours.");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[UnifiedSavePath] dlopen({soname}) failed: {e.Message}");
            }
        }

        Log.Warn("[UnifiedSavePath] Could not preload libgcc_s.so.1. If Harmony patching now "
                 + "fails on Linux, add 'LD_PRELOAD=libgcc_s.so.1' (or pass "
                 + "'env LD_PRELOAD=libgcc_s.so.1 %command%') in the game's Steam launch options.");
    }

    private static class LinuxNative
    {
        // libdl.so.2 still exports dlopen on modern glibc (2.34+ keeps it as a
        // compatibility stub) and on every distro the game ships for.
        [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
        internal static extern IntPtr dlopen(string filename, int flags);
    }
}

// Patch the getter in case it's called directly
[HarmonyPatch(typeof(UserDataPathProvider), "get_IsRunningModded")]
public static class PatchGetIsRunningModded
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}

// Patch the setter so it can never be set to true
[HarmonyPatch(typeof(UserDataPathProvider), "set_IsRunningModded")]
public static class PatchSetIsRunningModded
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool value)
    {
        value = false;
        return true; // run original setter with value=false
    }
}

// Patch GetProfileDir directly as a safety net against JIT inlining
[HarmonyPatch(typeof(UserDataPathProvider), "GetProfileDir")]
public static class PatchGetProfileDir
{
    [HarmonyPrefix]
    public static bool Prefix(int profileId, ref string __result)
    {
        __result = $"profile{profileId}";
        return false;
    }
}
