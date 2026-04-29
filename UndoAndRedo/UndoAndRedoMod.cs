using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoAndRedo;

[ModInitializer("Initialize")]
public static class UndoAndRedoMod
{
    private const int MaxUndoStackSize = 50;
    private static readonly List<CombatSnapshot> UndoStack = new();
    private static readonly List<CombatSnapshot> RedoStack = new();
    internal static bool IsRestoring;
    internal static bool PendingStartTurnRecoveryCheck;

    public static void Initialize()
    {
        var harmony = new Harmony("com.undoandredo.sts2");
        harmony.PatchAll(typeof(UndoAndRedoMod).Assembly);
    }

    public static void TakeSnapshot()
    {
        if (IsRestoring) return;

        var snapshot = CombatSnapshot.Capture();
        if (snapshot == null || snapshot.IsFailed) return;

        UndoStack.Add(snapshot);
        if (UndoStack.Count > MaxUndoStackSize)
            UndoStack.RemoveAt(0);
        RedoStack.Clear();
        // Diagnostic: log game state at capture time to help debug stuck animations
        try
        {
            var inTransition = NGame.Instance?.Transition?.InTransition == true;
            var aqSet = RunManager.Instance?.ActionQueueSet;
            var aqEmpty = aqSet?.IsEmpty ?? true;
            var playQueue = NCardPlayQueue.Instance;
            var pqCount = 0;
            if (playQueue != null && PlayQueueField != null)
            {
                var queueList = PlayQueueField.GetValue(playQueue) as System.Collections.IList;
                pqCount = queueList?.Count ?? 0;
            }
            var hand = NPlayerHand.Instance;
            var currentPlay = hand != null && HandCurrentCardPlayField != null
                ? HandCurrentCardPlayField.GetValue(hand) : null;
            var mode = hand != null && HandCurrentModeField != null
                ? HandCurrentModeField.GetValue(hand) : null;
            var actionsDisabled = CombatManager.Instance != null && PlayerActionsDisabledProp != null
                ? PlayerActionsDisabledProp.GetValue(CombatManager.Instance) : null;
            Log.Write($"Snapshot taken. Undo stack: {UndoStack.Count} | inTransition={inTransition} aqEmpty={aqEmpty} playQueue={pqCount} currentCardPlay={currentPlay != null} mode={mode} actionsDisabled={actionsDisabled}");
        }
        catch { Log.Write($"Snapshot taken. Undo stack: {UndoStack.Count}"); }
    }

    public static void Undo()
    {
        // Skip FAILED sentinels
        while (UndoStack.Count > 0 && UndoStack[^1].IsFailed)
        {
            UndoStack.RemoveAt(UndoStack.Count - 1);
            Log.Write("Undo: skipping FAILED sentinel");
        }
        if (UndoStack.Count == 0) return;
        Log.Write($">>> UNDO pressed. Undo stack: {UndoStack.Count}, Redo stack: {RedoStack.Count}");

        // Save current state for redo
        var current = CombatSnapshot.Capture();
        if (current != null)
            RedoStack.Add(current);

        // Pop from end of undo stack
        var previous = UndoStack[^1];
        UndoStack.RemoveAt(UndoStack.Count - 1);
        IsRestoring = true;
        try
        {
            previous.Restore();
            RefreshAllVisuals();
            PendingStartTurnRecoveryCheck = true;
            Log.Write($">>> UNDO complete. Undo stack: {UndoStack.Count}, Redo stack: {RedoStack.Count}");
        }
        catch (Exception ex) { Log.Write($">>> UNDO ERROR: {ex}"); }
        finally
        {
            IsRestoring = false;
        }
    }

    public static void Redo()
    {
        // Skip FAILED sentinels
        while (RedoStack.Count > 0 && RedoStack[^1].IsFailed)
        {
            RedoStack.RemoveAt(RedoStack.Count - 1);
            Log.Write("Redo: skipping FAILED sentinel");
        }
        if (RedoStack.Count == 0) return;
        Log.Write($">>> REDO pressed. Undo stack: {UndoStack.Count}, Redo stack: {RedoStack.Count}");

        // Save current state for undo
        var current = CombatSnapshot.Capture();
        if (current != null)
            UndoStack.Add(current);

        // Pop from end of redo stack
        var next = RedoStack[^1];
        RedoStack.RemoveAt(RedoStack.Count - 1);
        IsRestoring = true;
        try
        {
            next.Restore();
            RefreshAllVisuals();
            PendingStartTurnRecoveryCheck = true;
            Log.Write($">>> REDO complete. Undo stack: {UndoStack.Count}, Redo stack: {RedoStack.Count}");
        }
        catch (Exception ex) { Log.Write($">>> REDO ERROR: {ex}"); }
        finally
        {
            IsRestoring = false;
        }
    }

    public static void ClearStacks()
    {
        UndoStack.Clear();
        RedoStack.Clear();
        PendingStartTurnRecoveryCheck = false;
    }

    private static readonly System.Reflection.FieldInfo CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");
    private static readonly System.Reflection.FieldInfo? CombatManagerCombatCtsField =
        AccessTools.Field(typeof(CombatManager), "_combatCts");

    private static readonly System.Reflection.MethodInfo NotifyCombatStateChangedMethod =
        AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged");

    // Card holder animation snapping (lazy-initialized from instance type)
    private static Type? _holderType;
    private static System.Reflection.FieldInfo? HolderTargetPosField;
    private static System.Reflection.FieldInfo? HolderPosCancelField;
    private static System.Reflection.FieldInfo? HolderTargetAngleField;
    private static System.Reflection.FieldInfo? HolderTargetScaleField;
    private static System.Reflection.MethodInfo? SetAngleInstantlyMethod;
    private static System.Reflection.MethodInfo? SetScaleInstantlyMethod;

    private static void InitHolderReflection()
    {
        if (_holderType != null) return;
        var hand = NPlayerHand.Instance;
        if (hand == null || hand.ActiveHolders.Count == 0) return;
        _holderType = hand.ActiveHolders[0].GetType();
        HolderTargetPosField = AccessTools.Field(_holderType, "_targetPosition");
        HolderPosCancelField = AccessTools.Field(_holderType, "_positionCancelToken");
        HolderTargetAngleField = AccessTools.Field(_holderType, "_targetAngle");
        HolderTargetScaleField = AccessTools.Field(_holderType, "_targetScale");
        SetAngleInstantlyMethod = AccessTools.Method(_holderType, "SetAngleInstantly");
        SetScaleInstantlyMethod = AccessTools.Method(_holderType, "SetScaleInstantly");
    }

