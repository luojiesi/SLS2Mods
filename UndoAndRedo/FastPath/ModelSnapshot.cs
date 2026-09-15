using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace UndoAndRedo.FastPath;

/// <summary>
/// In-place memento of an object graph.
///
/// Capture walks every object reachable from the given roots and records, for each object, the value of
/// every instance field (value types boxed, references kept as references) and, for arrays, every element.
/// Restore writes all of that back into the very same object instances. Because instances are preserved,
/// everything that holds a reference to them — Godot nodes, event subscriptions, other models — keeps
/// working. Collections need no special handling: a List's <c>_items</c>/<c>_size</c> and a Dictionary's
/// <c>_entries</c>/<c>_count</c> are ordinary fields and arrays.
///
/// Never touched (neither walked nor restored): Godot objects, delegates/events, tasks and threading
/// primitives, reflection metadata, loggers, canonical (immutable) models, and static fields.
/// </summary>
internal sealed class ModelSnapshot
{
    private readonly struct ObjectRecord
    {
        public readonly object Target;
        public readonly FieldInfo[] Fields;
        public readonly object?[] Values;
        public ObjectRecord(object target, FieldInfo[] fields, object?[] values) { Target = target; Fields = fields; Values = values; }
    }

    private readonly struct ArrayRecord
    {
        public readonly Array Target;
        public readonly object?[] Elements;
        public ArrayRecord(Array target, object?[] elements) { Target = target; Elements = elements; }
    }

    private readonly List<ObjectRecord> _objects = new();
    private readonly List<ArrayRecord> _arrays = new();

    public int ObjectCount => _objects.Count + _arrays.Count;
    public double CaptureMs { get; private set; }
    public string Label { get; set; } = "";

    // ── type classification ──────────────────────────────────────────────────

    private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();
    private static readonly Dictionary<Type, bool> _skipCache = new();
    private static readonly PropertyInfo? IsMutableProp = HarmonyLib.AccessTools.Property(typeof(AbstractModel), "IsMutable");

    private static bool IsLeafType(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
        || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) || t == typeof(Guid);

    /// <summary>Types whose instances are left completely alone.</summary>
    private static bool IsUntouchable(Type t)
    {
        if (_skipCache.TryGetValue(t, out var cached)) return cached;
        bool skip =
            typeof(GodotObject).IsAssignableFrom(t)
            || typeof(Delegate).IsAssignableFrom(t)
            || typeof(Task).IsAssignableFrom(t)
            || typeof(CancellationTokenSource).IsAssignableFrom(t) || t == typeof(CancellationToken)
            || typeof(Type).IsAssignableFrom(t) || typeof(MemberInfo).IsAssignableFrom(t)
            || typeof(System.Threading.Lock).IsAssignableFrom(t)
            || t.Name.Contains("Logger")
            || t.Name.Contains("TaskCompletionSource")
            || (t.Namespace != null && (t.Namespace.StartsWith("System.Threading") || t.Namespace.StartsWith("System.Reflection")
                                        || t.Namespace.StartsWith("Godot") || t.Namespace.StartsWith("System.Runtime")
                                        || t.Namespace.StartsWith("MegaCrit.Sts2.Core.Localization")
                                        || t.Namespace.StartsWith("MegaCrit.Sts2.Core.Logging")))
            || t == typeof(ModelId) || t == typeof(ModelDb)
            || t.Name == "PacketWriter" || t.Name == "PacketReader";
        _skipCache[t] = skip;
        return skip;
    }

