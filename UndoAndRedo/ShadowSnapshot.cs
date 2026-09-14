using System.Collections;
using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo;

/// <summary>
/// Research tool for a future snapshot-based fast path.
///
/// Walks the whole model object graph reachable from the run state and the combat manager with reflection
/// and flattens it into (path → value) pairs. A snapshot is taken right before every player decision; after
/// a rewind has restored the state through replay (the authoritative path), a second snapshot is taken and
/// the two are diffed on a thread-pool thread, so the comparison never costs a game frame. The diffs are
/// written to logs/UndoAndRedo.shadow.log and tell which fields a direct restore would have to reproduce
/// and which are transient noise.
/// </summary>
internal static class ShadowSnapshot
{
    private static readonly string ShadowLogPath = System.IO.Path.Combine(OS.GetUserDataDir(), "logs", "UndoAndRedo.shadow.log");
    private const int MaxDepth = 14;
    private const int MaxEntries = 400_000;
    private static readonly object _logLock = new();

    public sealed class Snapshot
    {
        public required Dictionary<string, string> Values;
        public required int ObjectCount;
        public required double CaptureMs;
        public required string Label;
    }

    private static readonly HashSet<string> SkipFieldNames = new(StringComparer.Ordinal)
    {
        "_logger", "_random", "_cts", "_combatCts", "_completionSource", "_executionTask",
        "_pauseForPlayerChoiceTaskSource", "_executeAfterResumptionTaskSource",
    };

    private static bool IsSkippedType(Type t)
    {
        if (typeof(Delegate).IsAssignableFrom(t)) return true;
        if (typeof(GodotObject).IsAssignableFrom(t)) return true;
        if (typeof(Task).IsAssignableFrom(t)) return true;
        if (t == typeof(CancellationToken) || typeof(CancellationTokenSource).IsAssignableFrom(t)) return true;
        if (t.FullName != null && (t.FullName.StartsWith("System.Threading") || t.FullName.StartsWith("Godot."))) return true;
        if (typeof(System.Random).IsAssignableFrom(t)) return true;
        if (t.Name.Contains("Logger")) return true;
        return false;
    }

