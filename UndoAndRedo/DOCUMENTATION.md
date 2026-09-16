# UndoAndRedo 2.1 — Combat rewind: snapshot fast path + deterministic replay

Press **Left Arrow** to undo the last player decision in combat, **Right Arrow** to redo it.

Targets Slay the Spire 2 **v0.107.1** (Godot 4.5 / .NET 9, HarmonyX). Singleplayer only.

See [LESSONS.md](LESSONS.md) for every bug found on the way here, its root cause, the testing method and the
compatibility audit — read it before changing the mod.

Two mechanisms, kept apart on purpose:

* **Replay path** (`RewindEngine`, the original 2.0 design): rebuild the run from the game's own combat replay
  and re-feed the recorded events. Always correct, ~0.4–1.5 s per undo. Redo always uses it (feeding the
  removed events into the live game, 50–300 ms).
* **Fast path** (`FastPath/`, 2.1): an in-place memento of the model object graph taken right before each
  player decision; undo writes it back into the same objects (~1 ms), verifies the checksum, and rebuilds the
  combat visuals from the model. ~100–120 ms per undo. Anything unverified falls back to the replay path.
  See [Fast path](#fast-path).

## Why the rewrite

Version 1 snapshotted game state field-by-field with reflection (HP, powers, piles, RNG counters, relic
counters, potions, …) and then tried to patch every Godot node back into a matching visual state. Every game
object with hidden state (power internals, relic dynamic vars, card-play queue, potion holders, end-turn flags)
was a separate bug, and multi-turn undo and potions were never reliable.

Version 2 patches **no state at all**. It reuses the machinery the game itself relies on for multiplayer
lockstep and for its built-in combat replay viewer:

| Game facility | Where | What it gives us |
|---|---|---|
| `CombatReplayWriter` | `RunManager.CombatReplayWriter` | Records, for the current map point, a full `SerializableRun` taken at room entry plus **every** event that drives combat: `GameAction` (card play, potion use/discard, end turn, ready-for-enemy-turn), `HookAction` (relic/power prompts), `ResumeAction` (an action continuing after a player choice), `PlayerChoice` (what was picked in any selection screen). |
| `NMainMenu.RunReplay` | debug menu | The game's own replay player: rebuild the run from the save, re-enter the map point, feed the events back through `ActionQueueSet.EnqueueWithoutSynchronizing` / `ResumeActionWithoutSynchronizing` / `PlayerChoiceSynchronizer.ReceiveReplayChoice`. We mirror this loop exactly. |
| `NetGameType.Replay` | `INetGameService.Type` | While the service reports `Replay`, synchronizers do not enqueue live requests, selection screens read the recorded choice instead of opening UI, and end-turn bookkeeping actions are taken from the stream. |
| `NonInteractiveMode` | `NonInteractiveMode.AutoSlayerCheck` | The game's headless bot mode: every `Cmd.Wait`, executor pause, SFX and music call becomes a no-op, so a whole combat replays in a few frames. |
| `NetFullCombatState` + `ChecksumTracker.GenerateChecksum` | multiplayer desync detection | A hash of all gameplay-relevant combat state (creatures, powers, piles, energy, potions, relics, orbs, all RNG counters). We use it to verify that a rewind reproduced the expected state. |

Determinism is guaranteed by the game: all RNG is seeded and counter-based (`Rng(seed, counter)`), the run save
carries every counter, and the same event stream fed in the same order produces the same state — that is what
multiplayer clients depend on.

## How undo works

```
live game                 recorder (ReplayRecorder)              rewind (RewindEngine)
─────────────────────     ───────────────────────────────────    ────────────────────────────────────
CombatReplayWriter        for each recorded event:                Left Arrow
  events[0..n)              • action id + root event index          1. copy events, pick target index
  serializableRun           • is the action still open?             2. fade out, RunManager.CleanUp()
                            • checksum before player decisions      3. RunState.FromSerializable(save)
                                                                    4. InitializeShared/RunLobby/SavedRun
                                                                       (Type reports Replay while feeding)
                                                                    5. Launch, NRun.Create, GenerateMap,
                                                                       fast-forward ids, LoadIntoLatestMapCoord
                                                                    6. wait for play phase of turn 1
                                                                    7. feed events[0..target) (Replay mode,
                                                                       NonInteractive, FastMode=Instant)
                                                                    8. wait until queue idle + play phase
                                                                    9. verify event count + checksum
                                                                   10. exit replay mode, fade in
```

### Choosing the truncation point

An **undo boundary** is a recorded `GameAction` event whose action is a player decision:
`PlayCardAction`, `UsePotionAction`, `DiscardPotionGameAction`, `EndPlayerTurnAction`,
`UndoEndPlayerTurnAction`, `ConsoleCmdGameAction`. Everything after a boundary (choices, resumes, hook
actions, the enemy turn) is a consequence of it and is undone together with it.

The kept prefix must be **closed**:

* No action that is still open in the live game (queued, executing, or waiting for a choice) may be kept.
  `ReplayRecorder` tracks every enqueued action and its `GameActionState`.
* No kept action may depend on a `ResumeAction` that lives in the removed suffix. Because the game can queue
  card B while card A is still executing, A's choice/resume events can be recorded *after* B. The recorder
  assigns every event its action id (the queue hands out ids sequentially, and a resume consumes one too),
  so a `ResumeAction` can be traced to the event that originally enqueued the action ("root"). If truncating
  before B would orphan A, the target moves back to an earlier boundary.

Undo during the enemy turn or mid-animation is allowed: the rebuild discards the in-flight work, and the
last boundary is the `EndPlayerTurnAction`, so the end-turn itself is undone.

### Redo

The removed suffix is split into self-contained segments (one per boundary, merged when resumptions spill
across). Segments are pushed on a redo stack together with the event count they expect to see live. Redo feeds
one segment through the same replay loop (no rebuild). Any new player action invalidates the stack, detected
by the live event count no longer matching. A trailing segment containing an unfinished action (undo pressed
while a choice screen was open) is not redoable.

### Verification

After a rewind the engine checks that the game recorded exactly `target` events during the replay (the replay
re-records itself through the same writer) and compares `NetFullCombatState` checksums against the one the
recorder took right before the undone action originally executed. A mismatch is logged and shown as a toast
but never blocks play.

## Fast path

On by default. The file `<user data>/logs/UndoAndRedo.fastpath` containing `off` disables it (replay only);
`shadow` is the dual-run research mode (trial restore, put the live state back, then replay as usual).
Results and timings go to `logs/UndoAndRedo.fastpath.log`.

**Capture** (`ReplayRecorder.OnBeforeActionExecuted` → `FastPath.CaptureBeforeDecision`): right before a player
decision starts executing, and only if that decision is the *only* queued action (no other queued actions, no
pending resumptions), `ModelSnapshot.Capture` walks every object reachable from the roots (run state, combat
manager, card db, action queue set/executor/synchronizers, checksum tracker, replay writer) and records every
instance field and array element — 900–1500 objects, 1–4 ms. Godot objects, delegates/events, tasks, threading
primitives, reflection metadata, loggers and canonical (immutable) models are neither walked nor restored.
Captures happen for live decisions and for decisions fed by the replay path alike, so undo stays fast after a
redo.

**Undo** (`FastPath.TryFastUndo`), all under a freeze-frame cover with SFX muted and `Engine.TimeScale` = 25:

1. Preconditions: action queue empty, executor idle, play phase, player side, no card play/selection in
   progress, a recorded checksum exists. Otherwise → replay path.
2. `Restore()` writes the memento back into the same instances (~1 ms). The `NetFullCombatState` checksum must
   equal the one recorded at capture time; on mismatch the live state (captured just before) is put back and
   the replay path runs.
3. The undone decision is still at the front of the restored queue: it is removed, the queue's next action id
   is set back to the decision's id, the executor's `CurrentlyRunningAction` is cleared, the game's replay log
   and the recorder's bookkeeping are cut to the kept prefix. Side effects the game applied when the decision
   was *enqueued* are undone by hand: a potion's `IsQueued` flag (set before its `UsePotionAction` is enqueued;
   the potion popup disables use/discard while it is set). From here on redo works exactly as after a
   replay-based undo. A new live decision after an undo drops the redo history.
