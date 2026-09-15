using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo.FastPath;

/// <summary>
/// Rebuilds the combat visuals from the (already restored) model, without touching the model.
///
/// The combat room node is replaced by a fresh one created the same way the game creates it when a combat
/// starts: <see cref="NCombatRoom.Create"/> builds creature nodes (HP bars, powers, block) from the
/// creatures, <c>OnCombatSetUp</c> activates the combat UI (piles, energy, end-turn button) and the
/// background. Freeing the old room drops every event subscription its nodes held. What a fresh room does
/// not have — hand cards, intents, orbs, end-turn state — is filled in from the model here, and the global
/// UI (potions, top bar, relic counters) is refreshed in place.
/// </summary>
internal static class VisualRebuild
{
    private static readonly MethodInfo? RoomOnCombatSetUp = AccessTools.Method(typeof(NCombatRoom), "OnCombatSetUp");
    private static readonly MethodInfo? EndTurnOnTurnStarted = AccessTools.Method(typeof(NEndTurnButton), "OnTurnStarted");
    private static readonly MethodInfo? TrackerNotify = AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged");
    private static readonly FieldInfo? PotionHolders = AccessTools.Field(typeof(NPotionContainer), "_holders");
    private static readonly FieldInfo? OrbNodes = AccessTools.Field(typeof(NOrbManager), "_orbs");
    private static readonly MethodInfo? OrbOnCombatSetup = AccessTools.Method(typeof(NOrbManager), "OnCombatSetup");
    private static readonly MethodInfo? RelicRefreshAmount = AccessTools.Method(typeof(NRelicInventoryHolder), "RefreshAmount");
    private static readonly MethodInfo? RelicRefreshStatus = AccessTools.Method(typeof(NRelicInventoryHolder), "RefreshStatus");
    private static readonly FieldInfo? HolderTargetPos = AccessTools.Field(typeof(NHandCardHolder), "_targetPosition");
    private static readonly FieldInfo? RoomContainer = AccessTools.Field(typeof(NRun), "_roomContainer");
    private static readonly FieldInfo? UiState = AccessTools.Field(typeof(NCombatUi), "_state");
    private static readonly PropertyInfo? RoomBackground = AccessTools.Property(typeof(NCombatRoom), "Background");
    private static readonly PropertyInfo? RoomBgContainer = AccessTools.Property(typeof(NCombatRoom), "BgContainer");
    private static readonly FieldInfo? LayoutRoomContainer = AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Events.NCombatEventLayout), "_combatRoomContainer");
    private static readonly PropertyInfo? LayoutEmbeddedRoom = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Nodes.Events.NCombatEventLayout), "EmbeddedCombatRoom");

    public static string ReflectionReport() =>
        $"OnCombatSetUp={(RoomOnCombatSetUp != null ? "OK" : "NULL")} OnTurnStarted={(EndTurnOnTurnStarted != null ? "OK" : "NULL")} " +
        $"NotifyCombatStateChanged={(TrackerNotify != null ? "OK" : "NULL")} _holders={(PotionHolders != null ? "OK" : "NULL")} " +
        $"_orbs={(OrbNodes != null ? "OK" : "NULL")} OrbOnCombatSetup={(OrbOnCombatSetup != null ? "OK" : "NULL")} " +
        $"RefreshAmount={(RelicRefreshAmount != null ? "OK" : "NULL")} _targetPosition={(HolderTargetPos != null ? "OK" : "NULL")} " +
        $"_roomContainer={(RoomContainer != null ? "OK" : "NULL")} NCombatUi._state={(UiState != null ? "OK" : "NULL")} " +
        $"Background.set={(RoomBackground?.SetMethod != null ? "OK" : "NULL")} BgContainer={(RoomBgContainer != null ? "OK" : "NULL")} " +
        $"_combatRoomContainer={(LayoutRoomContainer != null ? "OK" : "NULL")} EmbeddedCombatRoom.set={(LayoutEmbeddedRoom?.SetMethod != null ? "OK" : "NULL")}";

    /// <summary>
    /// A combat room whose UI was never activated crashes the game's screen-context update (its combat UI
    /// dereferences a null state while combat is in progress). If a rebuild failed half-way, give the current
    /// room's UI its state so the replay fallback's teardown can run.
    /// </summary>
    public static void MakeCurrentRoomSafe(CombatState cs)
    {
        try
        {
            var room = NCombatRoom.Instance;
            if (room?.Ui == null || UiState == null) return;
            if (UiState.GetValue(room.Ui) == null) UiState.SetValue(room.Ui, cs);
        }
        catch (Exception ex) { Log.Write($"fastpath: MakeCurrentRoomSafe failed: {ex.Message}"); }
    }

    /// <summary>Everything a fresh room needs that the game normally fills in over the course of a combat.</summary>
    public static async Task Rebuild(RunState rs, CombatState cs, CombatRoom room, Func<string> T, Action<string> log)
    {
        var nrun = NRun.Instance ?? throw new InvalidOperationException("NRun.Instance is null");
        var old = NCombatRoom.Instance ?? throw new InvalidOperationException("NCombatRoom.Instance is null");
        var me = LocalContext.GetMe(cs);

        // 1. Where creatures are drawn: the layout depends on spine bounds that settle after creation, so a
        //    fresh room can place them slightly differently. Keep what the player was looking at.
        var positions = new Dictionary<Creature, Vector2>(ReferenceEqualityComparer.Instance);
        foreach (var n in old.CreatureNodes) positions[n.Entity] = n.Position;
        foreach (var n in old.RemovingCreatureNodes) positions.TryAdd(n.Entity, n.Position);

        // 2. Replace the room node. The scene container removes and frees the old one (its _ExitTree handlers
        //    unsubscribe from the model and the combat manager) and readies the new one, which creates a
        //    node for every creature of the encounter. NRun.SetCurrentRoom is not used because it updates the
        //    active screen context right away, which enables the combat UI before it has a state (the game
        //    itself only gets away with that because combat is not "in progress" yet when it creates a room).
        var container = RoomContainer?.GetValue(nrun) as MegaCrit.Sts2.Core.Nodes.NSceneContainer
                        ?? throw new InvalidOperationException("NRun._roomContainer unavailable");
        // The background is carried over rather than recreated: some bosses (Kaiser Crab) are drawn by the
        // background and their monster models cache that node; a new background would leave them pointing
        // at a freed node and would start hidden.
        Node? keptBackground = null;
        if (old.Background != null && GodotObject.IsInstanceValid(old.Background) && RoomBackground?.SetMethod != null && RoomBgContainer != null)
        {
            keptBackground = old.Background;
            keptBackground.GetParent()?.RemoveChild(keptBackground);
        }
        var fresh = NCombatRoom.Create(room, CombatRoomMode.ActiveCombat)
                    ?? throw new InvalidOperationException("NCombatRoom.Create returned null");
        if (container.CurrentScene is NEventRoom eventRoom && eventRoom.Layout is MegaCrit.Sts2.Core.Nodes.Events.NCombatEventLayout layout)
        {
            // Combat-layout event (Punch-Off, The Architect, ...): the combat room lives inside the event layout.
            // Swap it there; the event room itself stays.
            if (LayoutRoomContainer?.GetValue(layout) is not Node roomHost || LayoutEmbeddedRoom?.SetMethod == null)
                throw new InvalidOperationException("event layout internals unavailable");
            old.GetParent()?.RemoveChild(old);
            old.QueueFree();
            LayoutEmbeddedRoom.SetValue(layout, fresh);
            roomHost.AddChild(fresh);
            log($"{T()} embedded room swapped inside the event layout");
        }
        else
        {
            container.SetCurrentScene(fresh);
        }
        if (keptBackground != null)
        {
            try
            {
                (RoomBgContainer!.GetValue(fresh) as Node)?.AddChild(keptBackground);
                RoomBackground!.SetValue(fresh, keptBackground);
                log($"{T()} background carried over");
            }
            catch (Exception ex)
            {
                Log.Write("fastpath: " + $"background carry-over failed ({ex.Message}); a new one will be created");
                if (keptBackground.GetParent() == null) keptBackground.QueueFree();
            }
        }
        log($"{T()} room node replaced ({fresh.CreatureNodes.Count()} creature nodes)");

        // 3. Creatures that are dead or gone in the restored state get no node (the game removes theirs
        //    after the death animation); the others go back to their previous screen positions.
        var alive = new HashSet<Creature>(cs.Creatures, ReferenceEqualityComparer.Instance);
        foreach (var n in fresh.CreatureNodes.ToList())
        {
            if (n.Entity.IsDead || !alive.Contains(n.Entity))
            {
                fresh.RemoveCreatureNode(n);
                n.GetParent()?.RemoveChild(n);
                n.QueueFree();
                log($"{T()} dropped node for {n.Entity} (dead)");
            }
            else if (positions.TryGetValue(n.Entity, out var p))
            {
                n.Position = p;
            }
        }

        // 4. What CombatManager.CombatSetUp does for a new room: activate the combat UI (and create a
        //    background only if none was carried over).
        //    Only now is the room safe to become the active screen.
        if (RoomOnCombatSetUp != null) RoomOnCombatSetUp.Invoke(fresh, new object[] { cs });
        else { fresh.Ui.Activate(cs); fresh.SetUpBackground(rs); }
        MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext.Instance.Update();
        log($"{T()} combat UI activated");

        // 5. Hand cards, in pile order.
        var hand = fresh.Ui.Hand;
        int added = 0;
        foreach (var card in me.PlayerCombatState!.Hand.Cards)
        {
            var nCard = NCard.Create(card);
            if (nCard == null) continue;
            nCard.Scale = Vector2.One;
            hand.Add(nCard);
            added++;
        }
        hand.ForceRefreshCardIndices();
        log($"{T()} hand: {added} cards");

        // 6. End-turn button: normally enabled by TurnStarted, which will not fire again for this turn.
        var endTurn = fresh.Ui.EndTurnButton;
        try
        {
            EndTurnOnTurnStarted?.Invoke(endTurn, new object[] { cs });
            endTurn.RefreshEnabled();
        }
        catch (Exception ex) { Log.Write("fastpath: " + $"end turn button: {ex.Message}"); }

        // 7. Enemy intents.
        foreach (var n in fresh.CreatureNodes)
        {
            if (n.Entity.Monster == null || n.Entity.IsDead) continue;
            try { await n.RefreshIntents(); }
            catch (Exception ex) { Log.Write("fastpath: " + $"intents for {n.Entity}: {ex.Message}"); }
        }

        // 8. Orb slots and orbs (Defect). Slots come from the same handler the game runs at combat set-up;
        //    orbs are placed one per slot from the queue.
        foreach (var n in fresh.CreatureNodes)
        {
            var om = n.OrbManager;
            var pcs = n.Entity.Player?.PlayerCombatState;
            if (om == null || pcs == null) continue;
            try
            {
                OrbOnCombatSetup?.Invoke(om, new object[] { cs });
                var orbs = pcs.OrbQueue.Orbs.ToList();
                if (orbs.Count > 0 && OrbNodes?.GetValue(om) is IList nodes)
                {
                    for (int k = 0; k < orbs.Count && k < nodes.Count; k++)
                        if (nodes[k] is NOrb orbNode) orbNode.ReplaceOrb(orbs[k]);
                    om.UpdateVisuals(OrbEvokeType.None);
                }
            }
            catch (Exception ex) { Log.Write("fastpath: " + $"orbs: {ex.Message}"); }
        }

        // 9. Potion belt (global UI, survives the room swap): make each slot show the model's potion.
        try
        {
            var potionContainer = nrun.GlobalUi.TopBar.PotionContainer;
            if (PotionHolders?.GetValue(potionContainer) is IList holders)
            {
                var slots = me.PotionSlots;
                int changed = 0;
                for (int k = 0; k < holders.Count; k++)
                {
                    if (holders[k] is not NPotionHolder holder) continue;
                    var want = k < slots.Count ? slots[k] : null;
                    var have = holder.Potion?.Model;
                    if (ReferenceEquals(want, have)) continue;
                    if (have != null) holder.RemoveUsedPotion();
                    if (want != null)
                    {
                        var np = NPotion.Create(want);
                        if (np != null)
                        {
                            np.Position = new Vector2(-30f, -30f); // as NPotionContainer.Add does
                            holder.AddPotion(np);
                            // A new NPotion is invisible until the game's "newly acquired" animation fades it in;
                            // show it as it looks when that animation has finished.
                            var inner = np.GetNodeOrNull<Control>("Container");
                            if (inner != null) { inner.Modulate = Colors.White; inner.Position = Vector2.Zero; }
                        }
                    }
                    changed++;
                }
                if (changed > 0) log($"{T()} potions: {changed} slot(s) updated");
            }
        }
        catch (Exception ex) { Log.Write("fastpath: " + $"potions: {ex.Message}"); }

        // 10. Top bar HP / gold and relic counters are event-driven; the restore fired no events.
        try
        {
            var top = nrun.GlobalUi.TopBar;
            InvokePrivate(top.Hp, "UpdateHealth", 0, 0);
            InvokePrivate(top.Gold, "UpdateGold");
            foreach (var holder in nrun.GlobalUi.RelicInventory.RelicNodes)
            {
                RelicRefreshAmount?.Invoke(holder, null);
                RelicRefreshStatus?.Invoke(holder, null);
            }
        }
        catch (Exception ex) { Log.Write("fastpath: " + $"top bar / relics: {ex.Message}"); }

        // 11. Let every listener recompute (card playability, energy label, end-turn glow, intent numbers).
        try { TrackerNotify?.Invoke(CombatManager.Instance.StateTracker, new object[] { "UndoAndRedo fast path" }); }
        catch (Exception ex) { Log.Write("fastpath: " + $"state notify: {ex.Message}"); }

        try { fresh.EnableControllerNavigation(); } catch (Exception ex) { Log.Write("fastpath: " + $"navigation: {ex.Message}"); }
    }

    private static void InvokePrivate(object? target, string name, params object[] args)
    {
        if (target == null) return;
        var m = AccessTools.Method(target.GetType(), name);
        m?.Invoke(target, args.Length == 0 ? null : args);
    }

    /// <summary>
    /// Waits (with the screen covered and time scaled up) until the hand shows every card at its resting
    /// position and the UI slide-in tweens have had enough scaled time to finish.
    /// </summary>
    public static async Task WaitForVisuals(RunState rs, int minFrames, double timeoutMs, Action<string> log)
    {
        var player = rs.Players[0];
        var start = Time.GetTicksMsec();
        int frames = 0, stable = 0;
        string last = "";
        while (Time.GetTicksMsec() - start < timeoutMs)
        {
            var hand = NPlayerHand.Instance;
            int model = player.PlayerCombatState?.Hand.Cards.Count ?? 0;
            var holders = hand?.ActiveHolders;
            bool inPlace = holders != null && holders.Count >= model;
            float maxDist = 0f;
            if (inPlace && HolderTargetPos != null)
            {
                foreach (var h in holders!)
                {
                    if (HolderTargetPos.GetValue(h) is Vector2 target)
                    {
                        float d = (h.Position - target).Length();
                        if (!float.IsFinite(d) || d > 2f) inPlace = false;
                        maxDist = Math.Max(maxDist, float.IsFinite(d) ? d : float.MaxValue);
                    }
                }
            }
            string now = $"holders={holders?.Count} model={model} inPlace={inPlace} maxDist={maxDist:F1}";
            if (now != last) { Log.Debug("fastpath: visuals: " + now); last = now; }
            stable = inPlace ? stable + 1 : 0;
            if (stable >= 2 && frames >= minFrames) break;
            await ScreenCover.NextFrame();
            frames++;
        }
    }
}