    private static bool IsLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTimeOffset)
        || t == typeof(DateTime) || t == typeof(Guid) || t == typeof(Vector2) || t == typeof(Vector2I) || t == typeof(Color);

    /// <summary>Takes a snapshot of the live model graph. Must run on the main thread.</summary>
    public static Snapshot? Capture(string label)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var values = new Dictionary<string, string>(16384);
            var visited = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            var rm = RunManager.Instance;
            var rs = rm.DebugOnlyGetState();
            if (rs == null) return null;
            Walk("Run", rs, values, visited, 0);
            Walk("Combat", CombatManager.Instance, values, visited, 0);
            Walk("CombatCardDb", MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb.Instance, values, visited, 0);
            Walk("Queue", rm.ActionQueueSet, values, visited, 0);
            Walk("Choices", rm.PlayerChoiceSynchronizer, values, visited, 0);
            Walk("Hooks", rm.ActionQueueSynchronizer, values, visited, 0);
            sw.Stop();
            return new Snapshot { Values = values, ObjectCount = visited.Count, CaptureMs = sw.Elapsed.TotalMilliseconds, Label = label };
        }
        catch (Exception ex)
        {
            Log.Write($"shadow capture failed: {ex.Message}");
            return null;
        }
    }

    private static void Walk(string path, object? obj, Dictionary<string, string> values, Dictionary<object, string> visited, int depth)
    {
        if (values.Count >= MaxEntries) return;
        if (obj == null) { values[path] = "null"; return; }
        var t = obj.GetType();
        if (IsLeaf(t)) { values[path] = Convert.ToString(obj, System.Globalization.CultureInfo.InvariantCulture) ?? ""; return; }
        if (IsSkippedType(t)) return;
        if (obj is Rng rng) { values[path] = $"Rng(seed={rng.Seed},counter={rng.Counter})"; return; }
        if (depth > MaxDepth) { values[path] = "(depth)"; return; }
        if (visited.TryGetValue(obj, out var firstPath)) { values[path] = "(ref " + firstPath + ")"; return; }
        visited[obj] = path;

        if (obj is IDictionary dict)
        {
            values[path + ".Count"] = dict.Count.ToString();
            foreach (DictionaryEntry e in dict)
                Walk(path + "[" + Convert.ToString(e.Key, System.Globalization.CultureInfo.InvariantCulture) + "]", e.Value, values, visited, depth + 1);
            return;
        }
        if (obj is IEnumerable seq && obj is not string)
        {
            int i = 0;
            foreach (var item in seq)
            {
                Walk(path + "[" + i + "]", item, values, visited, depth + 1);
                i++;
                if (i > 5000) break;
            }
            values[path + ".Count"] = i.ToString();
            return;
        }

        values[path + ".$type"] = t.Name;
        for (var type = t; type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (SkipFieldNames.Contains(f.Name) || IsSkippedType(f.FieldType)) continue;
                object? v;
                try { v = f.GetValue(obj); } catch { continue; }
                string name = f.Name;
                if (name.StartsWith("<") && name.EndsWith(">k__BackingField")) name = name.Substring(1, name.IndexOf('>') - 1);
                Walk(path + "." + name, v, values, visited, depth + 1);
            }
        }
    }

    /// <summary>Diffs two snapshots on a worker thread and appends the result to the shadow log.</summary>
    public static void CompareInBackground(Snapshot expected, Snapshot actual, string context)
    {
        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var diffs = new List<string>();
                int onlyExpected = 0, onlyActual = 0, changed = 0;
                foreach (var kv in expected.Values)
                {
                    if (!actual.Values.TryGetValue(kv.Key, out var av)) { onlyExpected++; if (diffs.Count < 300) diffs.Add($"  - {kv.Key} = {Trim(kv.Value)}"); }
                    else if (av != kv.Value) { changed++; if (diffs.Count < 300) diffs.Add($"  ~ {kv.Key}: {Trim(kv.Value)} -> {Trim(av)}"); }
                }
                foreach (var kv in actual.Values)
                    if (!expected.Values.ContainsKey(kv.Key)) { onlyActual++; if (diffs.Count < 300) diffs.Add($"  + {kv.Key} = {Trim(kv.Value)}"); }
                sw.Stop();
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] === SHADOW DIFF {context} ===");
                sb.AppendLine($"  expected: {expected.Label} ({expected.Values.Count} values, {expected.ObjectCount} objects, captured in {expected.CaptureMs:F1} ms)");
                sb.AppendLine($"  actual:   {actual.Label} ({actual.Values.Count} values, {actual.ObjectCount} objects, captured in {actual.CaptureMs:F1} ms)");
                sb.AppendLine($"  changed={changed} onlyInExpected={onlyExpected} onlyInActual={onlyActual} (diff took {sw.Elapsed.TotalMilliseconds:F1} ms)");
                foreach (var d in diffs) sb.AppendLine(d);
                if (changed + onlyExpected + onlyActual > diffs.Count) sb.AppendLine($"  ... {changed + onlyExpected + onlyActual - diffs.Count} more");
                lock (_logLock)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShadowLogPath)!);
                    System.IO.File.AppendAllText(ShadowLogPath, sb.ToString());
                }
                Log.Write($"shadow diff {context}: changed={changed} onlyExpected={onlyExpected} onlyActual={onlyActual} (see UndoAndRedo.shadow.log)");
            }
            catch (Exception ex)
            {
                Log.Write($"shadow diff failed: {ex.Message}");
            }
        });
    }

    private static string Trim(string s) => s.Length > 80 ? s.Substring(0, 77) + "..." : s;
}
