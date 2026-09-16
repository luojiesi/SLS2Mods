using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Random;

namespace RngPredictor;

/// <summary>
/// Side-effect-free mirror of the orb queue (OrbCmd.Channel / EvokeNext / OrbQueue.BeforeTurnEnd) for one
/// player: channels push out the front orb when the slots are full, evokes hit the front orb, end-of-turn
/// passives fire in slot order. Only Lightning consumes the CombatTargets stream (one draw per hit over the
/// hittable opponents); its values come from real orb instances (existing ones) or detached mutable copies
/// owned by the player (new ones), so Focus is included.
/// </summary>
internal sealed class OrbSim
{
    private readonly Player _p;
    private readonly ICombatState? _cs;
    private readonly Rng _targets;
    private readonly List<OrbModel> _queue;
    private int _capacity;
    // Predicted remaining HP+block per enemy: a Lightning hit that would kill an enemy removes it from later draws,
    // exactly as the game's hittable list shrinks when an enemy dies mid-sequence.
    private readonly DamageSim _dmg;

    public readonly List<string> Lines = new();
    /// <summary>One entry per Lightning hit: target, damage actually absorbed (block + HP), what caused it.</summary>
    public readonly List<(Creature creature, decimal damage, string what)> Hits = new();
    public readonly HashSet<Creature> Kills = new();
    public int LightningEvents;

    public IReadOnlyList<OrbModel> Queue => _queue;

    public OrbSim(Player p, Rng targetsClone)
    {
        _p = p;
        _cs = p.Creature?.CombatState;
        _targets = targetsClone;
        _queue = p.PlayerCombatState.OrbQueue.Orbs.ToList();
        _capacity = p.PlayerCombatState.OrbQueue.Capacity;
        _dmg = new DamageSim(p);
    }

    public OrbModel NewOrb(OrbModel canonical)
    {
        var m = canonical.ToMutable();
        try { m.Owner = _p; } catch { }
        return m;
    }

    public OrbModel NewOrb<T>() where T : OrbModel => NewOrb(ModelDb.Orb<T>());

    private static string Name(OrbModel o)
    {
        try { return o.Title.GetFormattedText(); } catch { return o.Id.Entry; }
    }

    private static string Name(Creature c)
    {
        try { return c.Name; } catch { return "?"; }
    }

    public void Channel(OrbModel orb)
    {
        if (_p.Character.BaseOrbSlotCount == 0 && _capacity == 0) _capacity = 1;
        if (_queue.Count >= _capacity) EvokeNext(dequeue: true);
        if (_capacity == 0) return;
        _queue.Add(orb);
        Lines.Add(L.T("充能 ", "Channel ") + Name(orb));
    }

    public void EvokeNext(bool dequeue)
    {
        if (_queue.Count == 0) return;
        var orb = _queue[0];
        Evoke(orb);
        if (dequeue) _queue.RemoveAt(0);
    }

    private void Evoke(OrbModel orb)
    {
        if (orb is LightningOrb)
        {
            var t = PickTarget();
            decimal dmg = 0m;
            try { dmg = orb.EvokeVal; } catch { }
            if (t != null)
            {
                var hit = ApplyDamage(t, dmg, L.T("激发", "Evoke"));
                Lines.Add(L.T($"激发 {Name(orb)} → {Name(t)} {hit.Damage}", $"Evoke {Name(orb)} → {Name(t)} {hit.Damage}") + (hit.Kill ? L.T("（击杀）", " (kill)") : ""));
                LightningEvents++;
            }
            return;
        }
        Lines.Add(L.T("激发 ", "Evoke ") + Name(orb));
    }

    /// <summary>Runs the hit through the shared damage ledger (Lightning damage is Unpowered) and records it.</summary>
    private DamageSim.Hit ApplyDamage(Creature t, decimal dmg, string what)
    {
        var hit = _dmg.Apply(t, dmg, MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered, _p.Creature, null);
        Hits.Add((t, hit.Dealt, what));
        if (hit.Kill) Kills.Add(t);
        return hit;
    }

    /// <summary>Mirror of OrbQueue.BeforeTurnEnd: every orb's passive, in slot order, with the trigger-count hook.</summary>
    public void EndTurnPassives()
    {
        if (_cs == null) return;
        foreach (var orb in _queue.ToList())
        {
            int count = 1;
            try { count = MegaCrit.Sts2.Core.Hooks.Hook.ModifyOrbPassiveTriggerCount(_cs, orb, 1, out _); } catch { }
            for (int i = 0; i < count; i++)
            {
                if (orb is LightningOrb)
                {
                    var t = PickTarget();
                    decimal dmg = 0m;
                    try { dmg = orb.PassiveVal; } catch { }
                    if (t != null)
                    {
                        var hit = ApplyDamage(t, dmg, L.T("被动", "Passive"));
                        Lines.Add(L.T($"回合结束 {Name(orb)} → {Name(t)} {hit.Damage}", $"End of turn {Name(orb)} → {Name(t)} {hit.Damage}") + (hit.Kill ? L.T("（击杀）", " (kill)") : ""));
                        LightningEvents++;
                    }
                }
            }
        }
    }

    private Creature? PickTarget()
    {
        if (_cs == null) return null;
        var list = _cs.GetOpponentsOf(_p.Creature).Where(e => e.IsHittable && !_dmg.IsDead(e)).ToList();
        if (list.Count == 0) return null;
        return _targets.NextItem(list);
    }

    /// <summary>Per-creature summary for the overlay markers: "⚡×N (total)".</summary>
    public void AddTargetsTo(Prediction pr)
    {
        var order = new List<Creature>();
        var count = new Dictionary<Creature, int>();
        var total = new Dictionary<Creature, decimal>();
        foreach (var (c, dmg, _) in Hits)
        {
            if (!count.ContainsKey(c)) { count[c] = 0; total[c] = 0m; order.Add(c); }
            count[c]++;
            total[c] += dmg;
        }
        foreach (var c in order)
            pr.Targets.Add((c, L.T($"电球 ×{count[c]} ({total[c]})", $"Lightning ×{count[c]} ({total[c]})") + (Kills.Contains(c) ? L.T(" 击杀", " kill") : "")));
    }
}
