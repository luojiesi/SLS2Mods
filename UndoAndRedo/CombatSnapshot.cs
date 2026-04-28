using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo;

internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", "UndoAndRedo.log");

    private static bool _cleared;

    internal static void Write(string msg)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath,
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] === Log cleared (new session) ==={System.Environment.NewLine}");
                _cleared = true;
            }
            System.IO.File.AppendAllText(LogPath,
                $"[{System.DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
        }
        catch { }
    }
}

public class CombatSnapshot
{
    // ── FAILED Sentinel ──
    public bool IsFailed { get; private init; }
    private static readonly CombatSnapshot FailedSentinel = new() { IsFailed = true };

    // ── Data Structures ──

    private record struct CreatureData(
        uint CombatId,
        Creature CreatureRef,
        int CurrentHp,
        int MaxHp,
        int Block,
        List<PowerData> Powers,
        Vector2? VisualGlobalPosition,
        Vector2? VisualBodyScale);

    private record struct PowerData(
        ModelId Id,
        int Amount,
        int AmountOnTurnStart,
        bool SkipNextDurationTick,
        object? InternalData,
        object? FacingDirection);

    private record struct MonsterMoveSnapshot(
        string? NextMoveStateId,
        string? CurrentStateId,
        bool PerformedFirstMove,
        bool SpawnedThisTurn,
        List<string> StateLogIds,
        Dictionary<string, bool> MovePerformedAtLeastOnce);

    private record struct RelicSnapshot(
        ModelId Id,
        int StackCount,
        bool IsWax,
        bool IsMelted,
        object Status,
        object? DynamicVarsClone);

    // ── Snapshot Data ──

    // Creature state
    private readonly List<CreatureData> _creatureStates = new();

    // Card piles (references to preserve NCard visual bindings)
    private readonly Dictionary<PileType, List<CardModel>> _savedPiles = new();
    // Card mutable state (deep clones for restoring modifications)
    private readonly Dictionary<CardModel, CardModel> _cardClones = new();

    // Player combat state
    private int _energy;
    private int _stars;

    // Combat state
    private int _roundNumber;
    private CombatSide _currentSide;

    // Control flow state — these are managed by event chains that undo bypasses
    private MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState _syncCombatState;
    private bool _cmIsPaused;
    private bool _executorIsPaused;
    private readonly List<bool> _actionQueuePauseStates = new(); // per-queue isPaused

    // RNG
    private readonly Dictionary<RunRngType, (uint seed, int counter)> _runRngStates = new();
    private readonly Dictionary<uint, (uint seed, int counter)> _monsterRngStates = new();

    // Monster moves & intents
    private readonly Dictionary<uint, MonsterMoveSnapshot> _monsterMoveStates = new();

    // Orbs — keep original references (like cards) so NOrb visuals stay bound
    private readonly List<OrbModel> _savedOrbRefs = new();
    private readonly Dictionary<OrbModel, OrbModel> _orbClones = new();
    private int _orbCapacity;
    private bool _hasOrbData;

    // Relics
    private readonly List<RelicSnapshot> _relicStates = new();
    // MemberwiseClone of each relic — used to restore subclass-specific private fields
    // (e.g. BrilliantScarf._cardsPlayedThisTurn, VelvetChoker._cardsPlayedThisTurn, etc.)
    private readonly Dictionary<RelicModel, object> _relicClones = new();

    // Pets
    private readonly List<uint> _petCombatIds = new();

    // Potions — preserve original references (like cards), clone for mutable state
    private readonly List<PotionModel?> _potionSlotRefs = new();
    private readonly Dictionary<PotionModel, PotionModel> _potionClones = new();

    // Creature roster (to detect summoned enemies for removal on undo)
    private readonly HashSet<uint> _creatureCombatIds = new();

    // Combat history (shallow copy — entries are immutable records)
    private List<object>? _savedHistoryEntries;

    // Escaped creatures (shallow copy — same references)
    private readonly List<Creature> _escapedCreatures = new();

    // Player gold
    private int _gold;

    // Creature ID allocator
    private uint _nextCreatureId;

    // All cards tracked by CombatState (references, for add/remove sync)
    private readonly List<CardModel> _allCards = new();

    // ── Reflection Caches ──

    // Creature
    private static readonly FieldInfo CreatureHpField =
        AccessTools.Field(typeof(Creature), "_currentHp");
    private static readonly FieldInfo CreatureMaxHpField =
        AccessTools.Field(typeof(Creature), "_maxHp");
    private static readonly FieldInfo CreatureBlockField =
        AccessTools.Field(typeof(Creature), "_block");
    private static readonly FieldInfo CreaturePowersField =
        AccessTools.Field(typeof(Creature), "_powers");

    // Player combat state
    private static readonly FieldInfo PcsEnergyField =
        AccessTools.Field(typeof(PlayerCombatState), "_energy");
    private static readonly FieldInfo PcsStarsField =
        AccessTools.Field(typeof(PlayerCombatState), "_stars");
    private static readonly FieldInfo? PcsPetsField =
        AccessTools.Field(typeof(PlayerCombatState), "_pets");

    // Card pile
    private static readonly FieldInfo CardPileCardsField =
        AccessTools.Field(typeof(CardPile), "_cards");

    // StateTracker card subscription management — CardPile.AddInternal/RemoveInternal
    // call these to register/unregister card event handlers. Since RestoreCardPiles
    // bypasses AddInternal/RemoveInternal, we must call these manually.
    private static readonly MethodInfo? StateTrackerSubscribeCardMethod =
        AccessTools.Method(typeof(CombatStateTracker), "Subscribe", new[] { typeof(CardModel) });
    private static readonly MethodInfo? StateTrackerUnsubscribeCardMethod =
        AccessTools.Method(typeof(CombatStateTracker), "Unsubscribe", new[] { typeof(CardModel) });

    // Powers
    private static readonly FieldInfo PowerAmountField =
        AccessTools.Field(typeof(PowerModel), "_amount");
    private static readonly FieldInfo PowerAmountOnTurnStartField =
        AccessTools.Field(typeof(PowerModel), "_amountOnTurnStart");
    private static readonly FieldInfo PowerSkipField =
        AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    private static readonly FieldInfo? PowerInternalDataField =
        AccessTools.Field(typeof(PowerModel), "_internalData");
    // Used to shallow-clone _internalData objects (e.g. HardenedShellPower.Data)
    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    // CardEnergyCost._card — back-reference from energy cost to its owning card.
    // Must be fixed after restoring card state from a MutableClone, because the clone's
    // _energyCost._card points to the clone rather than the original card.
    private static readonly FieldInfo? EnergyCostCardField =
        AccessTools.Field(typeof(CardEnergyCost), "_card");
    // SurroundedPower._facing — controls character facing direction when enemies are on both sides
    private static readonly Type? SurroundedPowerType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.Powers.SurroundedPower");
    private static readonly FieldInfo? SurroundedFacingField =
        SurroundedPowerType != null ? AccessTools.Field(SurroundedPowerType, "_facing") : null;

    // Monster
    private static readonly FieldInfo MonsterRngField =
        AccessTools.Field(typeof(MonsterModel), "_rng");
    private static readonly FieldInfo? MonsterSpawnedField =
        AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");
    private static readonly FieldInfo? MonsterMoveStateMachineField =
        AccessTools.Field(typeof(MonsterModel), "_moveStateMachine");
    private static readonly MethodInfo? SetMoveImmediateMethod =
        AccessTools.Method(typeof(MonsterModel), "SetMoveImmediate");
    // Direct access to NextMove property (private setter) — bypasses
    // CanTransitionAway check that blocks SetMoveImmediate for moves with
    // MustPerformOnceBeforeTransitioning (e.g. REATTACH_MOVE, RESPAWN_MOVE)
    private static readonly PropertyInfo? NextMoveProp =
        AccessTools.Property(typeof(MonsterModel), "NextMove");

