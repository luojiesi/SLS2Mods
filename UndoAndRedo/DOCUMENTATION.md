# UndoAndRedo 2.0 — Combat rewind by deterministic replay

Press **Left Arrow** to undo the last player decision in combat, **Right Arrow** to redo it.

Targets Slay the Spire 2 **v0.107.1** (Godot 4.5 / .NET 9, HarmonyX). Singleplayer only.

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
                                                                       with RewindNetGameService
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

## Files

```
UndoAndRedo/
  UndoAndRedoMod.cs         Entry point, logging, toast, Harmony patches
  ReplayRecorder.cs         Event/id/open-action tracking, checksums, boundary + segment analysis
  RewindEngine.cs           Undo/redo orchestration: teardown, rebuild, feed, settle, verify
  RewindNetGameService.cs   Singleplayer net service whose Type switches to Replay while feeding
  SelfTest.cs               Automated end-to-end test (only runs when logs/UndoAndRedo.selftest exists)
  UndoAndRedo.json          Mod manifest (DLL only, no PCK needed on 0.107+)
```

### Harmony patches

| Target | Kind | Purpose |
|---|---|---|
| `NGame._Input` | prefix | Left/Right arrow → undo/redo |
| `CombatReplayWriter.WriteReplay` | prefix | Skip the replay disk write during our own teardown |
| `PreloadManager.LoadRoomCombatAssets` | prefix | Skip the room asset preload (already resident) during replay |
| `NTransition.RoomFadeIn` | prefix | Suppress the game's fade-in while we rebuild behind a black screen |
| `RunManager.Launch` | postfix | Attach the recorder to the new run's queue/executor/writer |
| `RunManager.CleanUp` | prefix | Detach recorder, drop redo history (except during our own rebuild) |
| `NMainMenu._Ready` | postfix | Self-test trigger only |

### Reflection (private members we touch)

* `CombatReplayWriter._replay` (read only) — the live `CombatReplay`.
* `RunManager.State` setter, `RunManager.InitializeShared` / `InitializeRunLobby` / `InitializeSavedRun` —
  equivalent of the public `SetUpSavedSingleplayer`, but with our own net service and without bumping the
  save file's reload counter. `InitializeShared` parameters are bound by name so added parameters do not
  break the call. Startup logs a "Reflection:" diagnostic line for every member.

## Limitations

* **Event combats** (fights started from an event, room stack depth > 1) are refused with a toast. The
  game's replay initial state is taken at the map point, i.e. before the event's choices, so the room stack
  cannot be rebuilt from it. Supporting this needs a custom initial state taken at `CombatSetUp`
  plus manual room-stack reconstruction (`EnterRoomInternal(event, isRestoringRoomStackBase)` +
  `EnterRoomWithoutExitingCurrentRoom(combat)`).
* Singleplayer only. Multiplayer would need all peers to rewind together.
* A rewind costs a full room rebuild (~0.4 s, plus ~0.12 s per earlier turn replayed). Undo depth is unbounded within a combat.
* Cosmetic: enemy screen positions are re-randomised on rebuild; the engine restores the previous
  positions by combat id. Music keeps playing through the rebuild because `NonInteractiveMode` suppresses the
  music controller.

## Self-test

Create an empty file `<user data>/logs/UndoAndRedo.selftest` (user data is `%APPDATA%\SlayTheSpire2`) and
start the game with a save that is entering a fight. The mod continues the run, plays up to 3 cards per turn
for 3 turns through the real action path, undoes everything step by step, redoes everything, compares
checksums, writes `SELFTEST RESULT: PASS|FAIL` to `logs/UndoAndRedo.log` and quits. Back up the profile's
saves first; the run's reload counter is not touched but the fight is played.

## Test status (2026-09-14, v0.107.1)

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

What the speed work does while replaying (all restored afterwards): `RenderingServer.RenderLoopEnabled = false`
(logic keeps running, nothing is drawn, the window keeps showing the last frame — so no fade or screenshot is
needed), vsync off and fps cap lifted, `Engine.TimeScale` = 50 so tween/timer waits collapse, the game's
asset preloads (each ends in a full `GC.Collect`) and the replay disk write are skipped during the rebuild.

Remaining cost is real CPU work, not waiting: ~260 ms fixed (`NRun.Create` ~90 ms, combat start and first hand
draw ~100 ms, teardown/launch/map ~70 ms) plus ~120 ms per replayed turn, almost all of it the game
instantiating card/banner nodes and running hooks and checksums at turn transitions. Idle frames cost under
1 ms; the turn-transition frames cost 10–17 ms each. Going lower would mean suppressing the game's own UI
construction during replay or keeping the `NRun` scene alive across the rewind, both of which give up the
"the game builds its own state and visuals" guarantee.

Do not try to speed up physics (`Engine.PhysicsTicksPerSecond` = 1 crashed the engine with unbounded memory
growth); physics is left untouched.

## Build & deploy

```bash
"C:/Users/Jiesi Luo/.dotnet9/dotnet" build UndoAndRedo/UndoAndRedo.csproj -c Release \
  '-p:STS2GameDir=D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2'
cp UndoAndRedo/bin/Release/net9.0/UndoAndRedo.dll UndoAndRedo/UndoAndRedo.json \
  "D:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/"
```

## Maintenance

When a game update breaks things, check `logs/UndoAndRedo.log` first: the "Reflection:" lines at startup name
any missing member, and every rewind logs the event dump, the chosen target, settle diagnostics and the
checksum result. The replay loop in `RewindEngine.FeedEvents` should be kept identical to the game's
`NMainMenu.RunReplay`; diff it after each update. Decompile with:

```bash
ilspycmd "<game>/data_sts2_windows_x86_64/sts2.dll" -r "<game>/data_sts2_windows_x86_64" > sts2.cs
```
