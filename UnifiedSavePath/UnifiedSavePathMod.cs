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

        harmony.Patch(
            original: AccessTools.PropertyGetter(typeof(UserDataPathProvider), "IsRunningModded"),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetIsRunningModdedPrefix))
        );

        harmony.Patch(
            original: AccessTools.PropertySetter(typeof(UserDataPathProvider), "IsRunningModded"),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(SetIsRunningModdedPrefix))
        );

        harmony.Patch(
            original: AccessTools.Method(typeof(UserDataPathProvider), "GetProfileDir", [typeof(int)]),
            prefix: new HarmonyMethod(typeof(UnifiedSavePathMod), nameof(GetProfileDirPrefix))
        );

        // Also force the backing field to false in case it was already set
        UserDataPathProvider.IsRunningModded = false;
    }

    private static bool GetIsRunningModdedPrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool SetIsRunningModdedPrefix(ref bool value)
    {
        value = false;
        return true; // run original setter with value=false
    }

    private static bool GetProfileDirPrefix(int profileId, ref string __result)
    {
        __result = $"profile{profileId}";
        return false;
    }
}
