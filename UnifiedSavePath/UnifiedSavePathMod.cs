using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Modding;

namespace UnifiedSavePath;

[ModInitializer("Initialize")]
public static class UnifiedSavePathMod
{
    public static void Initialize()
    {
        var harmony = new Harmony("com.unifiedsavepath.sts2");

        // Patch the getter in case it's called directly
        harmony.Patch(
            original: AccessTools.PropertyGetter(typeof(UserDataPathProvider), "IsRunningModded"),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetIsRunningModded_Prefix))
        );

        // Patch the setter so it can never be set to true
        harmony.Patch(
            original: AccessTools.PropertySetter(typeof(UserDataPathProvider), "IsRunningModded"),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(SetIsRunningModded_Prefix))
        );

        // Patch GetProfileDir directly as a safety net against JIT inlining
        harmony.Patch(
            original: AccessTools.Method(typeof(UserDataPathProvider), "GetProfileDir", new[] { typeof(int) }),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetProfileDir_Prefix))
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
}
