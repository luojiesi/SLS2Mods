using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace UnifiedSavePath;

[ModInitializer("Initialize")]
public static class UnifiedSavePathMod
{
    public static void Initialize()
    {
        var harmony = new Harmony("com.unifiedsavepath.sts2");

        // Patch the getter in case it's called directly
        harmony.Patch(
            original: AccessTools.PropertyGetter(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded)),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetIsRunningModded_Prefix))
        );

        // Patch the setter so it can never be set to true
        harmony.Patch(
            original: AccessTools.PropertySetter(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded)),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(SetIsRunningModded_Prefix))
        );

        // Patch single-arg GetProfileDir as a safety net against JIT inlining
        harmony.Patch(
            original: AccessTools.Method(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetProfileDir), new[] { typeof(int) }),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetProfileDir_Prefix))
        );

        // Patch two-arg GetProfileDir (forceModState) — same bypass
        harmony.Patch(
            original: AccessTools.Method(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetProfileDir), new[] { typeof(int), typeof(bool?) }),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetProfileDir2_Prefix))
        );

        // Patch GetAccountDir — always return empty string so "modded/" prefix is never injected
        harmony.Patch(
            original: AccessTools.Method(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetAccountDir), new[] { typeof(bool?) }),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetAccountDir_Prefix))
        );

        // Skip CopyUnmoddedSaveFilesIfNeeded entirely — no need to create/modded folder
        harmony.Patch(
            original: AccessTools.Method(typeof(ModManager), nameof(ModManager.CopyUnmoddedSaveFilesIfNeeded)),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(CopyUnmoddedSaveFilesIfNeeded_Prefix))
        );

        // Also force the backing field to false in case it was already set
        UserDataPathProvider.IsRunningModded = false;
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

    private static void GetAccountDir_Prefix(bool? forceModState, ref string __result)
    {
        // Always return empty string so no "modded/" prefix is ever prepended
        __result = "";
    }

    private static bool CopyUnmoddedSaveFilesIfNeeded_Prefix()
    {
        return false;
    }
}
