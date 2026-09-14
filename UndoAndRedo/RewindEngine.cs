using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace UndoAndRedo;

/// <summary>
/// Performs undo/redo by deterministic replay.
///
/// Undo:
///   1. Take the game's live <see cref="CombatReplay"/> (run save at map-point entry + every recorded event).
///   2. Pick the truncation point (last closed player decision, see <see cref="ReplayRecorder.ComputeUndoTarget"/>).
///   3. Fade out, tear the run down exactly like "quit to menu", rebuild it from the saved run, re-enter the
///      room (same encounter — the save carries the RNG state), and feed the kept events back in using the
///      same loop the game's own replay viewer uses, in non-interactive mode so waits/animations are skipped.
///   4. Wait until the queue is idle and the play phase is back, verify the state checksum, fade in.
/// Redo:
///   Feed the removed segment back in the same way, without rebuilding.
/// </summary>
internal static class RewindEngine
{
    // ── configuration ────────────────────────────────────────────────────────

    /// <summary>Run the replay in the game's NonInteractiveMode (skips all Cmd waits and executor frame polling).</summary>
    private const bool UseNonInteractiveReplay = true;
    private const float FadeSeconds = 0.2f;
    /// <summary>Hide the rebuild behind a frozen copy of the last frame instead of a fade to black.</summary>
    private const bool UseFreezeFrame = true;
    /// <summary>Engine.TimeScale while replaying: makes tweens/timers finish in one frame.</summary>
    private const double ReplayTimeScale = 50.0;
    /// <summary>Disable vsync / fps cap while replaying so per-frame awaits run as fast as the GPU allows.</summary>
    private const bool UncapFrameRateDuringReplay = true;
    /// <summary>
    /// Stop the render loop while replaying: engine logic keeps running but nothing is drawn, so frame-bound
    /// waits take a fraction of a millisecond and the window simply keeps showing the last frame.
    /// </summary>
    private const bool DisableRenderLoopDuringReplay = true;
    private const double CombatStartTimeoutSec = 20.0;
    private const double ReplaySettleTimeoutSec = 30.0;
    private const int SettleStableFrames = 2;

    // ── state ────────────────────────────────────────────────────────────────

    /// <summary>True while recorded events are being fed. Switches the net service to Replay and enables NonInteractiveMode.</summary>
    public static bool ReplayModeActive { get; private set; }

    private static bool _busy;
    private static bool _ownRebuild;
    /// <summary>True while our own teardown runs; CombatReplayWriter.WriteReplay is skipped (disk write we do not need).</summary>
    public static bool SkipReplayWrite => _ownRebuild;
    private static double _savedTimeScale = 1.0;
    private static int _savedMaxFps;
    private static DisplayServer.VSyncMode _savedVsync = DisplayServer.VSyncMode.Enabled;
    private static bool _coverShown;
    private static Func<bool>? _installedCheck;
    private static Func<bool>? _previousCheck;

    private sealed class RedoEntry
    {
        public required List<CombatReplayEvent> Segment;
        /// <summary>Number of recorded events that must exist right before this segment is fed.</summary>
        public required int PrefixCount;
        /// <summary>Checksum the state had after this segment originally ran (null if unknown).</summary>
        public uint? ExpectedChecksumAfter;
    }

    private static readonly List<RedoEntry> _redo = new();

    // ── reflection into RunManager (private setup methods) ───────────────────

    private static readonly PropertyInfo? StateProp = AccessTools.Property(typeof(RunManager), "State");
    private static readonly MethodInfo? InitializeSharedMethod = AccessTools.Method(typeof(RunManager), "InitializeShared");
    private static readonly MethodInfo? InitializeRunLobbyMethod = AccessTools.Method(typeof(RunManager), "InitializeRunLobby");
    private static readonly MethodInfo? InitializeSavedRunMethod = AccessTools.Method(typeof(RunManager), "InitializeSavedRun");

