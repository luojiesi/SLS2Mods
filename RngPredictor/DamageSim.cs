using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.ValueProps;

namespace RngPredictor;

/// <summary>
/// Predicted HP / block ledger for one prediction: mirrors what CreatureCmd.Damage does to a target
/// (Hook.ModifyDamage → block absorbs → Hook.ModifyHpLost → LoseHpInternal) without touching the creatures,
/// so a sequence of random hits can drop an enemy from the later target draws once a hit would kill it,
/// exactly like the game's per-hit "where c.IsAlive" filter. Shared by the attack and orb simulations.
/// </summary>
internal sealed class DamageSim
{
    /// <param name="Damage">Damage after the modify-damage hook (Strength, Vulnerable, ...).</param>
    /// <param name="Dealt">Block removed + HP lost, i.e. what the target actually absorbs.</param>
    public readonly record struct Hit(Creature Target, decimal Damage, decimal Dealt, decimal HpLoss, bool Kill);

    private readonly Player _p;
    private readonly Dictionary<Creature, int> _hp = new();
    private readonly Dictionary<Creature, int> _block = new();
    private readonly HashSet<Creature> _dead = new();

    public DamageSim(Player p) { _p = p; }

    public bool IsDead(Creature c) => _dead.Contains(c) || (!_hp.ContainsKey(c) && SafeIsDead(c));

    private static bool SafeIsDead(Creature c) { try { return c.IsDead; } catch { return false; } }

    private void Init(Creature c)
    {
        if (_hp.ContainsKey(c)) return;
        try { _hp[c] = c.CurrentHp; _block[c] = c.Block; } catch { _hp[c] = int.MaxValue; _block[c] = 0; }
    }

    /// <summary>Apply one hit of <paramref name="baseAmount"/> to <paramref name="target"/> and record what it does.</summary>
    public Hit Apply(Creature target, decimal baseAmount, ValueProp props, Creature? dealer, CardModel? card)
    {
        Init(target);
        decimal amount = baseAmount;
        var cs = _p.Creature?.CombatState;
        try { amount = Hook.ModifyDamage(_p.RunState, cs, target, dealer, baseAmount, props, card, ModifyDamageHookType.All, CardPreviewMode.None, out _); }
        catch { }
        decimal blocked = props.HasFlag(ValueProp.Unblockable) ? 0m : Math.Min(_block[target], amount);
        _block[target] -= (int)blocked;
        decimal unblocked = Math.Max(amount - blocked, 0m);
        try { unblocked = Hook.ModifyHpLost(_p.RunState, cs, target, unblocked, props, dealer, card, HpLossHookPhase.BeforeOsty, out _); } catch { }
        try { unblocked = Hook.ModifyHpLost(_p.RunState, cs, target, unblocked, props, dealer, card, HpLossHookPhase.AfterOsty, out _); } catch { }
        int before = _hp[target];
        int loss = (int)Math.Min(unblocked, 999999999m);
        _hp[target] = Math.Max(before - loss, 0);
        bool kill = before > 0 && _hp[target] == 0;
        if (kill) _dead.Add(target);
        return new Hit(target, amount, blocked + (before - _hp[target]), before - _hp[target], kill);
    }

    /// <summary>
    /// Mirror of AttackCommand.Execute with TargetingRandomOpponents: per hit, one CombatTargets draw over the
    /// attacker's living opponents (minus the ones earlier hits killed), then the damage pipeline above.
    /// The hit count goes through Hook.ModifyAttackHitCount on a throw-away builder, like the game does.
    /// </summary>
    public List<Hit> RandomAttack(CardModel card, decimal baseDamage, ValueProp props, int hits, Rng targets, Creature? dealer = null)
    {
        var result = new List<Hit>();
        var attacker = dealer ?? _p.Creature;
        var cs = attacker?.CombatState;
        if (attacker == null || cs == null) return result;
        decimal count = hits;
        try
        {
            var cmd = DamageCmd.Attack(baseDamage).WithHitCount(hits);
            cmd = dealer != null && dealer != _p.Creature ? cmd.FromOsty(dealer, card) : cmd.FromCard(card);
            cmd = cmd.TargetingRandomOpponents(cs);
            count = Hook.ModifyAttackHitCount(cs, cmd, hits);
        }
        catch { }
        for (int i = 0; i < count; i++)
        {
            var valid = cs.GetOpponentsOf(attacker).Where(c => !IsDead(c)).ToList();
            if (valid.Count == 0) break;
            var t = targets.NextItem(valid);
            if (t == null) break;
            result.Add(Apply(t, baseDamage, props, attacker, card));
        }
        return result;
    }
}