    private static FieldInfo[] FieldsOf(Type t)
    {
        if (_fieldCache.TryGetValue(t, out var f)) return f;
        var list = new List<FieldInfo>();
        for (var type = t; type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var ft = fi.FieldType;
                if (typeof(Delegate).IsAssignableFrom(ft)) continue;        // events / callbacks stay live
                if (!ft.IsValueType && ft != typeof(object) && IsUntouchable(ft)) continue;
                list.Add(fi);
            }
        }
        f = list.ToArray();
        _fieldCache[t] = f;
        return f;
    }

    // ── capture ──────────────────────────────────────────────────────────────

    public static ModelSnapshot Capture(string label, params object?[] roots)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = new ModelSnapshot { Label = label };
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots) if (r != null) snap.Discover(r, visited, queue);
        while (queue.Count > 0)
            snap.Record(queue.Dequeue(), visited, queue);
        sw.Stop();
        snap.CaptureMs = sw.Elapsed.TotalMilliseconds;
        return snap;
    }

    /// <summary>Registers a value: enqueues class instances, scans struct fields for references.</summary>
    private void Discover(object? value, HashSet<object> visited, Queue<object> queue, int structDepth = 0)
    {
        if (value == null) return;
        var t = value.GetType();
        if (IsLeafType(t)) return;
        if (t.IsValueType)
        {
            if (structDepth > 6) return;
            foreach (var f in FieldsOf(t))
            {
                if (IsLeafType(f.FieldType)) continue;
                object? v; try { v = f.GetValue(value); } catch { continue; }
                Discover(v, visited, queue, structDepth + 1);
            }
            return;
        }
        if (IsUntouchable(t)) return;
        if (value is AbstractModel m && IsMutableProp?.GetValue(m) is false) return; // canonical template
        if (visited.Add(value)) queue.Enqueue(value);
    }

    private void Record(object obj, HashSet<object> visited, Queue<object> queue)
    {
        if (obj is Array arr)
        {
            if (arr.Rank != 1) return;
            var elemType = arr.GetType().GetElementType()!;
            var elems = new object?[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var v = arr.GetValue(i);
                elems[i] = v;
                if (!IsLeafType(elemType)) Discover(v, visited, queue);
            }
            _arrays.Add(new ArrayRecord(arr, elems));
            return;
        }
        var fields = FieldsOf(obj.GetType());
        var values = new object?[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            object? v;
            try { v = fields[i].GetValue(obj); } catch { v = null; }
            values[i] = v;
            if (v != null && !IsLeafType(fields[i].FieldType)) Discover(v, visited, queue);
        }
        _objects.Add(new ObjectRecord(obj, fields, values));
    }

    // ── disposed Godot references ────────────────────────────────────────────

    private static readonly Dictionary<Type, FieldInfo[]> _nodeFieldCache = new();

    /// <summary>Instance fields that can hold a Godot object (declared as one, or as plain object).</summary>
    private static FieldInfo[] NodeFieldsOf(Type t)
    {
        if (_nodeFieldCache.TryGetValue(t, out var f)) return f;
        var list = new List<FieldInfo>();
        for (var type = t; type != null && type != typeof(object); type = type.BaseType)
            foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (typeof(GodotObject).IsAssignableFrom(fi.FieldType) || fi.FieldType == typeof(object))
                    list.Add(fi);
        f = list.ToArray();
        _nodeFieldCache[t] = f;
        return f;
    }

    /// <summary>
    /// Walks the model graph like <see cref="Capture"/> and nulls every field that still points at a Godot
    /// object that has been freed (e.g. a monster caching a node of a combat room that no longer exists;
    /// such caches are lazy getters that re-resolve when null). Returns the number of fields cleared.
    /// </summary>
    public static int ClearDisposedGodotReferences(params object?[] roots)
    {
        var walker = new ModelSnapshot();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        foreach (var r in roots) if (r != null) walker.Discover(r, visited, queue);
        int cleared = 0;
        while (queue.Count > 0)
        {
            var obj = queue.Dequeue();
            if (obj is Array arr)
            {
                if (arr.Rank != 1 || IsLeafType(arr.GetType().GetElementType()!)) continue;
                for (int i = 0; i < arr.Length; i++) walker.Discover(arr.GetValue(i), visited, queue);
                continue;
            }
            var t = obj.GetType();
            foreach (var f in FieldsOf(t))
            {
                if (IsLeafType(f.FieldType)) continue;
                object? v; try { v = f.GetValue(obj); } catch { continue; }
                walker.Discover(v, visited, queue);
            }
            foreach (var f in NodeFieldsOf(t))
            {
                object? v; try { v = f.GetValue(obj); } catch { continue; }
                if (v is GodotObject go && !GodotObject.IsInstanceValid(go))
                {
                    try { f.SetValue(obj, null); cleared++; Log.Write($"fastpath: cleared disposed {f.FieldType.Name} {t.Name}.{f.Name}"); }
                    catch { }
                }
            }
        }
        return cleared;
    }

    // ── restore ──────────────────────────────────────────────────────────────

    /// <summary>Writes every recorded field and array element back into the original instances.</summary>
    public (int objects, int arrays, int errors) Restore()
    {
        int errors = 0;
        foreach (var rec in _objects)
        {
            for (int i = 0; i < rec.Fields.Length; i++)
            {
                var f = rec.Fields[i];
                var v = rec.Values[i];
                // A field typed 'object' may currently hold something we must not touch (e.g. a Godot object).
                if (v != null && !f.FieldType.IsValueType && IsUntouchable(v.GetType())) continue;
                try { f.SetValue(rec.Target, v); }
                catch { errors++; }
            }
        }
        foreach (var rec in _arrays)
        {
            var arr = rec.Target;
            int n = Math.Min(arr.Length, rec.Elements.Length);
            for (int i = 0; i < n; i++)
            {
                try { arr.SetValue(rec.Elements[i], i); }
                catch { errors++; }
            }
        }
        return (_objects.Count, _arrays.Count, errors);
    }
}