    // Monster move state machine (types resolved by name to avoid namespace import)
    private static readonly Type? SmType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
    private static readonly Type? MoveStateType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState");
    private static readonly FieldInfo? SmCurrentStateField =
        SmType != null ? AccessTools.Field(SmType, "_currentState") : null;
    private static readonly FieldInfo? SmPerformedFirstMoveField =
        SmType != null ? AccessTools.Field(SmType, "_performedFirstMove") : null;
    private static readonly FieldInfo? MoveStatePerformedField =
        MoveStateType != null ? AccessTools.Field(MoveStateType, "_performedAtLeastOnce") : null;

    // Resolved in static ctor
    private static readonly PropertyInfo? MonsterStateIdProperty;
    private static readonly MethodInfo? ForceCurrentStateMethod;

    // Orbs
    private static readonly FieldInfo? OrbQueueOrbsField =
        AccessTools.Field(typeof(OrbQueue), "_orbs");
    private static readonly FieldInfo? OrbQueueCapacityField =
        AccessTools.Field(typeof(OrbQueue), "<Capacity>k__BackingField");

    // Relics
    private static readonly FieldInfo? RelicDynamicVarsField =
        AccessTools.Field(typeof(RelicModel), "_dynamicVars");
    private static readonly PropertyInfo? RelicStatusProperty =
        AccessTools.Property(typeof(RelicModel), "Status");
    private static readonly FieldInfo? RelicStackCountField =
        AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    private static readonly MethodInfo? InvokeDisplayAmountChangedMethod =
        AccessTools.Method(typeof(RelicModel), "InvokeDisplayAmountChanged");

    // Potions
    private static readonly FieldInfo PlayerPotionSlotsField =
        AccessTools.Field(typeof(Player), "_potionSlots");
    private static readonly FieldInfo? PotionRemovedField =
        AccessTools.Field(typeof(PotionModel), "<HasBeenRemovedFromState>k__BackingField");
    private static readonly FieldInfo? PotionOwnerField =
        AccessTools.Field(typeof(PotionModel), "_owner");

    // Combat history — derive type from property to avoid TypeByName failures
    private static readonly System.Reflection.PropertyInfo? CmHistoryProperty =
        AccessTools.Property(typeof(CombatManager), "History");
    private static readonly FieldInfo? HistoryEntriesField =
        CmHistoryProperty?.PropertyType != null
            ? AccessTools.Field(CmHistoryProperty.PropertyType, "_entries")
            : null;

    // Escaped creatures
    private static readonly PropertyInfo? EscapedCreaturesProp =
        AccessTools.Property(typeof(CombatState), "EscapedCreatures");

    // Player gold
    private static readonly FieldInfo PlayerGoldField =
        AccessTools.Field(typeof(Player), "_gold");

    // Creature ID allocator
    private static readonly FieldInfo? NextCreatureIdField =
        AccessTools.Field(typeof(CombatState), "_nextCreatureId");

    // All cards tracked by CombatState
    private static readonly FieldInfo? AllCardsField =
        AccessTools.Field(typeof(CombatState), "_allCards");

    // RNG
    private static readonly FieldInfo RunRngDictField =
        AccessTools.Field(typeof(RunRngSet), "_rngs");
    private static readonly PropertyInfo RunManagerStateProperty =
        AccessTools.Property(typeof(RunManager), "State");
    private static readonly FieldInfo CombatManagerStateField =
        AccessTools.Field(typeof(CombatManager), "_state");

    // CombatState creature lists
    private static readonly FieldInfo CsAlliesField =
        AccessTools.Field(typeof(CombatState), "_allies");
    private static readonly FieldInfo CsEnemiesField =
        AccessTools.Field(typeof(CombatState), "_enemies");
    private static readonly FieldInfo? CsCreaturesChangedField =
        AccessTools.Field(typeof(CombatState), "CreaturesChanged");

    // Card mutable fields — populated once, used to copy state from clone to original
    private static readonly FieldInfo[] CardMutableFields = InitCardMutableFields();

    // Orb field skip set — fields to NOT copy when restoring orb state from clones
    // NOTE: does NOT skip HasBeenRemovedFromState — we want to restore that
    private static readonly HashSet<string> OrbFieldSkipSet = new()
    {
        "_canonicalInstance", "_owner",
        "<Id>k__BackingField", "<IsMutable>k__BackingField",
        "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
        "_dynamicVars"
    };

    // Potion field skip set — identity/ownership fields to NOT copy when restoring
    // NOTE: does NOT skip HasBeenRemovedFromState — we want to restore that
    private static readonly HashSet<string> PotionFieldSkipSet = new()
    {
        "_canonicalInstance", "_owner",
        "<Id>k__BackingField", "<IsMutable>k__BackingField",
        "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
        "_dynamicVars"
    };

    static CombatSnapshot()
    {
        var monsterStateType = AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState");
        MonsterStateIdProperty = monsterStateType != null
            ? AccessTools.Property(monsterStateType, "Id") : null;
        ForceCurrentStateMethod = SmType != null
            ? AccessTools.Method(SmType, "ForceCurrentState") : null;

        // Diagnostic logging for reflection cache
        Log.Write("=== Reflection Cache Diagnostics ===");
        Log.Write($"PlayerPotionSlotsField: {(PlayerPotionSlotsField != null ? "OK" : "NULL")}");
        Log.Write($"PotionRemovedField: {(PotionRemovedField != null ? "OK" : "NULL")}");
        Log.Write($"CmHistoryProperty: {(CmHistoryProperty != null ? $"OK (type={CmHistoryProperty.PropertyType.FullName})" : "NULL")}");
        Log.Write($"HistoryEntriesField: {(HistoryEntriesField != null ? "OK" : "NULL")}");
        Log.Write($"CreatureHpField: {(CreatureHpField != null ? "OK" : "NULL")}");
        Log.Write($"PcsEnergyField: {(PcsEnergyField != null ? "OK" : "NULL")}");
        Log.Write($"RelicDynamicVarsField: {(RelicDynamicVarsField != null ? "OK" : "NULL")}");
        Log.Write($"SmType: {(SmType != null ? "OK" : "NULL")}");
        Log.Write($"InvokeDisplayAmountChangedMethod: {(InvokeDisplayAmountChangedMethod != null ? "OK" : "NULL")}");
        Log.Write("=== End Diagnostics ===");
    }