    public static void VerifyReflection()
    {
        Log.Write($"Reflection: RunManager.State = {(StateProp != null ? "OK" : "NULL")}, " +
                  $"InitializeShared = {(InitializeSharedMethod != null ? "OK" : "NULL")}, " +
                  $"InitializeRunLobby = {(InitializeRunLobbyMethod != null ? "OK" : "NULL")}, " +
                  $"InitializeSavedRun = {(InitializeSavedRunMethod != null ? "OK" : "NULL")}");
        if (InitializeSharedMethod != null)
            Log.Write("InitializeShared params: " + string.Join(", ", InitializeSharedMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")));
    }

    // ── public entry points ──────────────────────────────────────────────────

    public static void RequestUndo() => TaskHelper.RunSafely(UndoAsync());

    public static void RequestRedo() => TaskHelper.RunSafely(RedoAsync());

    /// <summary>Waits until the action queue is idle and the local player is in the play phase.</summary>
    internal static async Task<bool> WaitForIdlePlayPhase(double timeoutSec)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        return await WaitUntil(() => !cm.IsInProgress
                                     || (rm.ActionQueueSet.IsEmpty && !rm.ActionExecutor.IsRunning
                                         && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase
                                         && !cm.EndingPlayerTurnPhaseOne && !cm.EndingPlayerTurnPhaseTwo),
                               timeoutSec, "idle play phase");
    }

    public static void OnRunCleanUp()
    {
        if (_ownRebuild) return;
        if (_redo.Count > 0) Log.Write("Run cleaned up; clearing redo history");
        _redo.Clear();
    }

    private static bool CanStart(out string? toast)
    {
        toast = null;
        if (_busy || ReplayModeActive) return false;
        var rm = RunManager.Instance;
        if (!rm.IsInProgress || rm.IsGameOver) return false;
        var game = NGame.Instance;
        if (game == null || game.Transition.InTransition) return false;
        if (rm.NetService == null || rm.NetService.Type != NetGameType.Singleplayer)
        {
            toast = "Undo: singleplayer only";
            return false;
        }
        if (!CombatManager.Instance.IsInProgress)
            return false;
        var rs = rm.DebugOnlyGetState();
        if (rs?.CurrentRoom is not CombatRoom room)
            return false;
        if (rs.CurrentRoomCount != 1 || room.ParentEventId != null)
        {
            toast = "Undo: not available in event combats";
            return false;
        }
        if (ReplayRecorder.Current == null)
        {
            toast = "Undo: recorder not attached";
            return false;
        }
        return true;
    }

    // ── undo ─────────────────────────────────────────────────────────────────

    internal static async Task<bool> UndoAsync()
    {
        if (!CanStart(out var why)) { if (why != null) UndoAndRedoMod.Toast(why); return false; }
        _busy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var rec = ReplayRecorder.Current!;
            var replay = rec.Replay;
            if (replay == null)
            {
                UndoAndRedoMod.Toast("Undo: no replay data for this combat");
                return false;
            }
            if (replay.serializableRun == null || replay.serializableRun.PreFinishedRoom != null)
            {
                UndoAndRedoMod.Toast("Undo: unsupported room state");
                return false;
            }

            var events = replay.events.ToList();
            Log.Write($"=== UNDO requested: {events.Count} recorded events ===\n{rec.DumpEvents(events)}");

            int target = rec.ComputeUndoTarget(events, out var reason);
            if (target < 0)
            {
                Log.Write($"Nothing to undo: {reason}");
                UndoAndRedoMod.Toast("Nothing to undo");
                return false;
            }

            var prefix = events.Take(target).ToList();
            var segments = rec.SplitRedoSegments(events, target);
            uint? expected = rec.ChecksumBefore(target);
            uint? liveChecksum = rec.CurrentChecksum();
            Log.Write($"Undo target = {target} ({ReplayRecorder.Describe(events[target])}); keeping {prefix.Count} events, {segments.Count} redo segment(s), expected checksum = {(expected.HasValue ? expected.Value.ToString() : "n/a")}");

            var header = CloneHeader(replay);

            var ok = await RebuildAndReplay(header, prefix, expected);

            // Push redo segments on top of the existing stack so that the earliest one is popped first.
            // (Older entries stay valid: they are only reachable after the newer ones are redone.)
            int prefixCount = prefix.Count;
            var entries = new List<RedoEntry>();
            foreach (var seg in segments)
            {
                int end = prefixCount + seg.Count;
                uint? after = end < events.Count ? rec.ChecksumBefore(end) : (end == events.Count ? liveChecksum : null);
                entries.Add(new RedoEntry { Segment = seg, PrefixCount = prefixCount, ExpectedChecksumAfter = after });
                prefixCount = end;
            }
            for (int i = entries.Count - 1; i >= 0; i--) _redo.Add(entries[i]);

            int remaining = ReplayRecorder.Boundaries(prefix).Count;
            Log.Write($"=== UNDO {(ok ? "complete" : "FAILED")} in {sw.ElapsedMilliseconds} ms; undo depth left {remaining}, redo {_redo.Count} ===");
            UndoAndRedoMod.Toast(ok ? $"Undo  ({remaining} left, {_redo.Count} redo)" : "Undo failed — see UndoAndRedo.log");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write($"UNDO ERROR: {ex}");
            UndoAndRedoMod.Toast("Undo failed — see UndoAndRedo.log");
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    // ── redo ─────────────────────────────────────────────────────────────────

    internal static async Task<bool> RedoAsync()
    {
        if (!CanStart(out var why)) { if (why != null) UndoAndRedoMod.Toast(why); return false; }
        _busy = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (_redo.Count == 0)
            {
                UndoAndRedoMod.Toast("Nothing to redo");
                return false;
            }
            var rec = ReplayRecorder.Current!;
            var replay = rec.Replay;
            if (replay == null) { UndoAndRedoMod.Toast("Redo: no replay data"); return false; }

            var entry = _redo[^1];
            if (replay.events.Count != entry.PrefixCount)
            {
                Log.Write($"Redo invalidated: live events {replay.events.Count} != expected prefix {entry.PrefixCount}");
                _redo.Clear();
                UndoAndRedoMod.Toast("Nothing to redo");
                return false;
            }
            _redo.RemoveAt(_redo.Count - 1);

            Log.Write($"=== REDO: feeding {entry.Segment.Count} events ===\n{rec.DumpEvents(entry.Segment)}");
            var runState = rec.RunState!;

            await CoverScreen();
            var fastMode = SaveManager.Instance.PrefsSave.FastMode;
            bool ok;
            try
            {
                EnterReplayMode();
                var fed = await FeedEvents(entry.Segment, runState);
                ok = await WaitForSettle(fed);
            }
            finally
            {
                ExitReplayMode(fastMode);
            }
            await UncoverScreen();

            if (ok && replay.events.Count != entry.PrefixCount + entry.Segment.Count)
            {
                Log.Write($"WARNING: after redo, recorded events = {replay.events.Count}, expected {entry.PrefixCount + entry.Segment.Count}");
                ok = false;
            }
            if (ok && entry.ExpectedChecksumAfter.HasValue)
            {
                var actual = rec.CurrentChecksum();
                if (actual.HasValue && actual.Value != entry.ExpectedChecksumAfter.Value)
                {
                    Log.Write($"WARNING: state checksum mismatch after redo: expected {entry.ExpectedChecksumAfter.Value}, got {actual.Value}");
                    UndoAndRedoMod.Toast("Redo: state mismatch detected (see log)");
                }
                else if (actual.HasValue)
                {
                    Log.Write($"Checksum verified: {actual.Value}");
                }
            }

            int remaining = ReplayRecorder.Boundaries(replay.events).Count;
            Log.Write($"=== REDO {(ok ? "complete" : "FAILED")} in {sw.ElapsedMilliseconds} ms; undo depth {remaining}, redo {_redo.Count} ===");
            UndoAndRedoMod.Toast(ok ? $"Redo  ({remaining} undo, {_redo.Count} redo)" : "Redo failed — see UndoAndRedo.log");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Write($"REDO ERROR: {ex}");
            UndoAndRedoMod.Toast("Redo failed — see UndoAndRedo.log");
            return false;
        }
        finally
        {
            _busy = false;
        }
    }

    // ── rebuild + replay ─────────────────────────────────────────────────────

    private sealed class ReplayHeader
    {
        public required SerializableRun Save;
        public required uint NextActionId;
        public required uint NextHookId;
        public required List<uint> ChoiceIds;
        public required List<int> RewardIds;
    }

    private static ReplayHeader CloneHeader(CombatReplay replay) => new()
    {
        Save = replay.serializableRun,
        NextActionId = replay.nextActionId,
        NextHookId = replay.nextHookId,
        ChoiceIds = replay.choiceIds.ToList(),
        RewardIds = replay.rewardIds.ToList(),
    };

    private static async Task<bool> RebuildAndReplay(ReplayHeader header, List<CombatReplayEvent> prefix, uint? expectedChecksum)
    {
        var game = NGame.Instance!;
        var rm = RunManager.Instance;
        var fastMode = SaveManager.Instance.PrefsSave.FastMode;
        var enemyPositions = CaptureEnemyPositions();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long f0 = _frameCounter;
        string T() => $"[+{sw.ElapsedMilliseconds} ms, {_frameCounter - f0} frames]";

        await CoverScreen();
        Log.Write($"{T()} screen covered");

        bool ok = false;
        try
        {
            EnterReplayMode();

            // 1. Tear down (mirrors NPauseMenu "quit to menu" → RunManager.CleanUp).
            _ownRebuild = true;
            rm.ActionQueueSet.Reset();
            rm.CleanUp();
            _ownRebuild = false;
            Log.Write($"{T()} teardown complete");

            // 2. Rebuild the run from the save taken at map-point entry, with our switchable net service.
            var runState = RunState.FromSerializable(header.Save);
            var service = new RewindNetGameService();
            SetUpSavedRun(rm, runState, header.Save, service);
            rm.CombatStateSynchronizer.IsDisabled = true;
            game.ReactionContainer.InitializeNetworking(service);
            Log.Write($"{T()} run rebuilt: act={runState.CurrentActIndex} floor={runState.TotalFloor}");

            // 3. Enter the room the same way the game's replay viewer does.
            // Run/act assets are already resident (same character, same act); each of the game's preload
            // calls ends with a full GC.Collect, so skipping them saves a few hundred ms.
            rm.Launch();
            Log.Write($"{T()} launch");
            game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
            Log.Write($"{T()} NRun created");
            await rm.GenerateMap();
            Log.Write($"{T()} map generated");
            rm.ActionQueueSet.FastForwardNextActionId(header.NextActionId);
            rm.ActionQueueSynchronizer.FastForwardHookId(header.NextHookId);
            rm.PlayerChoiceSynchronizer.FastForwardChoiceIds(header.ChoiceIds);
            rm.RewardsSetSynchronizer.FastForwardRewardIds(header.RewardIds);
            await rm.LoadIntoLatestMapCoord(null);
            if (rm.MapDrawingsToLoad != null && NRun.Instance != null)
            {
                NRun.Instance.GlobalUi.MapScreen.Drawings.LoadDrawings(rm.MapDrawingsToLoad);
                rm.MapDrawingsToLoad = null;
            }
            Log.Write($"{T()} room entered: {rm.DebugOnlyGetState()?.CurrentRoom?.GetType().Name} {rm.DebugOnlyGetState()?.CurrentRoom?.ModelId}");

            // 4. Wait for the play phase of turn 1.
            var cmTrace = CombatManager.Instance;
            Action<CombatState> onSetUp = _ => Log.Write($"{T()}   combat set up");
            Action<CombatState> onTurnStarted = _ => Log.Write($"{T()}   turn started (round {cmTrace.DebugOnlyGetState()?.RoundNumber}, side {cmTrace.DebugOnlyGetState()?.CurrentSide})");
            Action<CombatState> onTurnEnded = _ => Log.Write($"{T()}   turn ended");
            cmTrace.CombatSetUp += onSetUp; cmTrace.TurnStarted += onTurnStarted; cmTrace.TurnEnded += onTurnEnded;
            try
            {
            if (!await WaitUntil(() => CombatManager.Instance.IsInProgress
                                       && rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase,
                                 CombatStartTimeoutSec, "combat start"))
                return false;
            Log.Write($"{T()} play phase reached");

            // 5. Feed the kept events and wait for everything to settle.
            var fed = await FeedEvents(prefix, runState);
            Log.Write($"{T()} events fed");
            ok = await WaitForSettle(fed);
            Log.Write($"{T()} settled");
            }
            finally
            {
                cmTrace.CombatSetUp -= onSetUp; cmTrace.TurnStarted -= onTurnStarted; cmTrace.TurnEnded -= onTurnEnded;
            }

            // 6. Verify.
            var rec = ReplayRecorder.Current;
            var live = rec?.Replay;
            if (live != null && live.events.Count != prefix.Count)
            {
                Log.Write($"WARNING: replay produced {live.events.Count} recorded events, expected {prefix.Count} — divergence likely");
                ok = false;
            }
            if (ok && expectedChecksum.HasValue && rec != null)
            {
                var actual = rec.CurrentChecksum();
                if (actual.HasValue && actual.Value != expectedChecksum.Value)
                {
                    Log.Write($"WARNING: state checksum mismatch after undo: expected {expectedChecksum.Value}, got {actual.Value}");
                    UndoAndRedoMod.Toast("Undo: state mismatch detected (see log)");
                }
                else if (actual.HasValue)
                {
                    Log.Write($"Checksum verified: {actual.Value}");
                }
            }
        }
        finally
        {
            _ownRebuild = false;
            ExitReplayMode(fastMode);
        }

        RestoreEnemyPositions(enemyPositions);
        await UncoverScreen();
        Log.Write($"{T()} screen uncovered");

        try
        {
            var rs = rm.DebugOnlyGetState();
            if (rs != null && rs.VisitedMapCoords.Count > 0)
            {
                await NextFrame();
                NMapScreen.Instance?.InitMarker(rs.VisitedMapCoords[^1]);
            }
        }
        catch (Exception ex) { Log.Write($"Map marker restore error: {ex.Message}"); }

        return ok;
    }

    /// <summary>
    /// Equivalent of RunManager.SetUpSavedSingleplayer but with our own net service and without
    /// bumping the save file's reload counter.
    /// </summary>
    private static void SetUpSavedRun(RunManager rm, RunState state, SerializableRun save, INetGameService service)
    {
        if (StateProp == null || InitializeSharedMethod == null || InitializeRunLobbyMethod == null || InitializeSavedRunMethod == null)
            throw new InvalidOperationException("RunManager private setup members not found (game update?)");

        StateProp.SetValue(rm, state);

        var input = new PeerInputSynchronizer(service);
        var ps = InitializeSharedMethod.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            bool known = true;
            args[i] = p.Name switch
            {
                "netService" => service,
                "inputSynchronizer" => input,
                "shouldSave" => true,
                "dailyTime" => save.DailyTime,
                "startTime" => save.StartTime,
                "runTime" => save.RunTime,
                "winTime" => save.WinTime,
                "numReloads" => save.NumReloads,
                _ => Unknown(p, out known),
            };
            if (!known)
                Log.Write($"WARNING: unknown InitializeShared parameter '{p.Name}' defaulted");
        }
        InitializeSharedMethod.Invoke(rm, args);
        InitializeRunLobbyMethod.Invoke(rm, new object[] { service, state });
        InitializeSavedRunMethod.Invoke(rm, new object[] { save });

        static object? Unknown(ParameterInfo p, out bool known)
        {
            known = false;
            if (p.HasDefaultValue) return p.DefaultValue;
            return p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        }
    }

