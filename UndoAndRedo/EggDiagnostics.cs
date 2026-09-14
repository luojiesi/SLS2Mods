using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo;

/// <summary>
/// One-off diagnostic (logs only): why do the egg relics not show up in the Loadout mod's relic screen when
/// UpgradeAllCards is active? Dumps what the game and Loadout know about the eggs once per run.
/// </summary>
internal static class EggDiagnostics
{
    private static bool _done;

    public static void RunOnce()
    {
        if (_done) return;
        _done = true;
        try
        {
            var eggs = ModelDb.AllRelics.Where(r => r.Id.Entry.Contains("EGG")).ToList();
            Log.Write($"EGGDIAG: ModelDb.AllRelics={ModelDb.AllRelics.Count()} eggs=[{string.Join(", ", eggs.Select(e => $"{e.Id.Entry} rarity={e.Rarity} mutable={AccessTools.Property(typeof(AbstractModel), "IsMutable")?.GetValue(e)}"))}]");
            foreach (var pool in ModelDb.AllRelicPools)
            {
                var inPool = eggs.Where(e => pool.AllRelics.Any(r => r.Id == e.Id)).Select(e => e.Id.Entry).ToList();
                if (inPool.Count > 0) Log.Write($"EGGDIAG: pool {pool.Id.Entry} contains {string.Join(",", inPool)}");
            }
            var rs = RunManager.Instance.DebugOnlyGetState();
            if (rs != null)
            {
                foreach (var e in eggs)
                    Log.Write($"EGGDIAG: {e.Id.Entry}.IsAllowed(run)={e.IsAllowed(rs)} owned={rs.Players[0].Relics.Any(r => r.Id == e.Id)}");
            }

            var loadout = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Loadout");
            if (loadout == null) { Log.Write("EGGDIAG: Loadout assembly not loaded"); return; }

            // Content bans
            var banService = loadout.GetTypes().FirstOrDefault(t => t.Name == "ContentBanService");
            var isBanned = banService?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "IsBanned" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(RelicModel));
            foreach (var e in eggs)
                Log.Write($"EGGDIAG: Loadout.IsBanned({e.Id.Entry}) = {(isBanned != null ? isBanned.Invoke(null, new object[] { e }) : "n/a")}");

            // Catalog entries
            var catalogService = loadout.GetTypes().FirstOrDefault(t => t.Name == "CustomRunCatalogService");
            var kindType = loadout.GetTypes().FirstOrDefault(t => t.Name == "SelectionModelKind");
            var getCatalog = catalogService?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).FirstOrDefault(m => m.Name == "GetCatalog");
            if (getCatalog != null && kindType != null)
            {
                var relicKind = Enum.Parse(kindType, "Relic");
                var entries = getCatalog.Invoke(null, new[] { relicKind }) as IEnumerable;
                int total = 0, eggCount = 0;
                if (entries != null)
                    foreach (var entry in entries)
                    {
                        total++;
                        var text = DescribeEntry(entry);
                        if (text.Contains("EGG")) { eggCount++; Log.Write("EGGDIAG: catalog entry " + text); }
                    }
                Log.Write($"EGGDIAG: Loadout relic catalog entries={total}, egg entries={eggCount}");
            }
            else Log.Write("EGGDIAG: Loadout catalog API not found");
        }
        catch (Exception ex)
        {
            Log.Write($"EGGDIAG error: {ex}");
        }
    }

    private static string DescribeEntry(object entry)
    {
        var sb = new System.Text.StringBuilder();
        var t = entry.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? v; try { v = p.GetValue(entry); } catch { continue; }
            string s = v is IEnumerable en && v is not string ? "[" + string.Join(",", en.Cast<object>().Select(o => o?.ToString())) + "]" : v?.ToString() ?? "null";
            sb.Append(p.Name).Append('=').Append(s.Length > 60 ? s[..60] : s).Append(' ');
        }
        return sb.ToString();
    }
}