    // End turn state reset — CombatManager
    private static readonly System.Reflection.FieldInfo? PlayersReadyToEndTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");
    private static readonly System.Reflection.PropertyInfo? PlayerActionsDisabledProp =
        AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled");
    private static readonly System.Reflection.PropertyInfo? IsPlayPhaseProp =
        AccessTools.Property(typeof(CombatManager), "IsPlayPhase");
    private static readonly System.Reflection.PropertyInfo? EndingPhaseOneProp =
        AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseOne");
    private static readonly System.Reflection.PropertyInfo? EndingPhaseTwoProp =
        AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseTwo");
    private static readonly System.Reflection.PropertyInfo? IsEnemyTurnStartedProp =
        AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted");
    private static readonly System.Reflection.FieldInfo? PlayersReadyToBeginEnemyTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToBeginEnemyTurn");

    // End turn state reset — NPlayerHand
    private static readonly System.Reflection.FieldInfo? HandCurrentCardPlayField =
        AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    private static readonly System.Reflection.FieldInfo? HandCurrentModeField =
        AccessTools.Field(typeof(NPlayerHand), "_currentMode");
    private static readonly System.Reflection.FieldInfo? HandDraggedHolderIndexField =
        AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex");
    private static readonly System.Reflection.FieldInfo? HandHoldersAwaitingQueueField =
        AccessTools.Field(typeof(NPlayerHand), "_holdersAwaitingQueue");
    private static readonly System.Reflection.FieldInfo? HandIsDisabledField =
        AccessTools.Field(typeof(NPlayerHand), "_isDisabled");

