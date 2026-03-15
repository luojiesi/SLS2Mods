# UndoAndRedo — STS2 Combat Undo/Redo Mod

Combat undo/redo for Slay the Spire 2. Press **Left Arrow** to undo, **Right Arrow** to redo.

## Credits

Inspired by [**Undo the Spire**](https://github.com/filippobaroni/undo-the-spire) for Slay the Spire 1, created by **filippobaroni** ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3354673683)). The STS1 mod used the Save State Mod (STSStateSaver) under the hood, with significant patches to make save/restore faithful for human play. This STS2 version is a ground-up rewrite for the Godot/.NET architecture but follows the same core concept of full combat state snapshots.

## Architecture

**Approach:** Direct state snapshots (same idea as the STS1 mod). Before each player action, the entire combat state is snapshotted. On undo, the previous snapshot is restored directly — no replay.

**Why this works in STS2:** The game's combat model layer (`CombatState`, `Creature`, `PlayerCombatState`, `CardModel`, `PowerModel`, etc.) is pure data with no Godot Node references. Visual nodes (NCreature, NCard, NPlayerHand, etc.) are separate and can be refreshed after restoring the model.

```
UndoAndRedo/
  UndoAndRedo.csproj     .NET 9 project referencing sts2.dll, 0Harmony.dll, GodotSharp.dll
  UndoAndRedoMod.cs       Entry point, undo/redo logic, Harmony patches, visual refresh
  CombatSnapshot.cs        State capture & restore (all mid-combat state)
  mod_manifest.json        PCK manifest for Godot resource pack generation
```

## Technology Stack

- **Game:** Slay the Spire 2 (Godot 4.5.1 / C# / .NET 9.0)
- **Patching:** HarmonyX 2.4.2 (runtime method patching)
- **Deployment:** `.dll` + `.pck` (Godot resource pack) placed in `<game>/mods/`
- **Mod entry:** `[ModInitializer("Initialize")]` attribute on static class

## Build & Deploy

```bash
# Build
cd UndoTheSpire
"C:/Users/Jiesi Luo/.dotnet9/dotnet" build UndoAndRedo.csproj -c Release \
  '-p:STS2GameDir=D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2'

# Generate PCK (from parent directory)
cd ..
python create_pck.py UndoAndRedo/mod_manifest.json \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/UndoAndRedo.pck"

# Deploy DLL (game must be closed)
cp UndoAndRedo/bin/Release/net9.0/UndoAndRedo.dll \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/UndoAndRedo.dll"
```

## How It Works

### Snapshot Lifecycle

1. **TakeSnapshot()** — called by Harmony prefix patches before player actions
2. Pushes a `CombatSnapshot` onto `UndoStack`, clears `RedoStack`
3. **Undo()** — Left Arrow pressed while action queue is idle
   - Captures current state → pushes to `RedoStack`
   - Pops previous snapshot from `UndoStack`
   - Sets `IsRestoring = true` (prevents recursive snapshots)
   - Calls `snapshot.Restore()` to write all state back
   - Calls `RefreshAllVisuals()` to sync UI
4. **Redo()** — same idea, swapping stacks

### What Gets Snapshotted

| State | Game Type | Capture Method | Restore Gotchas |
|-------|-----------|----------------|-----------------|
| HP, MaxHP, Block | `Creature._currentHp/MaxHp/_block` | Copy ints via reflection | None |
| Powers (buffs/debuffs) | `Creature._powers` (List\<PowerModel\>) | Save PowerData (Id, Amount, AmountOnTurnStart, SkipNextDurationTick) | Must rebuild NPowerContainer visuals (see below) |
| Card piles (hand, draw, discard, exhaust, play) | `CardPile._cards` | Save List\<CardModel\> **references** (not clones) | Preserves NCard visual bindings; mutable card state saved separately |
| Card mutable state | CardModel fields (cost, keywords, flags) | `MutableClone()` per card, then field-by-field copy back | Skip identity fields (`_cloneOf`, `_owner`, `Id`, etc.) |
| Energy & Stars | `PlayerCombatState._energy/_stars` | Copy ints | None |
| Orbs | `OrbQueue._orbs` | `MutableClone()` each | None |
| Pets | `PlayerCombatState._pets` | Save CombatId refs | Lookup existing Creature objects by ID |
| Round number & side | `CombatState.RoundNumber/CurrentSide` | Copy | Fire TurnStarted event to refresh end turn button |
| Monster RNG | `MonsterModel._rng` (Rng) | Save (Seed, Counter), reconstruct with `new Rng(s, c)` | None |
| Monster move state | `MonsterMoveStateMachine._currentState, _performedFirstMove`, StateLog, MoveState._performedAtLeastOnce | Save state IDs + bools | Use `ForceCurrentState()` and `SetMoveImmediate()` |
| Monster intent | `MonsterModel.NextMove` | Saved as state ID | Call `nCreature.RefreshIntents()` after restore |
| Run RNG | `RunRngSet._rngs` (Dict\<RunRngType, Rng\>) | Save all (Seed, Counter) pairs | None |
| Relics | `RelicModel` | StackCount, IsWax, IsMelted, Status, DynamicVars.Clone() | Some relics may not support DynamicVars cloning |
| Potions | `Player._potionSlots` | **MutableClone()** each PotionModel | Must clone (game mutates originals); set `_owner` back to player, clear `HasBeenRemovedFromState` |
| Combat history | `CombatHistory._entries` | Shallow copy list (entries are immutable records) | Needed for per-turn counters (e.g., MementoMori discard count) |

### Harmony Patches

| Patch Target | Type | Purpose |
|-------------|------|---------|
| `NGame._Input` | Prefix | Intercept Left/Right Arrow keys for undo/redo |
| `PlayCardAction` ctor | Prefix | Snapshot before playing a card |
| `EndPlayerTurnAction` ctor | Prefix | Snapshot before ending turn |
| `UsePotionAction` ctor | Prefix | Snapshot before using a potion |
| `DiscardPotionGameAction` ctor | Prefix | Snapshot before discarding a potion |
| `CombatManager.Reset()` | Postfix | Clear undo/redo stacks when combat ends |

### Guard Conditions

Undo/redo is only allowed when:
- In combat (`GetCombatState() != null`)
- It is the player's turn (`cs.CurrentSide == CombatSide.Player`) — undoing during enemy turn leaves the async enemy-turn flow running and corrupts state
- Not in a screen transition (`NGame.Instance.Transition.InTransition == false`)
- Action queue is idle (`RunManager.Instance.ActionQueueSet.IsEmpty`)
- Not already restoring (`IsRestoring == false`)

## Visual Refresh — The Hard Part

Restoring model state is straightforward. Making the UI match is the tricky part because STS2 visuals are event-driven Godot nodes that subscribe to model events. Direct field writes don't fire events.

### Hand Cards (`RefreshHandVisuals`)

Cards in hand are backed by `NCard` Godot nodes managed by `NPlayerHand`. We preserve card **identity** (same CardModel references across snapshots) so existing NCard nodes stay valid.

- Compare restored hand pile to current visual holders
- Remove NCard nodes for cards no longer in hand (`hand.Remove(card)`)
- Create NCard nodes for cards newly in hand (`NCard.Create()` + `hand.Add()`)
- Call `hand.ForceRefreshCardIndices()` to fix ordering

**Animation snapping** (`SnapHandPositions`): After adding/removing cards, NPlayerHand recalculates holder positions with tweened animations. We cancel these and snap to target positions instantly via:
- Cancel `_positionCancelToken` on each holder
- Set `Position` to `_targetPosition`
- Call `SetAngleInstantly()` and `SetScaleInstantly()`

The holder type is lazy-initialized from `hand.ActiveHolders[0].GetType()` since it's an internal type.

### Power Icons (`RefreshPowerVisuals`)

**Problem:** `NPowerContainer` subscribes to `Creature.PowerApplied`/`PowerRemoved` events. It has no rebuild method and `SetCreature()` throws if called twice. When we modify `_powers` directly, no events fire, so icons become stale.

**Solution:** Clear and rebuild.
1. Navigate scene tree: `NCreature` → `NCreatureStateDisplay._powerContainer` → `NPowerContainer`
2. Get `_powerNodes` list, `QueueFree()` each NPower node, clear the list
3. Call private `Add(PowerModel)` method for each power in the creature's restored powers

Key reflection targets:
- `NCreatureStateDisplay._powerContainer` (FieldInfo)
- `NPowerContainer._powerNodes` (List\<NPower\>)
- `NPowerContainer.Add(PowerModel)` (private method, creates NPower.Create + AddChild)

### Potion Visuals (`RefreshPotionVisuals`)

**Problem:** `NPotionContainer` subscribes to `Player.PotionProcured`/`PotionDiscarded`/`UsedPotionRemoved` events. `NPotionHolder` has internal state (`_disabledUntilPotionRemoved`, grayed `Modulate`, etc.) from the use/discard animation flow.

**Solution:** Full holder reset + rebuild.
1. Find `NPotionContainer` under `NRun.Instance` via recursive type search
2. For each holder:
   - Remove all NPotion children and `QueueFree()` them
   - Reset `<Potion>k__BackingField` to null
   - Reset `_disabledUntilPotionRemoved` to false
   - Reset holder `Modulate` and empty icon `Modulate` to `Colors.White`
3. If slot should have a potion: `NPotion.Create(potionModel)` + `holder.AddPotion(nPotion)`

**Critical:** Potions must be **cloned** during capture (`MutableClone()`), not referenced. The game mutates PotionModel objects when used (sets `HasBeenRemovedFromState`, clears `_owner`). On restore, set `_owner` back to the player.

### Pile Counts (`SyncPileCountDisplays`)

**Problem:** `NCombatCardPile` buttons (draw/discard/exhaust count displays) subscribe to `CardPile.CardAddFinished`/`CardRemoveFinished` events — NOT `ContentsChanged`. So `pile.InvokeContentsChanged()` does nothing for these buttons.

**Solution:** Recursively search `NCombatRoom.Instance` for `NCombatCardPile` nodes and directly set:
- `_currentCount` field to `pile.Cards.Count`
- `_countLabel` text via `SetTextAutoSize(count.ToString())`

### End Turn State (`ResetEndTurnState`)

**Problem:** After undo, the end turn button can become unresponsive. Multiple state fields contribute:
- `CombatManager._playersReadyToEndTurn` — if player is in this set, clicking End Turn dispatches `UndoEndPlayerTurnAction` instead of `EndPlayerTurnAction`
- `CombatManager.PlayerActionsDisabled` — if true, hand cards are disabled
- `NPlayerHand._currentCardPlay` — if non-null, `InCardPlay` returns true, `CanTurnBeEnded` returns false
- `NPlayerHand._currentMode` — if not `Mode.Play`, `CanTurnBeEnded` returns false

**Solution:** `ResetEndTurnState()` clears all of these before other visual refreshes. `PlayerActionsDisabled` is set via property setter to fire `PlayerActionsDisabledChanged` event (NPlayerHand subscribes to re-enable cards).

### Card Descriptions (`RefreshCardDescriptionsDeferred`)

**Problem:** Per-turn counters in card descriptions (e.g., "X damage this turn") show stale values after undo. The card model state is correct but the visual text hasn't been re-rendered. NCards added during `RefreshHandVisuals` may not be `IsNodeReady()` yet when `NotifyCombatStateChanged` fires, causing `UpdateVisuals` to silently return.

**Solution:** Use `Callable.From(...).CallDeferred()` to call `UpdateVisuals(PileType.Hand, CardPreviewMode.Normal)` on each NCard in the hand one frame later, after all nodes are ready.

### General UI Refresh

- `CombatStateTracker.NotifyCombatStateChanged("UndoAndRedo")` — refreshes energy counter, HP bars, block display
- `CombatManager.TurnStarted` event delegate — fire with current CombatState to refresh end turn button label

### Monster Intents

Call `nCreature.RefreshIntents()` (async, fire-and-forget) for each monster after restoring move state.

## Key Game Classes Reference

For future maintenance when game updates break things, here are the critical game types and their roles:

### Model Layer (pure data, safe to snapshot)
- `CombatState` — root combat state (RoundNumber, CurrentSide, Creatures, Allies)
- `Creature` — HP, block, powers, CombatId; has `.Monster` (MonsterModel) and `.Player` (Player)
- `PlayerCombatState` — energy, stars, card piles, orb queue, pets
- `CardModel` — has `MutableClone()`, `CreateClone()`, `ToSerializable()`
- `PowerModel` — has `MutableClone()` via `AbstractModel`; `AfterCloned()` wipes event subscribers and `_owner`
- `PotionModel` — has `MutableClone()`; `_owner` field, `HasBeenRemovedFromState` flag
- `MonsterModel` — `_rng`, `MoveStateMachine`, `NextMove`
- `RelicModel` — `StackCount`, `IsWax`, `IsMelted`, `Status`, `DynamicVars`
- `Rng` — just `(Seed, Counter)`, reconstructable via `new Rng(seed, counter)`
- `OrbModel` — cloneable via `MutableClone()`

### Visual Layer (Godot nodes, need manual refresh)
- `NPlayerHand` — manages hand card display; `.ActiveHolders`, `.Add()`, `.Remove()`, `.ForceRefreshCardIndices()`
- `NCard` — visual card node; `NCard.Create(CardModel, ModelVisibility)`
- `NCombatRoom` — combat scene; `.Instance`, `.GetCreatureNode(creature)`
- `NCreature` — creature visual; has `NCreatureStateDisplay._stateDisplay`
- `NCreatureStateDisplay` — holds `NPowerContainer._powerContainer`, `NHealthBar`
- `NPowerContainer` — subscribes to `Creature.PowerApplied/PowerRemoved`; has private `Add(PowerModel)`; no rebuild method
- `NPower` — individual power icon; subscribes to `PowerModel.DisplayAmountChanged/Flashed/Removed`
- `NPotionContainer` — subscribes to player potion events; holds `_holders` list
- `NPotionHolder` — has `AddPotion()`, `DiscardPotion()`; internal `_disabledUntilPotionRemoved`, `_emptyIcon`
- `NPotion` — visual potion; `NPotion.Create(PotionModel)`
- `NCombatCardPile` — pile count button; subscribes to `CardPile.CardAddFinished/CardRemoveFinished`

### Managers
- `CombatManager` — `.Instance`, `._state` (CombatState), `.History`, `.StateTracker`, `.TurnStarted` event
- `RunManager` — `.Instance`, `.State` (RunState), `.ActionQueueSet`
- `CombatStateTracker` — `.NotifyCombatStateChanged(string)` — triggers UI refresh

## Logging

Debug logging writes to `Desktop/UndoAndRedo.log` and `GD.Print` (Godot console). The `Log` class is in `CombatSnapshot.cs`. Diagnostic messages on startup check all reflection fields. Each capture/restore logs key details.

To disable logging for release, comment out or remove `Log.Write()` calls.

## Known Issues & Limitations

1. **Pile count display** — `SyncPileCountDisplays` searches recursively under `NCombatRoom.Instance`. If the pile buttons are parented elsewhere (e.g., under `NRun`), they won't be found. Check if `NCombatCardPileType` resolves to non-null at startup.
2. **No multiplayer support** — single-player only.
3. **Action queue must be idle** — can't undo mid-animation. This is by design to avoid corrupted state.
4. **Card identity preservation** — we store CardModel references (not clones) in pile snapshots. This means the same CardModel object must persist across snapshots. If the game ever replaces CardModel objects (rather than mutating them), this approach breaks.
5. **Power events not reconnected** — after clearing and rebuilding NPowerContainer, the new NPower nodes subscribe to the PowerModel's events. But NCreature also subscribes to each PowerModel's `Flashed` event for VFX. After undo, NCreature may have stale subscriptions to old PowerModel instances (cosmetic-only issue).

## Maintenance Guide

When a game update breaks the mod:

1. **Check reflection fields first.** Run the game with the mod, check `Desktop/UndoAndRedo.log` for the "Reflection Cache Diagnostics" block. Any `NULL` entry means a field/property was renamed or removed.

2. **Decompile the game.** Use ILSpy or dotnet-ilspycmd on `<game>/data_sts2_windows_x86_64/sts2.dll`:
   ```bash
   dotnet-ilspycmd "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64/sts2.dll" \
     -t MegaCrit.Sts2.Core.Entities.Creatures.Creature > Creature.cs
   ```

3. **Common breakage patterns:**
   - Field renamed → update the string in `AccessTools.Field(typeof(X), "fieldName")`
   - Type moved to different namespace → update `AccessTools.TypeByName("full.namespace.TypeName")`
   - Method signature changed → update `AccessTools.Method()` parameter types
   - New state added to combat → add capture/restore logic for the new fields
   - Visual node hierarchy changed → update `FindNodeOfType` search or reflection navigation

4. **Test incrementally.** Each section in `Restore()` is wrapped in try-catch, so one broken area won't crash the whole mod. Check logs for `ERROR in Restore*` messages.
