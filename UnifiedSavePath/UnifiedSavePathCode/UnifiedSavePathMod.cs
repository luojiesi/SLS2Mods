using System.Reflection;
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
        var harmony = new Harmony("com.unifiedsavepath.sts2");

        // Patch targets differ by game version (0.107.1 lacks the last three), so each patch is optional

        // Patch the getter in case it's called directly
        TryPatch(harmony,
            () => AccessTools.PropertyGetter(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded)),
            nameof(GetIsRunningModded_Prefix), "IsRunningModded getter");

        // Patch the setter so it can never be set to true
        TryPatch(harmony,
            () => AccessTools.PropertySetter(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded)),
            nameof(SetIsRunningModded_Prefix), "IsRunningModded setter");

        // Patch single-arg GetProfileDir as a safety net against JIT inlining
        TryPatch(harmony,
            () => AccessTools.Method(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetProfileDir), new[] { typeof(int) }),
            nameof(GetProfileDir_Prefix), "GetProfileDir(int)");

        // Patch two-arg GetProfileDir (forceModState) — same bypass
        TryPatch(harmony,
            () => AccessTools.Method(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetProfileDir), new[] { typeof(int), typeof(bool?) }),
            nameof(GetProfileDir2_Prefix), "GetProfileDir(int, bool?)");

        // Patch GetAccountDir — always return empty string so "modded/" prefix is never injected
        TryPatch(harmony,
            () => AccessTools.Method(typeof(UserDataPathProvider), "GetAccountDir", new[] { typeof(bool?) }),
            nameof(GetAccountDir_Prefix), "GetAccountDir");

        // Skip CopyUnmoddedSaveFilesIfNeeded entirely — no need to create/modded folder
        TryPatch(harmony,
            () => AccessTools.Method(typeof(ModManager), "CopyUnmoddedSaveFilesIfNeeded"),
            nameof(CopyUnmoddedSaveFilesIfNeeded_Prefix), "CopyUnmoddedSaveFilesIfNeeded");

        // Also force the backing field to false in case it was already set
        UserDataPathProvider.IsRunningModded = false;
    }

    private static void TryPatch(Harmony harmony, Func<MethodBase?> resolve, string prefixName, string label)
    {
        try
        {
            var original = resolve();
            if (original == null)
            {
                Log.Info($"[UnifiedSavePath] {label} not present in this game version, skipping");
                return;
            }

            harmony.Patch(original, prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), prefixName));
        }
        catch (Exception e)
        {
            Log.Warn($"[UnifiedSavePath] Failed to patch {label}: {e}");
        }
    }

    private static bool GetIsRunningModded_Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool SetIsRunningModded_Prefix(ref bool value)
    {
        value = false;
        return true; // run original setter with value=false
    }

    private static bool GetProfileDir_Prefix(int profileId, ref string __result)
    {
        __result = $"profile{profileId}";
        return false;
    }

    private static bool GetProfileDir2_Prefix(int profileId, bool? forceModState, ref string __result)
    {
        // Ignore forceModState — always return root profile path
        __result = $"profile{profileId}";
        return false;
    }

    private static bool GetAccountDir_Prefix(bool? forceModState, ref string __result)
    {
        // Always return empty string so no "modded/" prefix is ever prepended
        __result = "";
        return false;
    }

    private static bool CopyUnmoddedSaveFilesIfNeeded_Prefix()
    {
        return false;
    }
}