    // Potion visual refresh
    private static readonly Type? NPotionContainerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer");
    private static readonly Type? NPotionHolderType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder");
    private static readonly Type? NPotionType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotion");
    private static readonly System.Reflection.FieldInfo? ContainerHoldersField =
        NPotionContainerType != null ? AccessTools.Field(NPotionContainerType, "_holders") : null;
    private static readonly System.Reflection.PropertyInfo? HolderPotionProp =
        NPotionHolderType != null ? AccessTools.Property(NPotionHolderType, "Potion") : null;
    private static readonly System.Reflection.MethodInfo? HolderAddPotionMethod =
        NPotionHolderType != null ? AccessTools.Method(NPotionHolderType, "AddPotion") : null;
    private static readonly System.Reflection.MethodInfo? NPotionCreateMethod =
        NPotionType != null ? AccessTools.Method(NPotionType, "Create",
            new[] { typeof(PotionModel) }) : null;
    // Holder internal state for cleanup
    private static readonly System.Reflection.FieldInfo? HolderPotionBackingField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "<Potion>k__BackingField") : null;
    private static readonly System.Reflection.FieldInfo? HolderDisabledField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "_disabledUntilPotionRemoved") : null;
    private static readonly System.Reflection.FieldInfo? HolderEmptyIconField =
        NPotionHolderType != null ? AccessTools.Field(NPotionHolderType, "_emptyIcon") : null;

    // Power visual refresh
    private static readonly Type? NPowerContainerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPowerContainer");
    private static readonly Type? NPowerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPower");
    private static readonly Type? NCreatureStateDisplayType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay");
    private static readonly System.Reflection.FieldInfo? StateDisplayPowerContainerField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_powerContainer") : null;
    private static readonly System.Reflection.FieldInfo? PowerContainerNodesField =
        NPowerContainerType != null ? AccessTools.Field(NPowerContainerType, "_powerNodes") : null;
    private static readonly System.Reflection.MethodInfo? PowerContainerAddMethod =
        NPowerContainerType != null ? AccessTools.Method(NPowerContainerType, "Add",
            new[] { typeof(PowerModel) }) : null;
    private static readonly System.Reflection.MethodInfo? PowerContainerUpdatePositionsMethod =
        NPowerContainerType != null ? AccessTools.Method(NPowerContainerType, "UpdatePositions") : null;

    // NCardPlayQueue cleanup — stale entries can cause tween hangs
    private static readonly System.Reflection.FieldInfo? PlayQueueField =
        AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");

    // Orb visual refresh — NOrbManager internals
    private static readonly System.Reflection.FieldInfo? NOrbManagerOrbsField =
        AccessTools.Field(typeof(NOrbManager), "_orbs");
    private static readonly System.Reflection.FieldInfo? NOrbManagerContainerField =
        AccessTools.Field(typeof(NOrbManager), "_orbContainer");
    private static readonly System.Reflection.FieldInfo? NOrbManagerTweenField =
        AccessTools.Field(typeof(NOrbManager), "_curTween");
    private static readonly System.Reflection.MethodInfo? NOrbManagerTweenLayoutMethod =
        AccessTools.Method(typeof(NOrbManager), "TweenLayout");
    private static readonly System.Reflection.MethodInfo? NOrbManagerUpdateNavMethod =
        AccessTools.Method(typeof(NOrbManager), "UpdateControllerNavigation");

    // ── Animation Snapping: creature visuals ──

    // NCreature intent fade tween
    private static readonly System.Reflection.FieldInfo? NCreatureIntentFadeTweenField =
        AccessTools.Field(typeof(NCreature), "_intentFadeTween");

    // NCreatureStateDisplay — health bar show/hide tween, original position, health bar ref
    private static readonly System.Reflection.FieldInfo? StateDisplayShowHideTweenField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_showHideTween") : null;
    private static readonly System.Reflection.FieldInfo? StateDisplayOriginalPositionField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_originalPosition") : null;
    private static readonly System.Reflection.FieldInfo? StateDisplayHealthBarField =
        NCreatureStateDisplayType != null ? AccessTools.Field(NCreatureStateDisplayType, "_healthBar") : null;

    // NHealthBar — tweens, block container, HP middleground, refresh
    private static readonly Type? NHealthBarType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NHealthBar");
    private static readonly System.Reflection.FieldInfo? HealthBarBlockTweenField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_blockTween") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarMiddlegroundTweenField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_middlegroundTween") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarBlockContainerField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_blockContainer") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarOriginalBlockPosField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_originalBlockPosition") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarCurrentHpRefreshField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_currentHpOnLastRefresh") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarMaxHpRefreshField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_maxHpOnLastRefresh") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarHpMiddlegroundField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_hpMiddleground") : null;
    private static readonly System.Reflection.FieldInfo? HealthBarHpForegroundField =
        NHealthBarType != null ? AccessTools.Field(NHealthBarType, "_hpForeground") : null;
    private static readonly System.Reflection.MethodInfo? HealthBarRefreshValuesMethod =
        NHealthBarType != null ? AccessTools.Method(NHealthBarType, "RefreshValues") : null;

    // Pile count display sync
    private static readonly Type? NCombatCardPileType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");
    private static readonly System.Reflection.FieldInfo? PileButtonCountField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_currentCount") : null;
    private static readonly System.Reflection.FieldInfo? PileButtonLabelField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_countLabel") : null;
    private static readonly System.Reflection.FieldInfo? PileButtonPileField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_pile") : null;

    internal static bool CanUndoRedo()
    {
        if (IsRestoring) { Log.Write("CanUndoRedo: blocked by IsRestoring"); return false; }
        if (CombatManager.Instance == null) { Log.Write("CanUndoRedo: blocked by CombatManager null"); return false; }

        var cs = GetCombatState();
        if (cs == null) { Log.Write("CanUndoRedo: blocked by CombatState null"); return false; }
        if (cs.CurrentSide != CombatSide.Player) { Log.Write($"CanUndoRedo: blocked by side={cs.CurrentSide}"); return false; }
        if (NGame.Instance?.Transition?.InTransition == true)
        {
            var pq = NCardPlayQueue.Instance;
            var pqCnt = 0;
            if (pq != null && PlayQueueField != null)
            {
                var ql = PlayQueueField.GetValue(pq) as System.Collections.IList;
                pqCnt = ql?.Count ?? 0;
            }
            var h = NPlayerHand.Instance;
            var cp = h != null && HandCurrentCardPlayField != null
                ? HandCurrentCardPlayField.GetValue(h) : null;
            var md = h != null && HandCurrentModeField != null
                ? HandCurrentModeField.GetValue(h) : null;
            var aqE = RunManager.Instance?.ActionQueueSet?.IsEmpty ?? true;
            var ad = CombatManager.Instance != null && PlayerActionsDisabledProp != null
                ? PlayerActionsDisabledProp.GetValue(CombatManager.Instance) : null;
            Log.Write($"CanUndoRedo: blocked by transition | aqEmpty={aqE} playQueue={pqCnt} currentCardPlay={cp != null} mode={md} actionsDisabled={ad}");
            return false;
        }

        var aqSet = RunManager.Instance?.ActionQueueSet;
        if (aqSet == null) { Log.Write("CanUndoRedo: blocked by ActionQueueSet null"); return false; }

        var syncr = RunManager.Instance?.ActionQueueSynchronizer;
        if (syncr == null) { Log.Write("CanUndoRedo: blocked by ActionQueueSynchronizer null"); return false; }
        if (syncr.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase)
        {
            Log.Write($"CanUndoRedo: blocked by combatState={syncr.CombatState}");
            return false;
        }
        if (IsPlayPhaseProp != null && !(bool)(IsPlayPhaseProp.GetValue(CombatManager.Instance) ?? false))
        {
            Log.Write("CanUndoRedo: blocked by IsPlayPhase=false");
            return false;
        }

        if (!aqSet.IsEmpty) { Log.Write("CanUndoRedo: blocked by ActionQueueSet not empty"); return false; }

        // Visual play queue still draining (card fly-to-discard animations)
        var playQueue = NCardPlayQueue.Instance;
        if (playQueue != null && PlayQueueField != null)
        {
            var queueList = PlayQueueField.GetValue(playQueue) as System.Collections.IList;
            if (queueList != null && queueList.Count > 0) { Log.Write($"CanUndoRedo: blocked by playQueue count={queueList.Count}"); return false; }
        }

        return true;
    }

    internal static CombatState? GetCombatState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;
        return CombatManagerStateField?.GetValue(cm) as CombatState;
    }

    internal static void EnsureFreshCombatCancellationSource()
    {
        var cm = CombatManager.Instance;
        if (cm == null || CombatManagerCombatCtsField == null) return;

        var combatCts = CombatManagerCombatCtsField.GetValue(cm)
            as System.Threading.CancellationTokenSource;
        if (combatCts != null && !combatCts.IsCancellationRequested)
            return;

        CombatManagerCombatCtsField.SetValue(cm, new System.Threading.CancellationTokenSource());
        Log.Write("EnsureFreshCombatCancellationSource: created fresh combat CTS");
    }

    private static void RefreshAllVisuals()
    {
        var cs = GetCombatState();
        if (cs == null) return;

        // Reset end turn / player action state so the button works correctly
        ResetEndTurnState();

        // Clean up stale card play visuals (NCardPlayQueue entries with active tweens)
        CleanupCardPlayVisuals();

        // Sync hand card visuals with restored pile contents
        RefreshHandVisuals(cs);

        // Sync Regent's Sovereign Blade orbiting VFX with restored pile contents
        RefreshSovereignBladeVisuals(cs);

        // Snap card holders to final positions instantly (skip animation)
        SnapHandPositions();

        // Refresh monster intent displays
        foreach (var creature in cs.Creatures)
        {
            if (creature.Monster == null) continue;
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature != null)
            {
                _ = nCreature.RefreshIntents(); // fire-and-forget async
            }
        }

        // Refresh power icons on all creatures
        RefreshPowerVisuals(cs);

        // Refresh orb visuals (rebuild NOrb nodes to match restored OrbQueue)
        RefreshOrbVisuals(cs);

        // Refresh potion visuals
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player != null)
                RefreshPotionVisuals(player);
        }

        // Sync pile count displays (draw/discard/exhaust)
        SyncPileCountDisplays();

        // Snap all creature visual animations to final state (health bars, intents, block)
        SnapCreatureVisuals(cs);

        Log.Write("RefreshAllVisuals: all visual refreshes done, notifying StateTracker");
        // Notify all combat UI subscribers to refresh
        // (energy counter, creature HP/block/powers, end turn button, etc.)
        var stateTracker = CombatManager.Instance?.StateTracker;
        if (stateTracker != null)
        {
            NotifyCombatStateChangedMethod?.Invoke(stateTracker, new object[] { "UndoAndRedo" });
        }

        // (TurnStarted event is now fired in CombatSnapshot.Restore as part of play phase init)

        // Deferred refresh of card descriptions so per-turn counters update
        // (NCards added during RefreshHandVisuals may not be IsNodeReady() yet)
        RefreshCardDescriptionsDeferred();
    }

    private static void ResetEndTurnState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;

        // ── CombatManager phase flags ──
        // Force the combat manager into a clean "player turn, play phase" state.
        // Cross-turn undos can leave these flags in wrong states from the future turn.

        // Clear _playersReadyToEndTurn so clicking End Turn dispatches
        // EndPlayerTurnAction instead of UndoEndPlayerTurnAction
        try
        {
            var readySet = PlayersReadyToEndTurnField?.GetValue(cm);
            if (readySet is System.Collections.ICollection col && col.Count > 0)
            {
                Log.Write($"ResetEndTurnState: clearing {col.Count} players from _playersReadyToEndTurn");
                var clearMethod = readySet.GetType().GetMethod("Clear");
                clearMethod?.Invoke(readySet, null);
            }
        }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: clear ready set ERROR: {ex}"); }

        // Clear _playersReadyToBeginEnemyTurn
        try
        {
            var readySet = PlayersReadyToBeginEnemyTurnField?.GetValue(cm);
            if (readySet is System.Collections.ICollection col && col.Count > 0)
            {
                var clearMethod = readySet.GetType().GetMethod("Clear");
                clearMethod?.Invoke(readySet, null);
            }
        }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: clear enemy ready set ERROR: {ex}"); }

        // (ActionQueueSynchronizer/queue pause states are now restored in CombatSnapshot.Restore)

        // Set PlayerActionsDisabled = false (uses property setter to fire event)
        try
        {
            if (PlayerActionsDisabledProp != null)
            {
                var current = (bool)(PlayerActionsDisabledProp.GetValue(cm) ?? false);
                if (current)
                {
                    Log.Write("ResetEndTurnState: clearing PlayerActionsDisabled");
                    PlayerActionsDisabledProp.SetValue(cm, false);
                }
            }
        }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: PlayerActionsDisabled ERROR: {ex}"); }

        // Ensure IsPlayPhase = true (false would prevent card playing)
        try { IsPlayPhaseProp?.SetValue(cm, true); }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: IsPlayPhase ERROR: {ex}"); }

        // Clear ending-turn phase flags
        try { EndingPhaseOneProp?.SetValue(cm, false); }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: EndingPhaseOne ERROR: {ex}"); }
        try { EndingPhaseTwoProp?.SetValue(cm, false); }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: EndingPhaseTwo ERROR: {ex}"); }

        // Clear IsEnemyTurnStarted
        try { IsEnemyTurnStartedProp?.SetValue(cm, false); }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: IsEnemyTurnStarted ERROR: {ex}"); }

        // ── NPlayerHand interaction state ──
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        try
        {
            // Clear _currentCardPlay so InCardPlay returns false.
            // Also QueueFree the NCardPlay node (and its child NCard) to remove
            // the "stuck in air" visual that blocks mouse input.
            if (HandCurrentCardPlayField != null)
            {
                var currentPlay = HandCurrentCardPlayField.GetValue(hand);
                if (currentPlay != null)
                {
                    Log.Write("ResetEndTurnState: clearing _currentCardPlay and removing visual");
                    HandCurrentCardPlayField.SetValue(hand, null);

                    // QueueFree the NCardPlay node — it contains the floating NCard
                    if (currentPlay is Node playNode && GodotObject.IsInstanceValid(playNode))
                    {
                        // Kill any active tween first
                        var tweenField = AccessTools.Field(playNode.GetType(), "_tween");
                        if (tweenField != null)
                        {
                            var tween = tweenField.GetValue(playNode) as Tween;
                            if (tween != null && tween.IsValid()) tween.Kill();
                        }
                        playNode.QueueFree();
                        Log.Write("ResetEndTurnState: QueueFree'd NCardPlay node");
                    }
                }
            }

            // Ensure CurrentMode is Play
            if (HandCurrentModeField != null)
            {
                var mode = HandCurrentModeField.GetValue(hand);
                if (mode != null && (int)mode != (int)NPlayerHand.Mode.Play)
                {
                    Log.Write($"ResetEndTurnState: resetting CurrentMode from {mode} to Play");
                    HandCurrentModeField.SetValue(hand, NPlayerHand.Mode.Play);
                }
            }

            // Reset drag state so no card appears mid-drag
            HandDraggedHolderIndexField?.SetValue(hand, -1);

            // Clear holders awaiting queue (cards queued for play)
            if (HandHoldersAwaitingQueueField?.GetValue(hand) is System.Collections.IDictionary awaitingQueue
                && awaitingQueue.Count > 0)
            {
                Log.Write($"ResetEndTurnState: clearing {awaitingQueue.Count} holdersAwaitingQueue entries");
                awaitingQueue.Clear();
            }

            // Force hand to enabled state (undo always targets player turn)
            if (HandIsDisabledField != null)
            {
                var isDisabled = (bool)(HandIsDisabledField.GetValue(hand) ?? false);
                if (isDisabled)
                {
                    Log.Write("ResetEndTurnState: forcing hand to enabled state");
                    HandIsDisabledField.SetValue(hand, false);
                    // Reset position and modulate to enabled state
                    ((Control)hand).Modulate = Colors.White;
                }
            }
        }
        catch (Exception ex) { Log.Write($"ResetEndTurnState: hand state ERROR: {ex}"); }
    }

    private static void RefreshCardDescriptionsDeferred()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        // Use CallDeferred to ensure NCards are fully in the tree before updating
        Callable.From(() =>
        {
            try
            {
                foreach (var holder in hand.ActiveHolders)
                {
                    holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
                }
            }
            catch (Exception ex) { Log.Write($"RefreshCardDescriptionsDeferred ERROR: {ex}"); }
        }).CallDeferred();

        // Second pass after one full frame — catches cost modifiers (like VoidForm)
        // that depend on power state which may not be fully synced during CallDeferred.
        _ = RefreshCardVisualsNextFrame(hand);
    }

    private static async Task RefreshCardVisualsNextFrame(NPlayerHand hand)
    {
        try
        {
            await hand.ToSignal(hand.GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var holder in hand.ActiveHolders)
            {
                holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }
        }
        catch (Exception ex) { Log.Write($"RefreshCardVisualsNextFrame ERROR: {ex}"); }
    }

    private static void RefreshHandVisuals(CombatState cs)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        // Find the hand pile from player combat state
        List<CardModel>? restoredHandCards = null;
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;
            foreach (var pile in player.PlayerCombatState.AllPiles)
            {
                if (pile.Type == PileType.Hand)
                {
                    restoredHandCards = pile.Cards.ToList();
                    break;
                }
            }
            if (restoredHandCards != null) break;
        }
        if (restoredHandCards == null) return;

        // Collect current visual cards, then clear all and recreate in correct order.
        // This ensures card ordering matches the restored pile order exactly.
        var currentVisualCards = new List<CardModel>();
        foreach (var holder in hand.ActiveHolders)
            currentVisualCards.Add(holder.CardNode.Model);

        // Remove all current visual cards
        foreach (var card in currentVisualCards)
            hand.Remove(card);

        // Re-create visual cards in correct restored order
        for (int i = 0; i < restoredHandCards.Count; i++)
        {
            var nCard = NCard.Create(restoredHandCards[i], ModelVisibility.Visible);
            nCard.Scale = Vector2.One; // Prevent scale tween during undo
            hand.Add(nCard, i);
        }

        hand.ForceRefreshCardIndices();
        Log.Write($"RefreshHandVisuals: visual={currentVisualCards.Count} restored={restoredHandCards.Count}");
    }

    private static void RefreshSovereignBladeVisuals(CombatState cs)
    {
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var nCreature = NCombatRoom.Instance?.GetCreatureNode(player.Creature);
            if (nCreature == null) continue;

            var desiredBlades = new List<SovereignBlade>();
            foreach (var pile in player.PlayerCombatState.AllPiles)
            {
                foreach (var card in pile.Cards)
                {
                    if (!card.IsDupe && card is SovereignBlade blade)
                        desiredBlades.Add(blade);
                }
            }

            var desiredBladeSet = new HashSet<CardModel>(desiredBlades);
            var activeBladeCards = new HashSet<CardModel>();
            var bladeNodes = new List<NSovereignBladeVfx>();

            foreach (var child in nCreature.GetChildren())
            {
                if (child is NSovereignBladeVfx bladeNode)
                    bladeNodes.Add(bladeNode);
            }

            foreach (var bladeNode in bladeNodes)
            {
                var bladeCard = bladeNode.Card;
                if (!desiredBladeSet.Contains(bladeCard) || !activeBladeCards.Add(bladeCard))
                {
                    nCreature.RemoveChild(bladeNode);
                    bladeNode.QueueFree();
                }
            }

            foreach (var blade in desiredBlades)
            {
                if (activeBladeCards.Contains(blade))
                    continue;

                var bladeNode = NSovereignBladeVfx.Create(blade);
                if (bladeNode == null)
                    continue;

                nCreature.AddChild(bladeNode);
                bladeNode.Position = Vector2.Zero;
                bladeNode.Forge(blade.DynamicVars.Damage.IntValue, false);
                activeBladeCards.Add(blade);
            }

            for (int i = 0; i < desiredBlades.Count; i++)
            {
                var bladeNode = SovereignBlade.GetVfxNode(player, desiredBlades[i]);
                if (bladeNode != null)
                    bladeNode.OrbitProgress = (float)i / desiredBlades.Count;
            }

            Log.Write($"RefreshSovereignBladeVisuals: desired={desiredBlades.Count} active={activeBladeCards.Count}");
        }
    }

    private static void SnapHandPositions()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || hand.ActiveHolders.Count == 0) return;

        InitHolderReflection();
        if (_holderType == null) return;

        foreach (var holder in hand.ActiveHolders)
        {
            // Cancel running position animation
            var cancel = HolderPosCancelField?.GetValue(holder)
                as System.Threading.CancellationTokenSource;
            cancel?.Cancel();

            // Snap position to target
            if (HolderTargetPosField != null)
                ((Control)holder).Position = (Vector2)HolderTargetPosField.GetValue(holder)!;

            // Snap angle and scale instantly via reflection
            if (HolderTargetAngleField != null && SetAngleInstantlyMethod != null)
                SetAngleInstantlyMethod.Invoke(holder,
                    new object[] { (float)HolderTargetAngleField.GetValue(holder)! });
            if (HolderTargetScaleField != null && SetScaleInstantlyMethod != null)
                SetScaleInstantlyMethod.Invoke(holder,
                    new object[] { (Vector2)HolderTargetScaleField.GetValue(holder)! });
        }
    }

    private static void RefreshPowerVisuals(CombatState cs)
    {
        if (NPowerContainerType == null || PowerContainerNodesField == null ||
            PowerContainerAddMethod == null || NPowerType == null) return;

        foreach (var creature in cs.Creatures)
        {
            try
            {
                var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
                if (nCreature == null) continue;

                // Navigate: NCreature → NCreatureStateDisplay → NPowerContainer
                var stateDisplay = FindNodeOfType(nCreature, NCreatureStateDisplayType?.Name ?? "NCreatureStateDisplay");
                if (stateDisplay == null) continue;

                var container = StateDisplayPowerContainerField?.GetValue(stateDisplay);
                if (container == null)
                {
                    // Fallback: search recursively for the NPowerContainer
                    container = FindNodeOfType(nCreature, NPowerContainerType.Name);
                }
                if (container == null) continue;

                // Step 1: Clear all existing NPower nodes
                var powerNodes = PowerContainerNodesField.GetValue(container) as System.Collections.IList;
                if (powerNodes != null)
                {
                    foreach (var node in powerNodes)
                    {
                        if (node is Node godotNode)
                            godotNode.QueueFree();
                    }
                    powerNodes.Clear();
                }

                // Step 2: Re-add NPower nodes for each current power
                foreach (var power in creature.Powers)
                {
                    PowerContainerAddMethod.Invoke(container, new object[] { power });
                }

                Log.Write($"RefreshPowerVisuals: creature {creature.CombatId} rebuilt {creature.Powers.Count} powers");
            }
            catch (Exception ex)
            {
                Log.Write($"RefreshPowerVisuals: creature {creature.CombatId} ERROR: {ex}");
            }
        }
    }

    private static void RefreshOrbVisuals(CombatState cs)
    {
        if (NOrbManagerOrbsField == null || NOrbManagerContainerField == null) return;

        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var nCreature = NCombatRoom.Instance?.GetCreatureNode(ally);
            if (nCreature == null) continue;

            var orbManager = nCreature.OrbManager;
            if (orbManager == null) continue;

            var orbQueue = player.PlayerCombatState.OrbQueue;
            if (orbQueue == null) continue;

            try
            {
                // Kill any active tween
                var tween = NOrbManagerTweenField?.GetValue(orbManager) as Tween;
                if (tween != null && tween.IsValid()) tween.Kill();

                // Get internal lists
                var nOrbsList = NOrbManagerOrbsField.GetValue(orbManager) as System.Collections.IList;
                var container = NOrbManagerContainerField.GetValue(orbManager) as Control;
                if (nOrbsList == null || container == null) continue;

                // QueueFree all existing NOrb visual nodes
                foreach (var nOrb in nOrbsList)
                {
                    if (nOrb is Node node) node.QueueFree();
                }
                nOrbsList.Clear();

                bool isLocal = orbManager.IsLocal;
                var restoredOrbs = orbQueue.Orbs.ToList();
                int capacity = orbQueue.Capacity;

                // Re-create NOrb nodes for filled orb slots
                for (int i = 0; i < restoredOrbs.Count; i++)
                {
                    var nOrb = NOrb.Create(isLocal, restoredOrbs[i]);
                    container.AddChild(nOrb);
                    nOrbsList.Add(nOrb);
                    nOrb.Position = Vector2.Zero;
                }

                // Re-create empty slot NOrb nodes
                for (int i = restoredOrbs.Count; i < capacity; i++)
                {
                    var nOrb = NOrb.Create(isLocal);
                    container.AddChild(nOrb);
                    nOrbsList.Add(nOrb);
                    nOrb.Position = Vector2.Zero;
                }

                // Animate layout and update navigation
                NOrbManagerTweenLayoutMethod?.Invoke(orbManager, null);
                NOrbManagerUpdateNavMethod?.Invoke(orbManager, null);

                // Kill the layout tween and snap orb positions immediately
                var tweenAfter = NOrbManagerTweenField?.GetValue(orbManager) as Tween;
                if (tweenAfter != null && tweenAfter.IsValid()) tweenAfter.Kill();
                if (capacity > 0)
                {
                    float arcAngle = 125f;
                    float angleStep = arcAngle / (float)(capacity - 1);
                    float radius = Mathf.Lerp(225f, 300f, ((float)capacity - 3f) / 7f);
                    if (!isLocal) radius *= 0.75f;
                    float curAngle = arcAngle;
                    for (int i = 0; i < nOrbsList.Count && i < capacity; i++)
                    {
                        float s = Mathf.DegToRad(-25f - curAngle);
                        var finalPos = new Vector2(-Mathf.Cos(s), Mathf.Sin(s)) * radius;
                        if (nOrbsList[i] is NOrb nOrbItem)
                            nOrbItem.Position = finalPos;
                        curAngle -= angleStep;
                    }
                }

                // Defer UpdateVisuals on each NOrb so sprites are created after nodes enter tree
                Callable.From(() =>
                {
                    try
                    {
                        var orbsAfter = NOrbManagerOrbsField?.GetValue(orbManager) as System.Collections.IList;
                        if (orbsAfter == null) return;
                        foreach (var item in orbsAfter)
                        {
                            if (item is NOrb nOrbNode)
                                nOrbNode.UpdateVisuals(false);
                        }
                    }
                    catch (Exception ex) { Log.Write($"RefreshOrbVisuals deferred ERROR: {ex}"); }
                }).CallDeferred();

                Log.Write($"RefreshOrbVisuals: rebuilt {restoredOrbs.Count} orbs + {capacity - restoredOrbs.Count} empty slots");
            }
            catch (Exception ex) { Log.Write($"RefreshOrbVisuals ERROR: {ex}"); }
        }
    }

    private static void RefreshPotionVisuals(Player player)
    {
        var nRun = NRun.Instance;
        if (nRun == null || NPotionContainerType == null) return;

        var container = FindNodeOfType(nRun, NPotionContainerType.Name);
        if (container == null) return;

        var holders = ContainerHoldersField?.GetValue(container) as System.Collections.IList;
        if (holders == null) return;

        for (int i = 0; i < holders.Count && i < player.PotionSlots.Count; i++)
        {
            var holder = (Node)holders[i]!;
            var desiredPotion = player.PotionSlots[i];

            try
            {
                // Step 1: Forcibly clean up any existing visual potion on this holder
                // Kill all tweens to stop any in-flight animations (discard/remove animations)
                foreach (var child in holder.GetChildren())
                {
                    if (NPotionType != null && NPotionType.IsInstanceOfType(child))
                    {
                        holder.RemoveChild(child);
                        ((Node)child).QueueFree();
                    }
                }
                // Reset holder state
                HolderPotionBackingField?.SetValue(holder, null);
                HolderDisabledField?.SetValue(holder, false);
                ((Control)holder).Modulate = Colors.White;
                // Show empty icon
                var emptyIcon = HolderEmptyIconField?.GetValue(holder) as Control;
                if (emptyIcon != null) emptyIcon.Modulate = Colors.White;

                // Step 2: If this slot should have a potion, create and add it
                if (desiredPotion != null)
                {
                    var nPotion = NPotionCreateMethod?.Invoke(null, new object[] { desiredPotion });
                    if (nPotion != null)
                    {
                        ((Node)nPotion).Set("position", new Vector2(-30f, -30f));
                        HolderAddPotionMethod?.Invoke(holder, new[] { nPotion });
                        Log.Write($"RefreshPotionVisuals: slot[{i}] added {desiredPotion.Id}");
                    }
                }
            }
            catch (Exception ex) { Log.Write($"RefreshPotionVisuals: slot[{i}] ERROR: {ex}"); }
        }
    }

    private static void SyncPileCountDisplays()
    {
        if (NCombatCardPileType == null || PileButtonCountField == null ||
            PileButtonLabelField == null || PileButtonPileField == null) return;

        var combatRoom = NCombatRoom.Instance;
        if (combatRoom == null) return;

        // Find all NCombatCardPile nodes in the scene tree
        SyncPileCountsRecursive(combatRoom);
    }

    private static void SyncPileCountsRecursive(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (NCombatCardPileType!.IsInstanceOfType(child))
            {
                try
                {
                    var pile = PileButtonPileField!.GetValue(child) as CardPile;
                    if (pile != null)
                    {
                        int actualCount = pile.Cards.Count;
                        PileButtonCountField!.SetValue(child, actualCount);
                        // Update the label text via the MegaLabel
                        var label = PileButtonLabelField!.GetValue(child);
                        if (label != null)
                        {
                            var setTextMethod = AccessTools.Method(label.GetType(), "SetTextAutoSize");
                            setTextMethod?.Invoke(label, new object[] { actualCount.ToString() });
                        }
                    }
                }
                catch (Exception ex) { Log.Write($"SyncPileCount ERROR: {ex}"); }
            }
            SyncPileCountsRecursive(child);
        }
    }

    private static void CleanupCardPlayVisuals()
    {
        try
        {
            var playQueue = NCardPlayQueue.Instance;
            if (playQueue == null || PlayQueueField == null) return;

            var queueList = PlayQueueField.GetValue(playQueue) as System.Collections.IList;
            if (queueList == null || queueList.Count == 0) return;

            Log.Write($"CleanupCardPlayVisuals: clearing {queueList.Count} stale play queue entries");

            foreach (var item in queueList)
            {
                if (item == null) continue;
                var itemType = item.GetType();

                // Kill active tween to prevent Finished signal hang
                var tweenField = AccessTools.Field(itemType, "currentTween");
                if (tweenField != null)
                {
                    var tween = tweenField.GetValue(item) as Tween;
                    if (tween != null && tween.IsValid())
                        tween.Kill();
                }

                // QueueFree the orphaned NCard
                var cardField = AccessTools.Field(itemType, "card");
                if (cardField != null)
                {
                    var nCard = cardField.GetValue(item) as NCard;
                    if (nCard != null && nCard.IsInsideTree())
                        nCard.QueueFree();
                }
            }

            queueList.Clear();
        }
        catch (Exception ex) { Log.Write($"CleanupCardPlayVisuals ERROR: {ex}"); }
    }

    private static void SnapCreatureVisuals(CombatState cs)
    {
        foreach (var creature in cs.Creatures)
        {
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature == null) continue;

            // 1. Snap intent alpha to 1.0 immediately (kill fade tween)
            try
            {
                var intentTween = NCreatureIntentFadeTweenField?.GetValue(nCreature) as Tween;
                if (intentTween != null && intentTween.IsValid()) intentTween.Kill();
                nCreature.IntentContainer.Modulate = Colors.White;
            }
            catch (Exception ex) { Log.Write($"SnapCreatureVisuals intent {creature.CombatId} ERROR: {ex.Message}"); }

            // 2. Snap NCreatureStateDisplay (health bar area)
            try
            {
                var stateDisplay = FindNodeOfType(nCreature,
                    NCreatureStateDisplayType?.Name ?? "NCreatureStateDisplay");
                if (stateDisplay == null) continue;

                // Kill show/hide tween, snap to fully visible at original position
                var showHideTween = StateDisplayShowHideTweenField?.GetValue(stateDisplay) as Tween;
                if (showHideTween != null && showHideTween.IsValid()) showHideTween.Kill();
                ((Control)stateDisplay).Modulate = Colors.White;
                if (StateDisplayOriginalPositionField?.GetValue(stateDisplay) is Vector2 origPos)
                    ((Control)stateDisplay).Position = origPos;

                // 3. Snap NHealthBar tweens
                var healthBar = StateDisplayHealthBarField?.GetValue(stateDisplay);
                if (healthBar == null) continue;

                // Kill middleground tween (HP drain animation)
                var mgTween = HealthBarMiddlegroundTweenField?.GetValue(healthBar) as Tween;
                if (mgTween != null && mgTween.IsValid()) mgTween.Kill();

                // Kill block tween (block icon bounce-in animation)
                var blockTween = HealthBarBlockTweenField?.GetValue(healthBar) as Tween;
                if (blockTween != null && blockTween.IsValid()) blockTween.Kill();

                // Snap block container to original position with full alpha
                var blockContainer = HealthBarBlockContainerField?.GetValue(healthBar) as Control;
                if (blockContainer != null && HealthBarOriginalBlockPosField?.GetValue(healthBar) is Vector2 blockPos)
                {
                    blockContainer.Position = blockPos;
                    blockContainer.Modulate = Colors.White;
                }

                // Reset cached HP values so RefreshValues does a full recalc
                HealthBarCurrentHpRefreshField?.SetValue(healthBar, -1);
                HealthBarMaxHpRefreshField?.SetValue(healthBar, -1);

                // Force full refresh of health bar
                HealthBarRefreshValuesMethod?.Invoke(healthBar, null);

                // Kill the middleground tween AGAIN (RefreshValues creates one)
                // and snap middleground to match foreground
                var mgTween2 = HealthBarMiddlegroundTweenField?.GetValue(healthBar) as Tween;
                if (mgTween2 != null && mgTween2.IsValid()) mgTween2.Kill();

                var hpMiddleground = HealthBarHpMiddlegroundField?.GetValue(healthBar) as Control;
                var hpForeground = HealthBarHpForegroundField?.GetValue(healthBar) as Control;
                if (hpMiddleground != null && hpForeground != null)
                    hpMiddleground.OffsetRight = hpForeground.OffsetRight - 2f;
            }
            catch (Exception ex) { Log.Write($"SnapCreatureVisuals healthbar {creature.CombatId} ERROR: {ex.Message}"); }
        }
        Log.Write($"SnapCreatureVisuals: snapped {cs.Creatures.Count} creatures");
    }

    private static Node? FindNodeOfType(Node parent, string typeName)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child.GetType().Name == typeName) return child;
            var found = FindNodeOfType(child, typeName);
            if (found != null) return found;
        }
        return null;
    }
}

