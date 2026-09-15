# UndoAndRedo — lessons learned (2026-09-14, v0.107.1)

Companion to [DOCUMENTATION.md](DOCUMENTATION.md) (which describes the design as it is). This file records
*why* things are the way they are: every bug found on the way to 2.2.0, its root cause, the fix, and the
testing/compatibility knowledge that is not visible in the code. Read this first before changing the mod.

## Timeline in one paragraph

2.0 rewound by rebuilding the run from the game's own combat replay and re-feeding the recorded events (correct,
0.4–1.5 s). The player wanted ~0.3 s. Profiling showed the cost was real CPU work (card node creation, hooks,
checksums at turn transitions), so a second mechanism was built beside it: an in-place memento of the whole
model object graph (2.1, ~0.1 s per undo), validated in "shadow" mode against the replay path before being
switched on. 2.1.1 split the log into summary/trace; 2.2.0 added fights started from events.

## Bugs found, root causes, fixes

| Symptom | Root cause | Fix |
|---|---|---|
| Screen "flashing", hand cards flying across the screen, end-turn button stuck, cards unplayable after redo | `NHandCardHolder` moves with `Position.Lerp(target, delta * 7)` every frame; the weight is not clamped, so under `Engine.TimeScale` > 1 (or a slow frame) it overshoots to infinity. Hitbox is re-enabled only by that loop. | Harmony prefixes on `AnimPosition/AnimAngle/AnimScale` snap holders to their targets while replaying or rebuilding, and re-enable the hitbox. Wait until every holder is within 2 px of its target before uncovering. |
| Wrong hand (10 cards) / wrong boss HP after undo | The replay feeder ran ahead of the game: enqueued the next event while the previous action was paused for a player choice. | Lockstep feeding: wait for each action to start (end turn: finish; ready-for-enemy-turn: next player turn). |
| 60 s freeze during undo | A boss asked for a card choice during the enemy turn; the feeder was blocked waiting and never delivered the recorded choice. | Deliver Choice/Resume/Hook events in stream order *while* waiting on an action (`PumpNonActions`). |
| False checksum mismatches | `BeforeActionExecuted` fires again when an action resumes after a player choice; the "before" checksum was overwritten mid-action. | Capture only when `action.State == WaitingForExecution`. |
| Fast undo crashed with NRE in `NCombatUi.Enable` | `NRun.SetCurrentRoom` updates the active screen context immediately; with combat in progress that calls `Ui.Enable()` before `Ui.Activate(state)` ran. The game gets away with it because combat is not yet "in progress" when it creates a room. | Add the room through `NRun._roomContainer.SetCurrentScene`, activate the UI, *then* `ActiveScreenContext.Update()`. |
| Redo after a fast undo ran the enemy turn twice | Runs the game starts itself use its own `NetSingleplayerGameService`; in replay mode our wrapper is what makes `RequestEnqueue` drop the game's own `ReadyToBeginEnemyTurnAction`. Only runs rebuilt by us had the wrapper. | `RunManager.InitializeShared` prefix wraps the service for every singleplayer run. |
| Restored potion invisible | A fresh `NPotion` is transparent until the "newly acquired" animation fades it in. | Set its container visible when re-adding. |
| Potion could not be used or discarded after undoing its use | `PotionModel.EnqueueManualUse` sets `IsQueued = true` *before* enqueuing the action; the memento captured that flag; nothing cleared it after the restore; the popup disables both buttons while it is set. | After detaching the undone decision, call the game's own cancel path (`AfterUsageCanceled`, holder re-enable). General rule: side effects applied at *enqueue* time are inside the memento and must be undone by hand. |
| Kaiser Crab: both bosses invisible after undo, next hit threw `ObjectDisposedException` | The bosses are drawn by the background node (`NKaiserCrabBossBackground`); `Crusher`/`Rocket` cache that node lazily. Recreating the background left them pointing at a freed node, and the new background starts hidden (only `AfterAddedToRoom` shows it). | Carry the old background over into the fresh room. After the old room is freed, null every model field that still points at a freed Godot object (`ModelSnapshot.ClearDisposedGodotReferences`). |
| Misleading "redo N" in the toast | Redo entries were only invalidated lazily. | Drop the redo history as soon as a new live decision is recorded. |
| Undo took 115 ms, redo 100 ms | Per-line file writes on the hot path. | Two-level logging; trace only with `logs/UndoAndRedo.debug`. Now ~90 ms / ~40 ms. |
| Event fights: undo "worked" but the turn ended again immediately | Not a mod bug: SpeedX's "auto end turn when no playable cards" fires on the restored state. | Documented; disable SpeedX in self-tests. |