    private static FieldInfo[] InitCardMutableFields()
    {
        var skipSet = new HashSet<string>
        {
            // Identity/reference fields — must not be overwritten
            "_cloneOf", "_canonicalInstance", "_deckVersion", "_owner",
            "_isDupe", "_currentTarget", "_isEnchantmentPreview",
            // AbstractModel identity fields
            "<Id>k__BackingField", "<IsMutable>k__BackingField",
            "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField"
            // NOTE: _dynamicVars IS restored (not skipped) — stored counters
            // (e.g. exhaust-based damage multipliers) need to be captured.
            // After restoration, InitializeWithOwner fixes CalculatedVar._owner
            // references so they point to the live card, not the clone.
        };

        var fields = new List<FieldInfo>();
        var type = typeof(CardModel);
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly && !skipSet.Contains(field.Name))
                    fields.Add(field);
            }
            type = type.BaseType;
        }
        return fields.ToArray();
    }

    // ── Capture ──

    public static CombatSnapshot? Capture()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;

        var cs = CombatManagerStateField.GetValue(cm) as CombatState;
        if (cs == null) return null;

        try
        {

        // Capture control flow state
        var syncCombatState = MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase;
        var cmPaused = false;
        var execPaused = false;
        var queuePauseStates = new List<bool>();
        try
        {
            var syncr = RunManager.Instance?.ActionQueueSynchronizer;
            if (syncr != null) syncCombatState = syncr.CombatState;
            cmPaused = CombatManager.Instance?.IsPaused ?? false;
            var executor = RunManager.Instance?.ActionExecutor;
            if (executor != null) execPaused = executor.IsPaused;
            // Capture per-queue pause state
            var aqSet = RunManager.Instance?.ActionQueueSet;
            if (aqSet != null)
            {
                var queuesField = AccessTools.Field(aqSet.GetType(), "_actionQueues");
                if (queuesField?.GetValue(aqSet) is System.Collections.IList queues)
                {
                    foreach (var q in queues)
                    {
                        var pausedField = AccessTools.Field(q.GetType(), "isPaused");
                        queuePauseStates.Add(pausedField != null && (bool)pausedField.GetValue(q)!);
                    }
                }
            }
        }
        catch { }

        var snapshot = new CombatSnapshot
        {
            _roundNumber = cs.RoundNumber,
            _currentSide = cs.CurrentSide,
            _syncCombatState = syncCombatState,
            _cmIsPaused = cmPaused,
            _executorIsPaused = execPaused
        };

        snapshot._actionQueuePauseStates.AddRange(queuePauseStates);

        // Capture creature roster (to detect summons for removal on undo)
        foreach (var creature in cs.Creatures)
        {
            if (creature.CombatId != null)
                snapshot._creatureCombatIds.Add(creature.CombatId.Value);
        }

        // Capture creature states
        foreach (var creature in cs.Creatures)
        {
            if (creature.CombatId == null) continue;
            var combatId = creature.CombatId.Value;

            var powers = new List<PowerData>();
            foreach (var power in creature.Powers)
            {
                // Clone _internalData (used by powers like HardenedShellPower
                // to track per-turn state such as damageReceivedThisTurn)
                object? internalDataClone = null;
                var internalData = PowerInternalDataField?.GetValue(power);
                if (internalData != null)
                    internalDataClone = MemberwiseCloneMethod.Invoke(internalData, null);

                // Capture SurroundedPower._facing (character direction when enemies on both sides)
                object? facingDir = null;
                if (SurroundedFacingField != null && SurroundedPowerType != null
                    && SurroundedPowerType.IsInstanceOfType(power))
                    facingDir = SurroundedFacingField.GetValue(power);

                var powerAmount = (int)PowerAmountField.GetValue(power)!;
                powers.Add(new PowerData(
                    power.Id,
                    powerAmount,
                    (int)PowerAmountOnTurnStartField.GetValue(power)!,
                    (bool)PowerSkipField.GetValue(power)!,
                    internalDataClone,
                    facingDir));

            }

            // Save visual position and body scale for restoring after revive / facing
            Vector2? visualPos = null;
            Vector2? bodyScale = null;
            var nCreatureNode = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreatureNode != null)
            {
                visualPos = nCreatureNode.GlobalPosition;
                bodyScale = nCreatureNode.Body?.Scale;
            }

            snapshot._creatureStates.Add(new CreatureData(
                combatId, creature, creature.CurrentHp, creature.MaxHp,
                creature.Block, powers, visualPos, bodyScale));

            // Monster RNG + move state
            if (creature.Monster != null)
            {
                var rng = MonsterRngField.GetValue(creature.Monster) as Rng;
                if (rng != null)
                    snapshot._monsterRngStates[combatId] = (rng.Seed, rng.Counter);

                CaptureMonsterMoves(snapshot, creature.Monster, combatId);
            }
        }

        // Capture player combat state
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var pcs = player.PlayerCombatState;
            snapshot._energy = (int)PcsEnergyField.GetValue(pcs)!;
            snapshot._stars = (int)PcsStarsField.GetValue(pcs)!;

            // Card piles (save references)
            foreach (var pile in pcs.AllPiles)
                snapshot._savedPiles[pile.Type] = pile.Cards.ToList();

            // Card mutable state (save deep clones)
            foreach (var card in pcs.AllCards)
                snapshot._cardClones[card] = (CardModel)card.MutableClone();

            // Orbs
            CaptureOrbs(snapshot, pcs);

            // Pets
            CapturePets(snapshot, pcs);

            // Relics
            CaptureRelics(snapshot, player);

            // Potions
            CapturePotions(snapshot, player);

            // Gold
            snapshot._gold = (int)PlayerGoldField.GetValue(player)!;
        }

        // Escaped creatures (shallow copy of references)
        if (EscapedCreaturesProp?.GetValue(cs) is IEnumerable<Creature> escapedCreatures)
            snapshot._escapedCreatures.AddRange(escapedCreatures);

        // Creature ID allocator
        if (NextCreatureIdField != null)
            snapshot._nextCreatureId = (uint)NextCreatureIdField.GetValue(cs)!;

        // All cards tracked by CombatState
        if (AllCardsField?.GetValue(cs) is List<CardModel> allCards)
            snapshot._allCards.AddRange(allCards);

        CaptureRunRng(snapshot);
        CaptureCombatHistory(snapshot);

        // Summary
        var pileSummary = string.Join(", ", snapshot._savedPiles.Select(kv => $"{kv.Key}={kv.Value.Count}"));
        Log.Write($"Capture: round={snapshot._roundNumber} side={snapshot._currentSide} " +
            $"creatures={snapshot._creatureStates.Count} energy={snapshot._energy} stars={snapshot._stars} " +
            $"piles=[{pileSummary}] cards={snapshot._cardClones.Count} " +
            $"orbs={snapshot._savedOrbRefs.Count}(cap={snapshot._orbCapacity}) " +
            $"relics={snapshot._relicStates.Count} potions={snapshot._potionSlotRefs.Count(p => p != null)} " +
            $"gold={snapshot._gold} escaped={snapshot._escapedCreatures.Count} " +
            $"nextId={snapshot._nextCreatureId} allCards={snapshot._allCards.Count}");
        return snapshot;
        }
        catch (Exception ex)
        {
            Log.Write($"Capture FAILED: {ex}");
            return FailedSentinel;
        }
    }

    private static void CaptureMonsterMoves(
        CombatSnapshot snapshot, MonsterModel monster, uint combatId)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        // NextMove state ID
        string? nextMoveId = null;
        var nextMove = monster.NextMove;
        if (nextMove != null)
            nextMoveId = MonsterStateIdProperty?.GetValue(nextMove) as string;

        // Current state ID
        string? currentStateId = null;
        var currentState = SmCurrentStateField?.GetValue(sm);
        if (currentState != null)
            currentStateId = MonsterStateIdProperty?.GetValue(currentState) as string;

        bool performedFirstMove = SmPerformedFirstMoveField != null &&
            (bool)SmPerformedFirstMoveField.GetValue(sm)!;

        bool spawnedThisTurn = MonsterSpawnedField != null &&
            (bool)MonsterSpawnedField.GetValue(monster)!;

        // StateLog (list of state IDs)
        var stateLogIds = new List<string>();
        var stateLogProp = SmType != null ? AccessTools.Property(SmType, "StateLog") : null;
        if (stateLogProp?.GetValue(sm) is System.Collections.IList stateLog)
        {
            foreach (var state in stateLog)
            {
                var id = MonsterStateIdProperty?.GetValue(state) as string;
                if (id != null) stateLogIds.Add(id);
            }
        }

        // MoveState._performedAtLeastOnce for each MoveState in the States dictionary
        var movePerformed = new Dictionary<string, bool>();
        var statesProp = SmType != null ? AccessTools.Property(SmType, "States") : null;
        if (statesProp?.GetValue(sm) is System.Collections.IDictionary statesDict &&
            MoveStatePerformedField != null)
        {
            foreach (System.Collections.DictionaryEntry entry in statesDict)
            {
                var key = entry.Key as string;
                if (key != null && MoveStateType != null &&
                    MoveStateType.IsInstanceOfType(entry.Value))
                {
                    movePerformed[key] =
                        (bool)MoveStatePerformedField.GetValue(entry.Value)!;
                }
            }
        }

        snapshot._monsterMoveStates[combatId] = new MonsterMoveSnapshot(
            nextMoveId, currentStateId, performedFirstMove, spawnedThisTurn,
            stateLogIds, movePerformed);
    }

    private static void CaptureOrbs(CombatSnapshot snapshot, PlayerCombatState pcs)
    {
        var orbQueue = pcs.OrbQueue;
        if (orbQueue == null) return;

        snapshot._hasOrbData = true;
        snapshot._orbCapacity = orbQueue.Capacity;

        foreach (var orb in orbQueue.Orbs)
        {
            snapshot._savedOrbRefs.Add(orb);
            snapshot._orbClones[orb] = (OrbModel)orb.MutableClone();
        }
        Log.Write($"CaptureOrbs: {snapshot._savedOrbRefs.Count} orbs, capacity={snapshot._orbCapacity}" +
            $" types=[{string.Join(", ", snapshot._savedOrbRefs.Select(o => o.GetType().Name))}]");
    }

    private static void CapturePets(CombatSnapshot snapshot, PlayerCombatState pcs)
    {
        if (PcsPetsField == null) return;
        if (PcsPetsField.GetValue(pcs) is not System.Collections.IList petsList) return;

        foreach (var pet in petsList)
        {
            if (pet is Creature creature && creature.CombatId != null)
                snapshot._petCombatIds.Add(creature.CombatId.Value);
        }
    }

    private static void CaptureRelics(CombatSnapshot snapshot, Player player)
    {
        foreach (var relic in player.Relics)
        {
            object? dvClone = null;
            try { dvClone = relic.DynamicVars?.Clone(relic); }
            catch { /* some relics may not support DynamicVars cloning */ }

            var status = RelicStatusProperty?.GetValue(relic);

            snapshot._relicStates.Add(new RelicSnapshot(
                relic.Id,
                relic.StackCount,
                relic.IsWax,
                relic.IsMelted,
                status!,
                dvClone));

            // Clone the relic to capture subclass-specific private fields
            // (e.g. BrilliantScarf._cardsPlayedThisTurn, VelvetChoker._cardsPlayedThisTurn)
            try
            {
                var clone = MemberwiseCloneMethod.Invoke(relic, null);
                if (clone != null)
                {
                    snapshot._relicClones[relic] = clone;
                    // Log subclass field values at capture time
                    LogRelicSubclassFields("CaptureRelics", relic.Id, clone);
                }
            }
            catch { }
        }
    }

    private static void CapturePotions(CombatSnapshot snapshot, Player player)
    {
        if (PlayerPotionSlotsField == null)
        {
            Log.Write("CapturePotions: PlayerPotionSlotsField is NULL, skipping");
            return;
        }
        int count = 0;
        foreach (var potion in player.PotionSlots)
        {
            // Store original reference (preserves identity like cards)
            snapshot._potionSlotRefs.Add(potion);
            // Clone mutable state for restoration
            if (potion != null && !snapshot._potionClones.ContainsKey(potion))
                snapshot._potionClones[potion] = (PotionModel)potion.MutableClone();
            if (potion != null) count++;
        }
        Log.Write($"CapturePotions: {count} potions in {player.PotionSlots.Count} slots");
    }

    private static void CaptureCombatHistory(CombatSnapshot snapshot)
    {
        var cm = CombatManager.Instance;
        if (cm == null)
        {
            Log.Write("CaptureCombatHistory: CombatManager.Instance is NULL");
            return;
        }
        if (CmHistoryProperty == null)
        {
            Log.Write("CaptureCombatHistory: CmHistoryProperty is NULL");
            return;
        }
        if (HistoryEntriesField == null)
        {
            Log.Write("CaptureCombatHistory: HistoryEntriesField is NULL");
            return;
        }

        var history = CmHistoryProperty.GetValue(cm);
        if (history == null)
        {
            Log.Write("CaptureCombatHistory: history object is NULL");
            return;
        }

        var rawEntries = HistoryEntriesField.GetValue(history);
        if (rawEntries == null)
        {
            Log.Write("CaptureCombatHistory: _entries value is NULL");
            return;
        }

        if (rawEntries is System.Collections.IList entries)
        {
            snapshot._savedHistoryEntries = new List<object>(entries.Cast<object>());
            Log.Write($"CaptureCombatHistory: captured {entries.Count} entries");
        }
        else
        {
            Log.Write($"CaptureCombatHistory: _entries is not IList, type={rawEntries.GetType().FullName}");
        }
    }

    private static void CaptureRunRng(CombatSnapshot snapshot)
    {
        var runManager = RunManager.Instance;
        if (runManager == null) return;

        var runState = RunManagerStateProperty?.GetValue(runManager) as RunState;
        if (runState == null) return;

        var runRngSet = runState.Rng;
        if (runRngSet == null) return;

        var rngsDict = RunRngDictField?.GetValue(runRngSet) as Dictionary<RunRngType, Rng>;
        if (rngsDict == null) return;

        foreach (var kvp in rngsDict)
            snapshot._runRngStates[kvp.Key] = (kvp.Value.Seed, kvp.Value.Counter);
    }

    // ── Restore ──

    public void Restore()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;

        var cs = CombatManagerStateField.GetValue(cm) as CombatState;
        if (cs == null) return;

        Log.Write("=== Restore() starting ===");

        // DO NOT call ActionQueueSet.Reset() — it kills the action processing loop.
        // We only allow undo when the queue is idle (checked in UndoTheSpireMod).

        cs.RoundNumber = _roundNumber;
        cs.CurrentSide = _currentSide;

        // Restore async machinery — clean up stale state, then re-initialize play phase.
        //
        // Instead of trying to save/restore every piece of async state individually,
        // we: (1) clean up anything stale, then (2) run the same 5-line play phase
        // initialization that the game does at the start of every player turn
        // (CombatManager.StartTurn lines 393638-393642). This ensures the async
        // machinery is in a known-good state regardless of what undo disrupted.
        try
        {
            var syncr = RunManager.Instance?.ActionQueueSynchronizer;
            var executor = RunManager.Instance?.ActionExecutor;

            // 1. Clear stale deferred actions (would be enqueued by SetCombatState)
            if (syncr != null)
            {
                var deferredField = AccessTools.Field(syncr.GetType(), "_requestedActionsWaitingForPlayerTurn");
                if (deferredField?.GetValue(syncr) is System.Collections.IList deferred && deferred.Count > 0)
                {
                    Log.Write($"Restore: clearing {deferred.Count} stale deferred actions");
                    deferred.Clear();
                }
            }

            // 2. Complete any pending ActionExecutor TCS so IsRunning=false
            if (executor != null)
            {
                var tcsField = AccessTools.Field(executor.GetType(), "_queueTaskCompletionSource");
                if (tcsField != null)
                {
                    var tcs = tcsField.GetValue(executor);
                    if (tcs != null)
                    {
                        var task = tcs.GetType().GetProperty("Task")?.GetValue(tcs) as Task;
                        if (task != null && !task.IsCompleted)
                        {
                            Log.Write("Restore: completing ActionExecutor TCS");
                            tcs.GetType().GetMethod("TrySetResult")?.Invoke(tcs, new object[] { true });
                        }
                    }
                }
            }

            // 3. Clear NPlayerHand._selectionCompletionSource (Burning Pact / SimpleSelect)
            var hand = NPlayerHand.Instance;
            if (hand != null)
            {
                var selTcsField = AccessTools.Field(typeof(NPlayerHand), "_selectionCompletionSource");
                if (selTcsField != null)
                {
                    var selTcs = selTcsField.GetValue(hand);
                    if (selTcs != null)
                    {
                        var selTask = selTcs.GetType().GetProperty("Task")?.GetValue(selTcs) as Task;
                        if (selTask != null && !selTask.IsCompleted)
                        {
                            Log.Write("Restore: completing _selectionCompletionSource");
                            selTcs.GetType().GetMethod("SetResult")
                                ?.Invoke(selTcs, new object[] { System.Array.Empty<CardModel>() });
                        }
                        selTcsField.SetValue(hand, null);
                    }
                }
                var backstopField = AccessTools.Field(typeof(NPlayerHand), "_selectModeBackstop");
                if (backstopField?.GetValue(hand) is Control backstop) backstop.Visible = false;
                var headerField = AccessTools.Field(typeof(NPlayerHand), "_selectionHeader");
                if (headerField?.GetValue(hand) is Control header) header.Visible = false;
                var selectedField = AccessTools.Field(typeof(NPlayerHand), "_selectedCards");
                if (selectedField?.GetValue(hand) is System.Collections.IList sel && sel.Count > 0) sel.Clear();
            }

            // 4. Re-initialize play phase — the same sequence the game runs at the
            //    start of every player turn (CombatManager.StartTurn lines 393638-393642).
            //    This ensures SetCombatState, queue unpausing, IsPlayPhase, and
            //    TurnStarted event all happen correctly, regardless of what undo broke.
            if (_currentSide == CombatSide.Player)
            {
                Log.Write("Restore: re-initializing player play phase");
                executor?.Unpause();
                syncr?.SetCombatState(
                    MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase);
                var cmInst = CombatManager.Instance;
                if (cmInst != null)
                {
                    var isPlayPhaseProp = AccessTools.Property(typeof(CombatManager), "IsPlayPhase");
                    isPlayPhaseProp?.SetValue(cmInst, true);
                    var isEnemyStartedProp = AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted");
                    isEnemyStartedProp?.SetValue(cmInst, false);
                    // Fire TurnStarted — updates End Turn button text + visibility
                    var turnStartedField = AccessTools.Field(typeof(CombatManager), "TurnStarted");
                    var turnStartedDelegate = turnStartedField?.GetValue(cmInst) as Delegate;
                    turnStartedDelegate?.DynamicInvoke(cs);
                }
            }
        }
        catch (Exception ex) { Log.Write($"Restore: async state ERROR: {ex.Message}"); }

        // Restore creature states
        try
        {
            int restoredCount = 0, skippedCount = 0;
            foreach (var saved in _creatureStates)
            {
                Creature? creature = null;
                foreach (var c in cs.Creatures)
                {
                    if (c.CombatId == saved.CombatId)
                    {
                        creature = c;
                        break;
                    }
                }
                if (creature == null)
                {
                    skippedCount++;
                    continue;
                }

                bool wasDead = creature.IsDead;

                Log.Write($"RestoreCreature: id={saved.CombatId} hp={creature.CurrentHp}->{saved.CurrentHp}" +
                    $" maxHp={creature.MaxHp}->{saved.MaxHp} block={creature.Block}->{saved.Block}" +
                    $" powers={creature.Powers.Count}->{saved.Powers.Count} wasDead={wasDead}");

                CreatureHpField.SetValue(creature, saved.CurrentHp);
                CreatureMaxHpField.SetValue(creature, saved.MaxHp);
                CreatureBlockField.SetValue(creature, saved.Block);
                RestorePowers(creature, saved.Powers);

                // Restore Body.Scale (encodes character facing direction via Scale.X sign)
                if (saved.VisualBodyScale.HasValue)
                {
                    var nCreatureForScale = NCombatRoom.Instance?.GetCreatureNode(creature);
                    var body = nCreatureForScale?.Body;
                    if (body != null)
                        body.Scale = saved.VisualBodyScale.Value;
                }

                if (creature.Monster != null)
                {
                    if (_monsterRngStates.TryGetValue(saved.CombatId, out var rngState))
                        MonsterRngField.SetValue(creature.Monster,
                            new Rng(rngState.seed, rngState.counter));

                    RestoreMonsterMoves(creature.Monster, saved.CombatId);
                }

                // If creature was dead and is now alive, revive its visual
                if (wasDead && saved.CurrentHp > 0)
                {
                    Log.Write($"RestoreCreature: id={saved.CombatId} dead->alive, reviving visual");
                    var combatRoom = NCombatRoom.Instance;
                    if (combatRoom != null)
                    {
                        var nCreature = combatRoom.GetCreatureNode(creature);
                        if (nCreature != null)
                        {
                            // Visual exists (creature stayed in combat after death) —
                            // just play revive animation to re-enable UI + animation
                            nCreature.StartReviveAnim();
                        }
                        else
                        {
                            // Visual was QueueFree'd on death — re-create it
                            combatRoom.AddCreature(creature);
                            combatRoom.GetCreatureNode(creature)?.StartReviveAnim();
                        }
                    }
                }

                // If creature was alive and should now be dead, remove its visual
                if (!wasDead && saved.CurrentHp <= 0)
                {
                    Log.Write($"RestoreCreature: id={saved.CombatId} alive->dead, removing visual");
                    RemoveCreatureVisual(creature);
                }

                restoredCount++;
            }
            Log.Write($"RestoreCreatures: restored={restoredCount} skipped={skippedCount} (dead/missing, will revive next)");
        }
        catch (Exception ex) { Log.Write($"ERROR in RestoreCreatures: {ex}"); }

        // Revive creatures that were killed and removed since this snapshot
        try { ReviveKilledCreatures(cs); }
        catch (Exception ex) { Log.Write($"ERROR in ReviveKilledCreatures: {ex}"); }

        // Remove creatures that were summoned after this snapshot was taken
        try { RemoveSummonedCreatures(cs); }
        catch (Exception ex) { Log.Write($"ERROR in RemoveSummonedCreatures: {ex}"); }

        // Restore player combat state
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var pcs = player.PlayerCombatState;

            try
            {
                PcsEnergyField.SetValue(pcs, _energy);
                PcsStarsField.SetValue(pcs, _stars);
                RestoreCardPiles(pcs);
                RestoreCardStates(pcs);
            }
            catch (Exception ex) { Log.Write($"ERROR in RestoreCards/Energy: {ex}"); }

            try { RestoreOrbs(pcs); }
            catch (Exception ex) { Log.Write($"ERROR in RestoreOrbs: {ex}"); }

            try { RestorePets(pcs, cs); }
            catch (Exception ex) { Log.Write($"ERROR in RestorePets: {ex}"); }

            try { RestoreRelics(player); }
            catch (Exception ex) { Log.Write($"ERROR in RestoreRelics: {ex}"); }

            try { RestorePotions(player); }
            catch (Exception ex) { Log.Write($"ERROR in RestorePotions: {ex}"); }

            // Restore gold
            try
            {
                int oldGold = player.Gold;
                PlayerGoldField.SetValue(player, _gold);
                if (oldGold != _gold)
                    Log.Write($"RestoreGold: {oldGold}->{_gold}");
            }
            catch (Exception ex) { Log.Write($"ERROR in RestoreGold: {ex}"); }
        }

        // Restore escaped creatures list
        try
        {
            if (EscapedCreaturesProp != null)
            {
                // Modify in-place to avoid stale references from code caching the list
                var currentEscaped = EscapedCreaturesProp.GetValue(cs);
                if (currentEscaped is IList<Creature> escapedList)
                {
                    int oldCount = escapedList.Count;
                    escapedList.Clear();
                    foreach (var creature in _escapedCreatures)
                        escapedList.Add(creature);
                    Log.Write($"RestoreEscapedCreatures: {oldCount}->{_escapedCreatures.Count}");
                }
            }
        }
        catch (Exception ex) { Log.Write($"ERROR in RestoreEscapedCreatures: {ex}"); }

        // NOTE: _nextCreatureId and _allCards are monotonically-growing tracking
        // structures. Rolling them back corrupts game bookkeeping (e.g. creatures
        // created after the snapshot get orphaned IDs, cards lose tracking).
        // We still capture them for diagnostic logging but skip restore.
        // try
        // {
        //     if (NextCreatureIdField != null)
        //         NextCreatureIdField.SetValue(cs, _nextCreatureId);
        // }
        // catch (Exception ex) { Log.Write($"ERROR in RestoreNextCreatureId: {ex}"); }

        // try
        // {
        //     if (AllCardsField?.GetValue(cs) is List<CardModel> currentAllCards)
        //     {
        //         int oldCount = currentAllCards.Count;
        //         currentAllCards.Clear();
        //         currentAllCards.AddRange(_allCards);
        //         Log.Write($"RestoreAllCards: {oldCount}->{_allCards.Count}");
        //     }
        // }
        // catch (Exception ex) { Log.Write($"ERROR in RestoreAllCards: {ex}"); }

        try { RestoreRunRng(); }
        catch (Exception ex) { Log.Write($"ERROR in RestoreRunRng: {ex}"); }

        try { RestoreCombatHistory(); }
        catch (Exception ex) { Log.Write($"ERROR in RestoreCombatHistory: {ex}"); }

        Log.Write("=== Restore() complete ===");
    }

    // ── Restore Helpers ──

    private static void RestorePowers(Creature creature, List<PowerData> savedPowers)
    {
        var powersList = (List<PowerModel>)CreaturePowersField.GetValue(creature)!;

        var savedByKey = new Dictionary<ModelId, PowerData>();
        foreach (var p in savedPowers)
            savedByKey[p.Id] = p;

        for (int i = powersList.Count - 1; i >= 0; i--)
        {
            if (!savedByKey.ContainsKey(powersList[i].Id))
                powersList.RemoveAt(i);
        }

        var existingIds = new HashSet<ModelId>();
        foreach (var power in powersList)
        {
            if (savedByKey.TryGetValue(power.Id, out var saved))
            {
                PowerAmountField.SetValue(power, saved.Amount);
                PowerAmountOnTurnStartField.SetValue(power, saved.AmountOnTurnStart);
                PowerSkipField.SetValue(power, saved.SkipNextDurationTick);
                // Restore internal data (e.g. VoidFormPower.cardsPlayedThisTurn)
                // Re-clone so the snapshot retains its original copy (game mutates the live object)
                if (saved.InternalData != null && PowerInternalDataField != null)
                {
                    var cloned = MemberwiseCloneMethod.Invoke(saved.InternalData, null);
                    PowerInternalDataField.SetValue(power, cloned);
                }
                // Restore SurroundedPower._facing
                if (saved.FacingDirection != null && SurroundedFacingField != null)
                    SurroundedFacingField.SetValue(power, saved.FacingDirection);
                existingIds.Add(power.Id);
            }
        }

        var ownerField = AccessTools.Field(typeof(PowerModel), "_owner");
        foreach (var saved in savedPowers)
        {
            if (existingIds.Contains(saved.Id)) continue;

            var canonical = ModelDb.GetByIdOrNull<PowerModel>(saved.Id);
            if (canonical == null) continue;

            var newPower = (PowerModel)canonical.MutableClone();
            PowerAmountField.SetValue(newPower, saved.Amount);
            PowerAmountOnTurnStartField.SetValue(newPower, saved.AmountOnTurnStart);
            PowerSkipField.SetValue(newPower, saved.SkipNextDurationTick);
            // Restore internal data for re-created powers too
            // Re-clone so the snapshot retains its original copy
            if (saved.InternalData != null && PowerInternalDataField != null)
            {
                var cloned = MemberwiseCloneMethod.Invoke(saved.InternalData, null);
                PowerInternalDataField.SetValue(newPower, cloned);
            }
            // Restore SurroundedPower._facing for re-created powers
            if (saved.FacingDirection != null && SurroundedFacingField != null)
                SurroundedFacingField.SetValue(newPower, saved.FacingDirection);
            ownerField?.SetValue(newPower, creature);

            powersList.Add(newPower);
        }
    }

    private void RestoreCardPiles(PlayerCombatState pcs)
    {
        var stateTracker = CombatManager.Instance?.StateTracker;

        // Collect all cards currently in piles BEFORE restoration.
        // Used to diff against post-restore state for StateTracker subscription sync.
        var cardsBefore = new HashSet<CardModel>();
        foreach (var pile in pcs.AllPiles)
        {
            foreach (var card in pile.Cards)
                cardsBefore.Add(card);
        }

        // Restore pile contents
        foreach (var pile in pcs.AllPiles)
        {
            if (!_savedPiles.TryGetValue(pile.Type, out var savedCards))
                continue;

            var cardsList = (List<CardModel>)CardPileCardsField.GetValue(pile)!;
            int oldCount = cardsList.Count;
            cardsList.Clear();
            cardsList.AddRange(savedCards);
            Log.Write($"RestoreCardPiles: {pile.Type} {oldCount}->{savedCards.Count} cards");

            // Fire ContentsChanged so pile count UI updates
            pile.InvokeContentsChanged();
        }

        // Collect all cards in piles AFTER restoration
        var cardsAfter = new HashSet<CardModel>();
        foreach (var pile in pcs.AllPiles)
        {
            foreach (var card in pile.Cards)
                cardsAfter.Add(card);
        }

        // Sync CombatStateTracker subscriptions.
        // CardPile.AddInternal/RemoveInternal normally manage these, but we
        // bypass them by directly setting _cards. Without this sync, cards
        // removed from piles retain stale event handlers and cards added to
        // piles lack required subscriptions — causing animation hangs when
        // enemies modify cards during their turn.
        if (stateTracker != null)
        {
            int unsubs = 0, subs = 0;
            foreach (var card in cardsBefore)
            {
                if (!cardsAfter.Contains(card))
                {
                    try { StateTrackerUnsubscribeCardMethod?.Invoke(stateTracker, new object[] { card }); unsubs++; }
                    catch (Exception ex) { Log.Write($"StateTracker.Unsubscribe ERROR: {ex}"); }
                }
            }
            foreach (var card in cardsAfter)
            {
                if (!cardsBefore.Contains(card))
                {
                    try { StateTrackerSubscribeCardMethod?.Invoke(stateTracker, new object[] { card }); subs++; }
                    catch (Exception ex) { Log.Write($"StateTracker.Subscribe ERROR: {ex}"); }
                }
            }
            if (unsubs > 0 || subs > 0)
                Log.Write($"RestoreCardPiles: StateTracker sync: unsubscribed={unsubs} subscribed={subs}");
        }
    }

    private void RestoreCardStates(PlayerCombatState pcs)
    {
        // Copy mutable fields from each saved clone back to the original card object.
        // This restores energy cost, keywords, flags, etc. while preserving card identity.
        int matched = 0, missed = 0;
        foreach (var card in pcs.AllCards)
        {
            if (!_cardClones.TryGetValue(card, out var clone)) { missed++; continue; }
            matched++;

            foreach (var field in CardMutableFields)
            {
                try { field.SetValue(card, field.GetValue(clone)); }
                catch { /* skip any field that can't be set */ }
            }

            // Fix DynamicVars _owner references — the cloned DynamicVarSet has _owner
            // pointing to the clone. Re-initialize to point to the live card so
            // CalculatedVar multipliers (e.g. exhaust pile count) read the correct state.
            card.DynamicVars.InitializeWithOwner(card);

            // Fix EnergyCost._card — the cloned CardEnergyCost has _card pointing to
            // the clone. Without this fix, GetWithModifiers checks _card.CombatState
            // which is null on the clone (not in any pile), so global cost modifiers
            // like VoidForm never apply.
            if (EnergyCostCardField != null && card.EnergyCost != null)
                EnergyCostCardField.SetValue(card.EnergyCost, card);
        }
        Log.Write($"RestoreCardStates: restored {matched} cards, {missed} not found in snapshot");
    }

    private void RestoreMonsterMoves(MonsterModel monster, uint combatId)
    {
        if (!_monsterMoveStates.TryGetValue(combatId, out var saved)) return;

        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        SmPerformedFirstMoveField?.SetValue(sm, saved.PerformedFirstMove);
        MonsterSpawnedField?.SetValue(monster, saved.SpawnedThisTurn);

        // Get States dictionary via reflection
        var statesProp = SmType != null ? AccessTools.Property(SmType, "States") : null;
        var statesDict = statesProp?.GetValue(sm) as System.Collections.IDictionary;
        if (statesDict == null) return;

        // Restore current state via ForceCurrentState (public)
        if (saved.CurrentStateId != null && statesDict.Contains(saved.CurrentStateId))
        {
            var currentState = statesDict[saved.CurrentStateId];
            ForceCurrentStateMethod?.Invoke(sm, new[] { currentState });
        }

        // Restore StateLog
        var stateLogProp = SmType != null ? AccessTools.Property(SmType, "StateLog") : null;
        if (stateLogProp?.GetValue(sm) is System.Collections.IList stateLog)
        {
            stateLog.Clear();
            foreach (var id in saved.StateLogIds)
            {
                if (statesDict.Contains(id))
                    stateLog.Add(statesDict[id]);
            }
        }

        // Restore MoveState._performedAtLeastOnce
        if (MoveStatePerformedField != null)
        {
            foreach (System.Collections.DictionaryEntry entry in statesDict)
            {
                var key = entry.Key as string;
                if (key != null && MoveStateType != null &&
                    MoveStateType.IsInstanceOfType(entry.Value) &&
                    saved.MovePerformedAtLeastOnce.TryGetValue(key, out var performed))
                {
                    MoveStatePerformedField.SetValue(entry.Value, performed);
                }
            }
        }

        // Restore NextMove directly via reflection (bypasses CanTransitionAway check).
        // SetMoveImmediate checks NextMove.CanTransitionAway which fails for moves with
        // MustPerformOnceBeforeTransitioning=true (e.g. REATTACH_MOVE, RESPAWN_MOVE),
        // silently leaving the wrong NextMove and causing premature respawn.
        if (saved.NextMoveStateId != null && statesDict.Contains(saved.NextMoveStateId))
        {
            var nextState = statesDict[saved.NextMoveStateId];
            if (MoveStateType != null && MoveStateType.IsInstanceOfType(nextState))
                NextMoveProp?.SetValue(monster, nextState);
        }
    }

    private void RestoreOrbs(PlayerCombatState pcs)
    {
        if (!_hasOrbData) return;

        var orbQueue = pcs.OrbQueue;
        if (orbQueue == null || OrbQueueOrbsField == null)
        {
            Log.Write($"RestoreOrbs: skipped (orbQueue={orbQueue != null}, field={OrbQueueOrbsField != null})");
            return;
        }

        if (OrbQueueOrbsField.GetValue(orbQueue) is System.Collections.IList orbsList)
        {
            int oldCount = orbsList.Count;
            orbsList.Clear();
            foreach (var orb in _savedOrbRefs)
            {
                if (_orbClones.TryGetValue(orb, out var clone))
                    CopyOrbFields(clone, orb);
                orbsList.Add(orb);
            }
            Log.Write($"RestoreOrbs: {oldCount}->{_savedOrbRefs.Count} orbs, capacity={_orbCapacity}" +
                $" types=[{string.Join(", ", _savedOrbRefs.Select(o => o.GetType().Name))}]");
        }

        OrbQueueCapacityField?.SetValue(orbQueue, _orbCapacity);
    }

    /// <summary>
    /// Copy all mutable instance fields from source (clone) to target (original),
    /// walking up the type hierarchy. Skips identity/ownership fields in OrbFieldSkipSet.
    /// Handles subclass-specific fields like DarkOrb._evokeVal, GlassOrb._passiveVal.
    /// </summary>
    private static void CopyOrbFields(OrbModel source, OrbModel target)
    {
        var type = source.GetType();
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (OrbFieldSkipSet.Contains(field.Name)) continue;
                if (field.IsInitOnly || field.IsLiteral) continue;
                try
                {
                    field.SetValue(target, field.GetValue(source));
                }
                catch (Exception ex)
                {
                    Log.Write($"CopyOrbFields: failed to copy {type.Name}.{field.Name}: {ex.Message}");
                }
            }
            type = type.BaseType;
        }
    }

    /// <summary>
    /// Copy all mutable instance fields from source (clone) to target (original),
    /// walking up the type hierarchy. Skips identity/ownership fields in PotionFieldSkipSet.
    /// </summary>
    private static void CopyPotionFields(PotionModel source, PotionModel target)
    {
        var type = source.GetType();
        while (type != null && type != typeof(object))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic |
                BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (PotionFieldSkipSet.Contains(field.Name)) continue;
                if (field.IsInitOnly || field.IsLiteral) continue;
                try
                {
                    field.SetValue(target, field.GetValue(source));
                }
                catch (Exception ex)
                {
                    Log.Write($"CopyPotionFields: failed to copy {type.Name}.{field.Name}: {ex.Message}");
                }
            }
            type = type.BaseType;
        }
    }

    private void RestorePets(PlayerCombatState pcs, CombatState cs)
    {
        if (PcsPetsField == null) return;
        if (PcsPetsField.GetValue(pcs) is not System.Collections.IList petsList) return;

        petsList.Clear();
        foreach (var id in _petCombatIds)
        {
            foreach (var creature in cs.Creatures)
            {
                if (creature.CombatId == id)
                {
                    petsList.Add(creature);
                    break;
                }
            }
        }
    }

    private void RestoreRelics(Player player)
    {
        var savedByKey = new Dictionary<ModelId, RelicSnapshot>();
        foreach (var saved in _relicStates)
            savedByKey[saved.Id] = saved;

        foreach (var relic in player.Relics)
        {
            if (!savedByKey.TryGetValue(relic.Id, out var saved)) continue;

            RelicStackCountField?.SetValue(relic, saved.StackCount);
            relic.IsWax = saved.IsWax;
            relic.IsMelted = saved.IsMelted;
            RelicStatusProperty?.SetValue(relic, saved.Status);

            if (saved.DynamicVarsClone != null)
                RelicDynamicVarsField?.SetValue(relic, saved.DynamicVarsClone);

            // Restore subclass-specific private fields from the MemberwiseClone
            // (e.g. _cardsPlayedThisTurn on BrilliantScarf, TurnsSeen on PollinousCore, etc.)
            if (_relicClones.TryGetValue(relic, out var clone))
            {
                Log.Write($"RestoreRelics: {relic.Id} BEFORE restore:");
                LogRelicSubclassFields("  BEFORE", relic.Id, relic);
                int copied = CopyRelicSubclassFields(clone, relic);
                Log.Write($"RestoreRelics: {relic.Id} AFTER restore ({copied} fields):");
                LogRelicSubclassFields("  AFTER", relic.Id, relic);
            }

            // Refresh the relic's counter display (e.g. PollinousCore turn counter)
            try { InvokeDisplayAmountChangedMethod?.Invoke(relic, null); }
            catch { }
        }
    }

    /// <summary>
    /// Copies all private instance fields declared on concrete relic subtypes
    /// (from the actual type up to, but NOT including, RelicModel itself).
    /// This restores per-turn counters like _cardsPlayedThisTurn without
    /// needing to enumerate every relic type explicitly.
    /// </summary>
    private static int CopyRelicSubclassFields(object clone, RelicModel target)
    {
        int count = 0;
        var type = target.GetType();
        while (type != null && type != typeof(RelicModel))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    var oldVal = field.GetValue(target);
                    var newVal = field.GetValue(clone);
                    field.SetValue(target, newVal);
                    if (!Equals(oldVal, newVal))
                        Log.Write($"  CopyField: {target.Id}.{field.Name} {oldVal} -> {newVal}");
                    count++;
                }
            }
            type = type.BaseType;
        }
        return count;
    }

    private static void LogRelicSubclassFields(string context, ModelId relicId, object relicOrClone)
    {
        var type = relicOrClone.GetType();
        while (type != null && type != typeof(RelicModel))
        {
            foreach (var field in type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.DeclaredOnly))
            {
                if (!field.IsLiteral && !field.IsInitOnly)
                {
                    var val = field.GetValue(relicOrClone);
                    Log.Write($"  {context}: {relicId}.{field.Name} = {val}");
                }
            }
            type = type.BaseType;
        }
    }

    private void RestorePotions(Player player)
    {
        if (PlayerPotionSlotsField == null)
        {
            Log.Write("RestorePotions: PlayerPotionSlotsField is NULL");
            return;
        }

        var slotsObj = PlayerPotionSlotsField.GetValue(player);
        if (slotsObj == null)
        {
            Log.Write("RestorePotions: _potionSlots value is NULL");
            return;
        }

        var slots = slotsObj as List<PotionModel?>;
        if (slots == null)
        {
            Log.Write($"RestorePotions: _potionSlots is not List<PotionModel?>, type={slotsObj.GetType().FullName}");
            return;
        }

        Log.Write($"RestorePotions: current={slots.Count} slots, saved={_potionSlotRefs.Count} slots");
        for (int i = 0; i < slots.Count && i < _potionSlotRefs.Count; i++)
        {
            var oldPotion = slots[i];
            var originalRef = _potionSlotRefs[i];
            slots[i] = originalRef;
            if (originalRef != null)
            {
                // Copy mutable state from clone back to original (preserves identity)
                if (_potionClones.TryGetValue(originalRef, out var clone))
                    CopyPotionFields(clone, originalRef);
                PotionOwnerField?.SetValue(originalRef, player);
            }
            Log.Write($"RestorePotions: slot[{i}] {(oldPotion?.Id.ToString() ?? "empty")} -> {(originalRef?.Id.ToString() ?? "empty")}");
        }
    }

    private void RestoreCombatHistory()
    {
        if (_savedHistoryEntries == null)
        {
            Log.Write("RestoreCombatHistory: _savedHistoryEntries is NULL (was not captured)");
            return;
        }

        var cm = CombatManager.Instance;
        if (cm == null || CmHistoryProperty == null || HistoryEntriesField == null)
        {
            Log.Write($"RestoreCombatHistory: cm={cm != null}, histProp={CmHistoryProperty != null}, entriesField={HistoryEntriesField != null}");
            return;
        }

        var history = CmHistoryProperty.GetValue(cm);
        if (history == null)
        {
            Log.Write("RestoreCombatHistory: history object is NULL");
            return;
        }

        var rawEntries = HistoryEntriesField.GetValue(history);
        if (rawEntries is System.Collections.IList entries)
        {
            int oldCount = entries.Count;
            entries.Clear();
            foreach (var entry in _savedHistoryEntries)
                entries.Add(entry);
            Log.Write($"RestoreCombatHistory: {oldCount} -> {entries.Count} entries");
        }
        else
        {
            Log.Write($"RestoreCombatHistory: _entries is not IList (null={rawEntries == null})");
        }
    }

    private void ReviveKilledCreatures(CombatState cs)
    {
        // Find creatures that existed when the snapshot was taken but are no longer
        // in CombatState (they were killed and removed during play)
        var existingIds = new HashSet<uint>();
        foreach (var c in cs.Creatures)
        {
            if (c.CombatId != null)
                existingIds.Add(c.CombatId.Value);
        }

        foreach (var saved in _creatureStates)
        {
            if (existingIds.Contains(saved.CombatId))
                continue; // Creature still exists in combat

            var creature = saved.CreatureRef;
            if (creature == null)
                continue;

            Log.Write($"ReviveKilledCreatures: reviving creature {saved.CombatId}");

            // 1. Re-attach to combat state (was set to null on death)
            creature.CombatState = cs;

            // 2. Restore HP/MaxHp/Block BEFORE adding to combat
            //    so IsDead returns false when visual is created
            CreatureHpField.SetValue(creature, saved.CurrentHp);
            CreatureMaxHpField.SetValue(creature, saved.MaxHp);
            CreatureBlockField.SetValue(creature, saved.Block);

            // 3. Re-add to CombatState's internal lists (_enemies or _allies).
            //    CombatManager.AddCreature requires the creature to already be in these lists.
            //    CombatState.RemoveCreature removed it on death.
            if (creature.Side == CombatSide.Enemy)
            {
                var enemies = CsEnemiesField?.GetValue(cs) as List<Creature>;
                if (enemies != null && !enemies.Contains(creature))
                    enemies.Add(creature);
            }
            else
            {
                var allies = CsAlliesField?.GetValue(cs) as List<Creature>;
                if (allies != null && !allies.Contains(creature))
                    allies.Add(creature);
            }

            // 4. Subscribe to StateTracker and set up monster via CombatManager.
            //    The monster still has its original MoveStateMachine — the setter
            //    throws if already set. Null it out first; RestoreMonsterMoves will restore
            //    the correct state afterwards.
            if (creature.Monster != null && MonsterMoveStateMachineField != null)
            {
                MonsterMoveStateMachineField.SetValue(creature.Monster, null);
                Log.Write($"ReviveKilledCreatures: cleared _moveStateMachine for creature {saved.CombatId}");
            }
            CombatManager.Instance.AddCreature(creature);

            // 5. Restore powers (they were stripped by RemoveAllPowersAfterDeath)
            RestorePowers(creature, saved.Powers);

            // 6. Restore monster RNG and move state
            if (creature.Monster != null)
            {
                if (_monsterRngStates.TryGetValue(saved.CombatId, out var rngState))
                    MonsterRngField.SetValue(creature.Monster,
                        new Rng(rngState.seed, rngState.counter));
                RestoreMonsterMoves(creature.Monster, saved.CombatId);
            }

            // 7. Create new visual node (old one was QueueFree'd on death)
            NCombatRoom.Instance?.AddCreature(creature);

            // 8. Restore visual position and play revive animation
            var nCreature = NCombatRoom.Instance?.GetCreatureNode(creature);
            if (nCreature != null)
            {
                // Restore saved position (AddCreature may place at wrong slot for auto-layout)
                if (saved.VisualGlobalPosition.HasValue)
                    nCreature.GlobalPosition = saved.VisualGlobalPosition.Value;
                nCreature.StartReviveAnim();
            }
        }
    }

    private void RemoveSummonedCreatures(CombatState cs)
    {
        // Find creatures that exist now but didn't exist when this snapshot was taken
        var toRemove = new List<Creature>();
        foreach (var creature in cs.Creatures)
        {
            if (creature.CombatId != null && !_creatureCombatIds.Contains(creature.CombatId.Value))
                toRemove.Add(creature);
        }

        if (toRemove.Count == 0) return;

        var enemies = CsEnemiesField?.GetValue(cs) as List<Creature>;
        var allies = CsAlliesField?.GetValue(cs) as List<Creature>;

        foreach (var creature in toRemove)
        {
            Log.Write($"RemoveSummonedCreatures: removing creature {creature.CombatId}");

            // Remove from the CombatState creature lists
            enemies?.Remove(creature);
            allies?.Remove(creature);

            RemoveCreatureVisual(creature);

            // Unsubscribe from state tracker
            CombatManager.Instance?.StateTracker?.Unsubscribe(creature);

            // Detach from combat state
            creature.CombatState = null;
        }

        // Notify UI that creature list changed
        try
        {
            var creaturesChangedDelegate = CsCreaturesChangedField?.GetValue(cs) as Action<CombatState>;
            creaturesChangedDelegate?.Invoke(cs);
        }
        catch (Exception ex)
        {
            Log.Write($"RemoveSummonedCreatures: CreaturesChanged event ERROR: {ex}");
        }
    }

    private void RestoreRunRng()
    {
        var runManager = RunManager.Instance;
        if (runManager == null) return;

        var runState = RunManagerStateProperty?.GetValue(runManager) as RunState;
        if (runState == null) return;

        var runRngSet = runState.Rng;
        if (runRngSet == null) return;

        var rngsDict = RunRngDictField?.GetValue(runRngSet) as Dictionary<RunRngType, Rng>;
        if (rngsDict == null) return;

        foreach (var (type, (seed, counter)) in _runRngStates)
            rngsDict[type] = new Rng(seed, counter);
    }

    /// <summary>
    /// Remove the NCreature visual node for a creature. Searches both active and
    /// removing creature node lists. Hides the node immediately and queues it for
    /// deletion.
    /// </summary>
    private static void RemoveCreatureVisual(Creature creature)
    {
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return;

            var nCreature = combatRoom.GetCreatureNode(creature);

            // Fallback: search _removingCreatureNodes if not in active list
            if (nCreature == null)
            {
                foreach (var nc in combatRoom.RemovingCreatureNodes)
                {
                    if (GodotObject.IsInstanceValid(nc) && nc.Entity == creature)
                    {
                        nCreature = nc;
                        break;
                    }
                }
            }

            if (nCreature == null)
            {
                Log.Write($"RemoveCreatureVisual: no visual node found for {creature.CombatId}");
                return;
            }

            Log.Write($"RemoveCreatureVisual: removing visual for {creature.CombatId}");

            // Hide immediately so it disappears this frame
            nCreature.Visible = false;

            // Remove from tracking (may throw — catch separately from QueueFree)
            try { combatRoom.RemoveCreatureNode(nCreature); }
            catch (Exception ex) { Log.Write($"RemoveCreatureVisual: RemoveCreatureNode error: {ex.Message}"); }

            nCreature.QueueFree();
        }
        catch (Exception ex)
        {
            Log.Write($"RemoveCreatureVisual: ERROR: {ex}");
        }
    }
}
