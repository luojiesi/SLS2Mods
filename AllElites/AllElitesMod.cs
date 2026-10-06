using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AllElites;

/// <summary>
/// Every normal fight becomes an elite fight: monster nodes on the map are
/// shown as elites, and any room that would be a normal monster room (map
/// node, or a "?" node that rolled a fight) is created as an elite room, so
/// it pulls from the act's elite pool and gives elite rewards.
/// </summary>
[ModInitializer("Initialize")]
public static class AllElitesMod
{
    public static void Initialize()
    {
        var harmony = new Harmony("com.allelites.sts2");
        harmony.PatchAll(typeof(AllElitesMod).Assembly);
        Log.Info("[AllElites] loaded");
    }
}

/// <summary>
/// Repaint the map: monster nodes become elite nodes. ModifyGeneratedMapLate
/// runs for both freshly generated and loaded maps, so a run started before
/// the mod was enabled gets the new icons too.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyGeneratedMapLate))]
public static class PatchMapIcons
{
    [HarmonyPostfix]
    public static void Postfix(ActMap __result)
    {
        foreach (var point in __result.GetAllMapPoints())
        {
            if (point.PointType == MapPointType.Monster)
                point.PointType = MapPointType.Elite;
        }
    }
}

/// <summary>
/// Turn any normal monster room into an elite room when it is created. This
/// catches "?" nodes that roll a fight and the act 1 first node the game
/// forces to Monster when Neow is skipped. Rooms created with an explicit
/// encounter (debug commands) are left alone.
/// </summary>
[HarmonyPatch(typeof(RunManager), "CreateRoom")]
public static class PatchCreateRoom
{
    [HarmonyPrefix]
    public static void Prefix(ref RoomType roomType, AbstractModel? model)
    {
        if (roomType == RoomType.Monster && model == null)
            roomType = RoomType.Elite;
    }
}