Things tried for speed and reverted: `RenderingServer.RenderLoopEnabled = false` (flickers on some machines),
vsync/fps-cap changes (replay is CPU-bound), `Engine.PhysicsTicksPerSecond = 1` (engine crash, unbounded memory
growth), `NFullscreenTextVfx` as a toast (it is a full-screen flash).

## Design decisions worth remembering

* **Two paths, kept apart.** `FastPath/` is dispatched from one place in `RewindEngine.UndoAsync`; the replay
  path never depends on it. Replay is the verified fallback everywhere except event fights (whose room stack
  it cannot rebuild).
* **Memento = every field of every reachable object, written back into the same instances.** No hand-picked
  state (that was v1's unfixable mistake). Godot objects, delegates, tasks, threading primitives and canonical
  models are untouchable. Because instances are preserved, event subscriptions from UI nodes stay valid.
* **Capture point**: right before a player decision executes, only when it is the *only* queued action (so
  "restore + detach the decision" equals the idle state before acting). Captured during replay feeding too, so
  undo stays fast after a redo.
* **Checksum is the oracle**: the memento restore must reproduce the checksum the recorder took before the
  decision; otherwise the live state is put back and replay runs. Nothing is ever applied unverified.
* **Visuals are rebuilt, not patched**: a fresh `NCombatRoom` built by the game's own code, plus what a new
  combat would not have (hand, intents, orbs, potions, top bar, relic counters, end-turn state). The background
  is the one node carried over.
* **Redo stays replay-based** (feeding the removed events into the live game, ~40 ms). A snapshot-based redo
  would save nothing measurable and add queue/recorder patch-ups.

## How the mod is tested

The self-test (`SelfTest.cs`, flag files under `logs/`) drives the game through the same calls the UI makes:
`ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target))`, `EndPlayerTurnAction`,
`UsePotionAction`, `EventSynchronizer.ChooseLocalOption`, `RunManager.EnterRoomDebug` / `EnterRoom`. Card choice
reads the model (`Hand.Cards` filtered by `card.CanPlay`, non-attacks first so the fight lasts). Every step
records the state checksum; after each undo/redo the checksum must match and the screen must agree with the
model (hand cards and order, living creatures, potions, no stuck potion flags). A screenshot per step goes to
`logs/selftest/` for eyeballing. Scenarios that pass on 2.2.0: starter fights (two seeds), potion use, Kaiser
Crab boss, Battleworn Dummy event fight, Punch-Off combat-layout event fight, plus one full real run.

Practicalities: launch only via `steam://rungameid/2868840`; the process is `SlayTheSpire2.exe`; overwriting the
DLL in `mods/` succeeds even while the game runs (so a successful copy proves nothing); disable ModLaunchManager
and SpeedX in `settings.save` for unattended runs and restore afterwards; `logs/UndoAndRedo.selftest.fresh`
keeps the player's saved run untouched.

## Compatibility audit (subscribed mods, 2026-09-14)

Decompiled every subscribed mod and checked Harmony targets, cached combat nodes, and net-service checks.
* No overlapping patch blocks another; other `NGame._Input` patches use F3 / PageUp / PageDown / R / Esc.
* No mod tests the concrete net-service type; Loadout treats `Replay` like `Singleplayer`.
* BetterSpire2 keys its creature labels by `Creature` and validates node instances before reuse — safe.
* intentgraph2 caches per `NCreature`; entries die with the old room in `_ExitTree` and are recreated on hover.
* SpeedX: auto end turn re-fires after an end-of-turn undo (inherent). Its other patches (animation speed,
  `NCombatRoom._Ready`, `NPlayerHand.Add`) run on the rebuilt room as on any room.
* General limit: the memento covers the game's model graph only. A mod that keeps per-combat gameplay state in
  its own statics would not be rewound. None of the current mods does.

## If a game update breaks it

1. Start the game once; `logs/UndoAndRedo.log` prints "Reflection:" / "FastPath reflection:" lines naming any
   missing member. Those are the private members the mod touches; re-decompile and re-map them.
2. Create `logs/UndoAndRedo.debug`, reproduce, read the trace: it says which path ran, the undo target, the
   checksum comparison and every rebuild step.
3. Run the self-test scenarios above before publishing.