// Capture input: Left Arrow = undo, Right Arrow = redo
[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchNGameInput
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode == Key.Left || key.Keycode == Key.Right)
            Log.Write($"InputPatch: key={key.Keycode}");

        if (!UndoAndRedoMod.CanUndoRedo())
            return;

        switch (key.Keycode)
        {
            case Key.Left:
                UndoAndRedoMod.Undo();
                break;
            case Key.Right:
                UndoAndRedoMod.Redo();
                break;
        }
    }
}

// Take snapshot before player plays a card
[HarmonyPatch(typeof(PlayCardAction), MethodType.Constructor,
    new[] { typeof(CardModel), typeof(Creature) })]
public static class PatchPlayCardSnapshot
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoAndRedoMod.TakeSnapshot();
    }
}

// Take snapshot before player ends turn
[HarmonyPatch(typeof(EndPlayerTurnAction), MethodType.Constructor,
    new[] { typeof(Player), typeof(int) })]
public static class PatchEndTurnSnapshot
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoAndRedoMod.TakeSnapshot();
        UndoAndRedoMod.EnsureFreshCombatCancellationSource();
    }
}

// Take snapshot before player uses a potion
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.GameActions.UsePotionAction), MethodType.Constructor,
    new[] { typeof(MegaCrit.Sts2.Core.Models.PotionModel), typeof(Creature), typeof(bool) })]