4. `VisualRebuild.Rebuild`: the `NCombatRoom` node is replaced through `NRun._roomContainer` by a fresh one
   (`NCombatRoom.Create` + the room's own `OnCombatSetUp`), which builds creature nodes, HP/block/power
   displays, piles, energy counter and end-turn button from the model the way a new combat does. The
   background node is carried over from the old room instead of recreated: some bosses (Kaiser Crab) are
   drawn by the background and their monster models cache that node. Freeing the old room drops every event
   subscription its nodes held; once it is actually freed (end of frame), every model field still pointing
   at a freed Godot object is nulled (`ModelSnapshot.ClearDisposedGodotReferences`; such caches are lazy
   getters that re-resolve). Then: nodes of dead/removed creatures
   are dropped, creature screen positions are copied from the old room, hand cards are created from the hand
   pile, the end-turn button gets its `OnTurnStarted`, intents are refreshed, orb slots/orbs are placed, potion
   slots are synced to `Player.PotionSlots`, top-bar HP/gold and relic counters are refreshed, and
   `CombatStateTracker.NotifyCombatStateChanged` makes every listener recompute. The room becomes the active
   screen only after its UI has a state (doing it earlier crashes `NCombatUi.Enable`).
5. Wait until the hand shows every card at its resting position (min 5 frames), uncover.

If anything throws after step 2, the current room is made safe (`NCombatUi._state`) and the replay path takes
over — it rebuilds everything from the save and does not depend on the live state.

**Why the net service must report Replay while feeding**: redo feeds recorded events into the live game in
replay mode. If the service still reported `Singleplayer`, the combat manager would enqueue its own
`ReadyToBeginEnemyTurnAction` next to the recorded one (double enemy turn). `Patch_NetSingleplayerGameService_Type`
makes the game's own service report `Replay` while `ReplayModeActive`, for every singleplayer run.

Known gaps of the fast path (all fall back to replay or are cosmetic): decisions made while another action was
still executing (queued card plays) get no memento; Defect orbs are placed best-effort (not tested); the old
room's pooled `NCard` nodes are freed with the room instead of returned to the pool (a few entries per undo).

## Files

```
UndoAndRedo/
  UndoAndRedoMod.cs         Entry point, logging, toast, Harmony patches
  ReplayRecorder.cs         Event/id/open-action tracking, checksums, mementos, boundary + segment analysis
  RewindEngine.cs           Undo/redo orchestration: fast path dispatch, teardown, rebuild, feed, settle, verify
  ShadowSnapshot.cs         Research tool: path→value model diff on a worker thread (logs/UndoAndRedo.shadow)
  FastPath/FastPath.cs      Mode file, capture validity, TryFastUndo, queue detach, shadow trial
  FastPath/ModelSnapshot.cs In-place memento of an object graph (capture/restore)
  FastPath/VisualRebuild.cs Fresh combat room + hand/intents/orbs/potions/top bar from the model
  FastPath/ScreenCover.cs   Freeze-frame cover used by the fast path
  SelfTest.cs               Automated end-to-end test (only runs when logs/UndoAndRedo.selftest exists)
  UndoAndRedo.json          Mod manifest (DLL only, no PCK needed on 0.107+)
```

### Harmony patches

| Target | Kind | Purpose |
|---|---|---|
| `NGame._Input` | prefix | Left/Right arrow → undo/redo |
| `CombatReplayWriter.WriteReplay` | prefix | Skip the replay disk write during our own teardown |
| `NHandCardHolder.AnimPosition/AnimAngle/AnimScale` | prefix | Move hand cards instantly while replaying or while the fast path rebuilds the hand. Their per-frame `Lerp(target, delta*k)` has no weight clamp and diverges to infinity under any time scale > 1 (that was the "flashing screen" bug: a card frame stretched across the whole screen) |
| `NetSingleplayerGameService.Type` | postfix (getter) | Report `Replay` while events are being fed. A patch instead of a wrapper class: a class implementing `INetGameService` stopped the mod from loading on the beta branch when the interface gained `LocalVersion` |
| `PreloadManager.LoadRoomCombatAssets` | prefix | Skip the room asset preload (already resident) during replay |
| `NTransition.RoomFadeIn` | prefix | Suppress the game's fade-in while we rebuild behind a black screen |
| `RunManager.Launch` | postfix | Attach the recorder to the new run's queue/executor/writer |
| `RunManager.CleanUp` | prefix | Detach recorder, drop redo history (except during our own rebuild) |
| `NMainMenu._Ready` | postfix | Self-test trigger only |

### Reflection (private members we touch)

* `CombatReplayWriter._replay` — the live `CombatReplay` (read; the fast path truncates its `events`).
* Fast path: `ActionQueueSet._actionQueues` / `ActionQueue.actions` / `_actionsWaitingForResumption` /
  `_nextId`, `ActionExecutor.CurrentlyRunningAction` (setter), `NRun._roomContainer`, `NCombatRoom.OnCombatSetUp`,
  `NCombatUi._state`, `NEndTurnButton.OnTurnStarted`, `CombatStateTracker.NotifyCombatStateChanged`,
  `NPotionContainer._holders`, `NOrbManager._orbs` / `OnCombatSetup`, `NRelicInventoryHolder.RefreshAmount` /
  `RefreshStatus`, `NTopBarHp.UpdateHealth`, `NTopBarGold.UpdateGold`, `NHandCardHolder._targetPosition`.
  Startup logs a "FastPath reflection:" line naming any missing member.
* `RunManager.State` setter, `RunManager.InitializeShared` / `InitializeRunLobby` / `InitializeSavedRun` —
  equivalent of the public `SetUpSavedSingleplayer`, but with our own net service and without bumping the
  save file's reload counter. `InitializeShared` parameters are bound by name so added parameters do not
  break the call. Startup logs a "Reflection:" diagnostic line for every member.

## Limitations

* **Event combats** (fights started from an event, room stack depth > 1) work through the fast path only.
  The game's replay initial state is taken at the map point, i.e. before the event's choices, so the replay
  path cannot rebuild the room stack; when no verified memento can be applied the undo is refused with a
  toast instead of falling back. Both kinds are handled: fights that get their own top-level combat room
  (e.g. Battleworn Dummy) and combat-layout events whose combat room is embedded in the event layout
  (Punch-Off, The Architect, The Lantern Key) — there the embedded room is swapped inside the layout.
* **Other mods that act on the combat state** react to an undo as they would to any state change. Known:
  SpeedX's "auto end turn when there are no playable cards" ends the turn again right after an end-of-turn
  is undone (the restored state has no playable cards by definition). Turn that option off to undo end turns.
* Singleplayer only. Multiplayer would need all peers to rewind together.
* A replay-path rewind costs a full room rebuild (~0.4 s, plus ~0.12 s per earlier turn replayed); the fast
  path costs ~0.1 s regardless of depth. Undo depth is unbounded within a combat.
* Cosmetic: enemy screen positions are re-randomised on rebuild; the engine restores the previous
  positions by combat id. Music keeps playing through the rebuild because `NonInteractiveMode` suppresses the
  music controller.

## Logging and debugging

`<user data>/logs/UndoAndRedo.log` is rewritten every session and, by default, stays small: startup reflection
report, one line per undo/redo (path taken, time, depth), warnings (checksum mismatch, fallback reasons, event
count divergence) and exceptions. That is what to ask a player for in a bug report.

Create the empty file `<user data>/logs/UndoAndRedo.debug` to turn on the full trace: event dumps, undo target
choice, per-step timings of the rebuild, memento captures/restores, replay feed progress, settle/visual waits,
and the separate `logs/UndoAndRedo.fastpath.log`. The flag is re-read every 2 s, so it can be created while the
game runs, right before reproducing a problem, and deleted afterwards. All other diagnostics are flag files too
and are off unless present: `UndoAndRedo.fastpath` (`off` / `shadow`), `UndoAndRedo.shadow` (model diffs),
`UndoAndRedo.capture` (per-frame screenshots while covered), `UndoAndRedo.selftest*` (see below).

## Self-test

Create an empty file `<user data>/logs/UndoAndRedo.selftest` (user data is `%APPDATA%\SlayTheSpire2`) and
start the game. With a saved run the mod continues it (travelling to the next monster node if needed); without
one, or if `logs/UndoAndRedo.selftest.fresh` exists, it starts an unsaved Ironclad run (seed from
`logs/UndoAndRedo.selftest.seed`, default `UNDOTEST`). `logs/UndoAndRedo.selftest.encounter` containing an
encounter id (e.g. `KAISER_CRAB_BOSS`) jumps straight into that fight, like the `fight` console command;
`logs/UndoAndRedo.selftest.event` containing `EVENT_ID:i[,j...]` (e.g. `PUNCH_OFF:1,0`) enters that event and
picks the given options in order, the last of which must start a fight. Disable mods that act on their own
(ModLaunchManager's launcher, SpeedX's auto end turn) in `settings.save` while testing. It
plays up to 3 cards per turn for N turns (`logs/UndoAndRedo.selftest.turns`, default 8; use 2–3 for the
starter deck or the fight ends early) through the real action path, undoes everything step by step, redoes
everything, undoes once more, plays one card live and undoes that, compares checksums after every step, checks
that the screen matches the model (hand cards and order, living creatures, potions), saves a screenshot per
step to `logs/selftest/`, writes `SELFTEST RESULT: PASS|FAIL` to `logs/UndoAndRedo.log` and quits. Back up
the profile's saves first when testing on a saved run.

## Test status (2026-09-14, v0.107.1)

Fast path, seven scenarios (starter fights with two seeds, potion use, Kaiser Crab boss, Battleworn Dummy
event fight, Punch-Off combat-layout event fight), each 8–12 decisions
undone one by one, redone, undone again after a redo, and undone after live play; every step checksum-verified,
screen consistent with the model, all PASS. One full real run by the player with no defects. Representative run
(2 turns / 8 decisions): 8 fast undos at 115–118 ms each
(restore + checksum 25 ms, room rebuild 10 ms, the rest is the 5-frame visual settle), 8 redos at 100–200 ms,
final undo and undo-after-live-play at ~90–107 ms, all checksums equal to the recorded ones, screen consistent
with the model at every step, PASS. Shadow mode earlier: 8/8 trial restores reproduced the expected checksum
with zero field differences in the model diff.

Replay path (unchanged):

Self-test passed nine times (clean, and with BaseLib / QuickRestart / Loadout / JmcModLib / BonModConfig /
BetterSaveSlots / BetterSpire2 / intentgraph2 loaded): 12 player decisions across 3 turns (and 32 across 8 turns) undone one by one
and redone one by one, every step checksum-verified, final checksum equal to the pre-undo one. Not yet
exercised by the automated test: potions, card-selection prompts, event combats.

### Performance

Measured on an 8-turn fight (32 player decisions) after the speed work:

| Operation | Time |
|---|---|
| Undo within turn 1 (nothing to replay) | ~260 ms |
| Undo at turn 4 (3 turns replayed) | ~650 ms |
| Undo at turn 8 (7 turns replayed) | ~1.2 s |
| Redo of one decision | 10–100 ms |

What the speed work does while replaying (all restored afterwards): a frozen copy of the last frame is drawn on a
top CanvasLayer (no fade to black), `Engine.TimeScale` = 25 so tween/timer waits collapse, hand card holders are
snapped to their targets (see patches), the game's asset preloads (each ends in a full `GC.Collect`) and the
replay disk write are skipped during the rebuild. Before uncovering, the engine waits until the hand shows every
card of the model within 2 px of its target. Measured in play on v0.107.1: undo 400–510 ms (elite fights, turn 1),
redo 85–120 ms.

Things that were tried and reverted: stopping the render loop (`RenderingServer.RenderLoopEnabled`, flickers on
some setups), toggling vsync, `Engine.PhysicsTicksPerSecond` = 1 (crashes the engine), the game's
`NFullscreenTextVfx` as a toast (it is a full-screen flash), and suppressing `NTransition.RoomFadeIn` (not needed).

Remaining cost is real CPU work, not waiting: ~260 ms fixed (`NRun.Create` ~90 ms, combat start and first hand
draw ~100 ms, teardown/launch/map ~70 ms) plus ~120 ms per replayed turn, almost all of it the game
instantiating card/banner nodes and running hooks and checksums at turn transitions. Idle frames cost under
1 ms; the turn-transition frames cost 10–17 ms each. Going lower would mean suppressing the game's own UI
construction during replay or keeping the `NRun` scene alive across the rewind, both of which give up the
"the game builds its own state and visuals" guarantee.

Do not try to speed up physics (`Engine.PhysicsTicksPerSecond` = 1 crashed the engine with unbounded memory
growth); physics is left untouched.

## Publishing

Steam Workshop item: see README. Package layout `nexus_packages/workshop/UndoAndRedo/content/UndoAndRedo/{dll,json}`
plus `description.txt` (Steam BBCode) and `preview.png`; upload with `tools/WorkshopUploader` (`create` for the
first upload, `update --item <id>` afterwards; Steam must be running and logged in).

## Build & deploy

```bash
"C:/Users/Jiesi Luo/.dotnet9/dotnet" build UndoAndRedo/UndoAndRedo.csproj -c Release \
  '-p:STS2GameDir=D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2'
cp UndoAndRedo/bin/Release/net9.0/UndoAndRedo.dll UndoAndRedo/UndoAndRedo.json \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/"
```

## Maintenance

When a game update breaks things, check `logs/UndoAndRedo.log` first: the "Reflection:" lines at startup name
any missing member; with `logs/UndoAndRedo.debug` present every rewind logs the event dump, the chosen target,
settle diagnostics and the checksum result. The replay loop in `RewindEngine.FeedEvents` should be kept identical to the game's
`NMainMenu.RunReplay`; diff it after each update. Decompile with:

```bash
ilspycmd "<game>/data_sts2_windows_x86_64/sts2.dll" -r "<game>/data_sts2_windows_x86_64" > sts2.cs
```