    // ── event feeding (mirrors NMainMenu.RunReplay) ──────────────────────────

    private static async Task<List<GameAction>> FeedEvents(List<CombatReplayEvent> events, RunState runState)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        var fed = new List<GameAction>();
        int n = 0;
        var fsw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var ev in events)
        {
            n++;
            long evStart = fsw.ElapsedMilliseconds;
            switch (ev.eventType)
            {
                case CombatReplayEventType.GameAction:
                {
                    while (cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo)
                        await NextFrame();
                    var player = runState.GetPlayer(ev.playerId!.Value);
                    var action = ev.action!.ToGameAction(player);
                    if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                    {
                        while (cm.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy)
                            await NextFrame();
                    }
                    rm.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                    fed.Add(action);
                    if (action is EndPlayerTurnAction || action is ReadyToBeginEnemyTurnAction)
                    {
                        await rm.ActionExecutor.FinishedExecutingActions();
                        Log.Write($"    feed #{n} {action.GetType().Name}: waited {fsw.ElapsedMilliseconds - evStart} ms (t={fsw.ElapsedMilliseconds}, frames {_frameCounter})");
                    }
                    break;
                }
                case CombatReplayEventType.HookAction:
                {
                    var hook = rm.ActionQueueSynchronizer.GetHookActionForId(ev.hookId!.Value, ev.playerId!.Value, ev.gameActionType!.Value);
                    rm.ActionQueueSet.EnqueueWithoutSynchronizing(hook);
                    fed.Add(hook);
                    break;
                }
                case CombatReplayEventType.ResumeAction:
                    rm.ActionQueueSet.ResumeActionWithoutSynchronizing(ev.actionId!.Value);
                    break;
                case CombatReplayEventType.PlayerChoice:
                {
                    var player = runState.GetPlayer(ev.playerId!.Value);
                    rm.PlayerChoiceSynchronizer.ReceiveReplayChoice(player, ev.choiceId!.Value, ev.playerChoiceResult!.Value);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown replay event type {ev.eventType}");
            }
        }
        Log.Write($"Fed {n} events ({fed.Count} actions)");
        return fed;
    }

    /// <summary>Waits until the queue is idle, all fed actions are done, and the player is back in the play phase.</summary>
    private static async Task<bool> WaitForSettle(List<GameAction> fed)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        var start = Time.GetTicksMsec();
        int stable = 0;
        string last = "";
        while (true)
        {
            if (!cm.IsInProgress)
            {
                Log.Write("Settle: combat ended during replay — divergence");
                return false;
            }
            var cs = cm.DebugOnlyGetState();
            bool queueEmpty = rm.ActionQueueSet.IsEmpty;
            bool execIdle = !rm.ActionExecutor.IsRunning;
            bool playPhase = rm.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase
                             && !cm.EndingPlayerTurnPhaseOne && !cm.EndingPlayerTurnPhaseTwo
                             && cs?.CurrentSide == CombatSide.Player;
            int openFed = fed.Count(a => a.State is not (GameActionState.Finished or GameActionState.Canceled));
            bool fedDone = openFed == 0;

            if (queueEmpty && execIdle && playPhase && fedDone)
            {
                if (++stable >= SettleStableFrames)
                {
                    Log.Write($"Settled after {Time.GetTicksMsec() - start} ms (round {cs?.RoundNumber})");
                    return true;
                }
            }
            else
            {
                stable = 0;
                string now = $"queueEmpty={queueEmpty} execIdle={execIdle} playPhase={playPhase} openFed={openFed} side={cs?.CurrentSide} sync={rm.ActionQueueSynchronizer.CombatState}";
                if (now != last) { Log.Write("Settle: " + now); last = now; }
            }

            if (Time.GetTicksMsec() - start > ReplaySettleTimeoutSec * 1000)
            {
                Log.Write($"Settle TIMEOUT: {last}");
                return false;
            }
            await NextFrame();
        }
    }

    internal static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSec, string what)
    {
        var start = Time.GetTicksMsec();
        while (!condition())
        {
            if (Time.GetTicksMsec() - start > timeoutSec * 1000)
            {
                Log.Write($"Timeout waiting for {what}");
                return false;
            }
            await NextFrame();
        }
        return true;
    }

    private static long _frameCounter;
    internal static async Task NextFrame()
    {
        var game = NGame.Instance!;
        await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        _frameCounter++;
    }

    // ── replay mode switches ─────────────────────────────────────────────────

    private static void EnterReplayMode()
    {
        EnsureNonInteractiveHook();
        ReplayModeActive = true;
        SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
        try
        {
            _savedTimeScale = Engine.TimeScale;
            Engine.TimeScale = ReplayTimeScale;
            if (UncapFrameRateDuringReplay)
            {
                _savedMaxFps = Engine.MaxFps;
                _savedVsync = DisplayServer.WindowGetVsyncMode();
                Engine.MaxFps = 0;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            }
            if (DisableRenderLoopDuringReplay)
                RenderingServer.RenderLoopEnabled = false;
        }
        catch (Exception ex) { Log.Write($"Engine speed settings failed: {ex.Message}"); }
        Log.Write("Replay mode ON");
    }

    private static void ExitReplayMode(FastModeType restoreFastMode)
    {
        ReplayModeActive = false;
        SaveManager.Instance.PrefsSave.FastMode = restoreFastMode;
        try
        {
            if (DisableRenderLoopDuringReplay)
                RenderingServer.RenderLoopEnabled = true;
            Engine.TimeScale = _savedTimeScale;
            if (UncapFrameRateDuringReplay)
            {
                Engine.MaxFps = _savedMaxFps;
                DisplayServer.WindowSetVsyncMode(_savedVsync);
            }
        }
        catch (Exception ex) { Log.Write($"Engine speed settings restore failed: {ex.Message}"); }
        Log.Write("Replay mode OFF");
    }

    // ── screen cover: freeze the last frame while we work underneath ─────────

    private static CanvasLayer? _coverLayer;

    private static async Task CoverScreen()
    {
        _coverShown = false;
        if (DisableRenderLoopDuringReplay)
            return; // the last presented frame stays on screen while rendering is stopped
        if (UseFreezeFrame)
        {
            try
            {
                var game = NGame.Instance!;
                var img = game.GetViewport().GetTexture().GetImage();
                if (img != null && !img.IsEmpty())
                {
                    var tex = ImageTexture.CreateFromImage(img);
                    var rect = new TextureRect
                    {
                        Texture = tex,
                        MouseFilter = Control.MouseFilterEnum.Stop,
                        StretchMode = TextureRect.StretchModeEnum.Scale,
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    };
                    rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    _coverLayer = new CanvasLayer { Layer = 120 };
                    _coverLayer.AddChild(rect);
                    game.AddChild(_coverLayer);
                    _coverShown = true;
                    await NextFrame();
                    return;
                }
                Log.Write("Freeze frame: viewport image empty, falling back to fade");
            }
            catch (Exception ex)
            {
                Log.Write($"Freeze frame failed ({ex.Message}), falling back to fade");
            }
        }
        await NGame.Instance!.Transition.FadeOut(FadeSeconds);
    }

    private static async Task UncoverScreen()
    {
        if (DisableRenderLoopDuringReplay && !_coverShown)
            return;
        if (_coverShown)
        {
            _coverShown = false;
            var layer = _coverLayer;
            _coverLayer = null;
            if (layer != null && GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
            return;
        }
        await NGame.Instance!.Transition.FadeIn(FadeSeconds);
    }

    /// <summary>
    /// NonInteractiveMode.IsActive is driven by a replaceable delegate (used by the game's AutoSlayer bot).
    /// Compose ours on top of whatever is installed, re-checking each time in case something replaced it.
    /// </summary>
    private static void EnsureNonInteractiveHook()
    {
        if (!UseNonInteractiveReplay) return;
        var current = NonInteractiveMode.AutoSlayerCheck;
        if (_installedCheck != null && ReferenceEquals(current, _installedCheck)) return;
        _previousCheck = current;
        var prev = _previousCheck;
        _installedCheck = () => ReplayModeActive || (prev?.Invoke() ?? false);
        NonInteractiveMode.AutoSlayerCheck = _installedCheck;
    }

    // ── cosmetic: keep enemies where they were on screen ─────────────────────

    private static List<(uint combatId, Vector2 pos)> CaptureEnemyPositions()
    {
        var positions = new List<(uint combatId, Vector2 pos)>();
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return positions;
            foreach (var node in combatRoom.CreatureNodes)
                if (node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            foreach (var node in combatRoom.RemovingCreatureNodes)
                if (GodotObject.IsInstanceValid(node) && node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            positions.Sort((a, b) => a.combatId.CompareTo(b.combatId));
        }
        catch (Exception ex) { Log.Write($"Enemy position capture error: {ex.Message}"); }
        return positions;
    }

    private static void RestoreEnemyPositions(List<(uint combatId, Vector2 pos)> saved)
    {
        if (saved.Count == 0) return;
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return;
            var nodes = combatRoom.CreatureNodes.Where(n => n.Entity.Side == CombatSide.Enemy)
                .OrderBy(n => n.Entity.CombatId ?? 0).ToList();
            foreach (var node in nodes)
            {
                var id = node.Entity.CombatId ?? 0;
                var match = saved.FirstOrDefault(s => s.combatId == id);
                if (match.combatId == id && saved.Any(s => s.combatId == id))
                    node.Position = match.pos;
            }
        }
        catch (Exception ex) { Log.Write($"Enemy position restore error: {ex.Message}"); }
    }
}