public static class PatchUsePotionSnapshot
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoAndRedoMod.TakeSnapshot();
    }
}

// Take snapshot before player discards a potion
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.GameActions.DiscardPotionGameAction), MethodType.Constructor,
    new[] { typeof(Player), typeof(uint), typeof(bool) })]
public static class PatchDiscardPotionSnapshot
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        UndoAndRedoMod.TakeSnapshot();
    }
}

// Clear stacks when combat ends
[HarmonyPatch(typeof(CombatManager), "Reset", new[] { typeof(bool) })]
public static class PatchCombatReset
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        UndoAndRedoMod.ClearStacks();
    }
}

// Compensate for broken turn lifecycle + schedule delayed PlayPhase init
[HarmonyPatch(typeof(CombatManager), "StartTurn")]
public static class PatchStartTurn
{
    private static readonly System.Reflection.MethodInfo RunAutoPrePlayPhaseMethod =
        AccessTools.Method(typeof(CombatManager), "RunAutoPrePlayPhase",
            new[] { typeof(HookPlayerChoiceContext), typeof(Task), typeof(Player) })!;

    [HarmonyPrefix]
    public static void Prefix(CombatManager __instance)
    {
        try
        {
            var cs = AccessTools.Field(typeof(CombatManager), "_state")?.GetValue(__instance) as CombatState;
            Log.Write($">>> StartTurn called: side={cs?.CurrentSide} round={cs?.RoundNumber} IsInProgress={__instance.IsInProgress}");

            // Only run the delayed recovery path after an undo/redo restore.
            if (UndoAndRedoMod.PendingStartTurnRecoveryCheck && cs?.CurrentSide == CombatSide.Player)
            {
                UndoAndRedoMod.PendingStartTurnRecoveryCheck = false;
                _ = DelayedPlayPhaseCheck(cs);
            }
        }
        catch { Log.Write(">>> StartTurn called"); }
    }

    private static async Task DelayedPlayPhaseCheck(CombatState cs)
    {
        try
        {
            for (int i = 0; i < 60; i++)
                await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);

            var syncr = RunManager.Instance?.ActionQueueSynchronizer;
            if (syncr == null || cs.CurrentSide != CombatSide.Player) return;
            if (syncr.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.NotPlayPhase) return;

            // StartTurn's async flow hung after an undo/redo restore.
            // Resume the player phase without replaying turn-start hooks, which
            // would double-trigger powers like FurnacePower.
            Log.Write(">>> DelayedPlayPhaseCheck: StartTurn hung, resuming play phase");

            var cm = CombatManager.Instance;
            if (cm == null || !cm.IsInProgress) return;

            // 1. CombatManager.RunAutoPrePlayPhase
            try
            {
                var localNetId = MegaCrit.Sts2.Core.Context.LocalContext.NetId!.Value;
                foreach (var player in cs.Players)
                {
                    if (!player.Creature.IsDead)
                    {
                        var ctx = new HookPlayerChoiceContext(
                            player, localNetId,
                            MegaCrit.Sts2.Core.Entities.Multiplayer.GameActionType.CombatPlayPhaseOnly);
                        Log.Write(">>> DelayedPlayPhaseCheck: calling CombatManager.RunAutoPrePlayPhase");
                        var task = (Task)RunAutoPrePlayPhaseMethod.Invoke(cm, new object?[] { ctx, Task.CompletedTask, player })!;
                        await ctx.AssignTaskAndWaitForPauseOrCompletion(task);
                        Log.Write(">>> DelayedPlayPhaseCheck: CombatManager.RunAutoPrePlayPhase done");
                    }
                }
            }
            catch (Exception ex) { Log.Write($">>> DelayedPlayPhaseCheck: RunAutoPrePlayPhase ERROR: {ex.Message}"); }

            // 2. CheckWinCondition
            try
            {
                Log.Write(">>> DelayedPlayPhaseCheck: calling CheckWinCondition");
                await cm.CheckWinCondition();
                Log.Write($">>> DelayedPlayPhaseCheck: CheckWinCondition done, IsInProgress={cm.IsInProgress}");
            }
            catch (Exception ex) { Log.Write($">>> DelayedPlayPhaseCheck: CheckWinCondition ERROR: {ex.Message}"); }

            // 3. PlayPhase init (same as StartTurn lines 393638-393642)
            if (cm.IsInProgress)
            {
                Log.Write(">>> DelayedPlayPhaseCheck: forcing PlayPhase init");
                RunManager.Instance?.ActionExecutor?.Unpause();
                syncr.SetCombatState(MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase);
                AccessTools.Property(typeof(CombatManager), "IsPlayPhase")?.SetValue(cm, true);
                AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted")?.SetValue(cm, false);
                var del = AccessTools.Field(typeof(CombatManager), "TurnStarted")?.GetValue(cm) as Delegate;
                del?.DynamicInvoke(cs);
                Log.Write(">>> DelayedPlayPhaseCheck: PlayPhase init complete");
            }
        }
        catch (Exception ex) { Log.Write($">>> DelayedPlayPhaseCheck ERROR: {ex.Message}"); }
    }
}


// Log NPlayerHand mode changes to track card selection/targeting flow
[HarmonyPatch(typeof(NPlayerHand), "set_CurrentMode")]
public static class PatchHandModeChanged
{
    [HarmonyPostfix]
    public static void Postfix(NPlayerHand __instance)
    {
        try
        {
            var mode = AccessTools.Field(typeof(NPlayerHand), "_currentMode")?.GetValue(__instance);
            var cp = AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay")?.GetValue(__instance);
            var drag = AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex")?.GetValue(__instance);
            var disabled = AccessTools.Field(typeof(NPlayerHand), "_isDisabled")?.GetValue(__instance);
            Log.Write($"HandMode changed: mode={mode} currentCardPlay={cp != null} dragIdx={drag} handDisabled={disabled}");
        }
        catch { }
    }
}

