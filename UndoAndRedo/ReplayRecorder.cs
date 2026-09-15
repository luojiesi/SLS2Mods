using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRedo;

/// <summary>
/// Observes the game's own <see cref="CombatReplayWriter"/> for the current run and keeps the extra
/// bookkeeping the rewind engine needs but the game does not store:
///
///  • which recorded event index each live <see cref="GameAction"/> corresponds to, and whether that
///    action is still "open" (queued, executing, or waiting for a player choice);
///  • the action id assigned to every GameAction / HookAction / ResumeAction event, so that a
///    ResumeAction can be traced back to the event that originally enqueued the action ("root");
///  • a checksum of the full combat state taken right before each player decision executes, used to
///    verify that a rewind reproduced the expected state.
///
/// Everything here is read-only with respect to game state.
/// </summary>
internal sealed class ReplayRecorder
{
    private static readonly FieldInfo? ReplayField = AccessTools.Field(typeof(CombatReplayWriter), "_replay");

    public static ReplayRecorder? Current { get; private set; }

    private sealed class TrackedAction
    {
        public required GameAction Action;
        public required int EventIndex;
    }

    private readonly struct EventInfo
    {
        public readonly uint Id;
        public readonly int RootEventIndex;
        public EventInfo(uint id, int root) { Id = id; RootEventIndex = root; }
    }

    private readonly RunManager _runManager;
    private readonly ActionQueueSet _queue;
    private readonly ActionExecutor _executor;
    private readonly CombatReplayWriter _writer;
    private readonly ChecksumTracker _checksums;
    private readonly CombatManager _combat;

    private readonly Dictionary<int, EventInfo> _eventInfo = new();
    private readonly Dictionary<uint, int> _idToRoot = new();
    private readonly Dictionary<int, TrackedAction> _tracked = new();
    private readonly Dictionary<int, uint> _checksumBefore = new();
    private readonly Dictionary<int, NetFullCombatState> _stateBefore = new();
    private readonly Dictionary<int, ShadowSnapshot.Snapshot> _shadowBefore = new();
    private readonly Dictionary<int, FastPath.FastPath.Capture> _fastBefore = new();
    private CombatReplay? _lastSeenReplay;

    public static void VerifyReflection()
    {
        Log.Write($"Reflection: CombatReplayWriter._replay = {(ReplayField != null ? "OK" : "NULL")}");
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    public static void Attach()
    {
        Detach();
        try
        {
            var rm = RunManager.Instance;
            if (rm.ActionQueueSet == null || rm.ActionExecutor == null || rm.CombatReplayWriter == null || rm.ChecksumTracker == null)
            {
                Log.Write("Recorder: run manager not fully initialized, not attaching");
                return;
            }
            Current = new ReplayRecorder(rm);
            Log.Debug("Recorder attached");
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder attach failed: {ex}");
        }
    }

    public static void Detach()
    {
        if (Current == null) return;
        try { Current.Unsubscribe(); } catch (Exception ex) { Log.Write($"Recorder detach error: {ex.Message}"); }
        Current = null;
    }

    private ReplayRecorder(RunManager rm)
    {
        _runManager = rm;
        _queue = rm.ActionQueueSet;
        _executor = rm.ActionExecutor;
        _writer = rm.CombatReplayWriter;
        _checksums = rm.ChecksumTracker;
        _combat = CombatManager.Instance;

        _queue.ActionEnqueued += OnActionEnqueued;
        _queue.ActionResumed += OnActionResumed;
        _executor.BeforeActionExecuted += OnBeforeActionExecuted;
        _combat.CombatSetUp += OnCombatSetUp;
    }

    private void Unsubscribe()
    {
        _queue.ActionEnqueued -= OnActionEnqueued;
        _queue.ActionResumed -= OnActionResumed;
        _executor.BeforeActionExecuted -= OnBeforeActionExecuted;
        _combat.CombatSetUp -= OnCombatSetUp;
    }

    // ── accessors ────────────────────────────────────────────────────────────

    /// <summary>The game's live replay object for the current map point (null outside of a recorded room).</summary>
    public CombatReplay? Replay => ReplayField?.GetValue(_writer) as CombatReplay;

    public RunState? RunState => _runManager.DebugOnlyGetState();

    /// <summary>Checksum recorded right before the player decision at <paramref name="eventIndex"/> started executing.</summary>
    public uint? ChecksumBefore(int eventIndex) => _checksumBefore.TryGetValue(eventIndex, out var c) ? c : null;

    /// <summary>Full state captured together with <see cref="ChecksumBefore"/> (for diffing on mismatch).</summary>
    public NetFullCombatState? StateBefore(int eventIndex) => _stateBefore.TryGetValue(eventIndex, out var st) ? st : null;

    /// <summary>Reflective model-graph snapshot taken before the decision at <paramref name="eventIndex"/> (research).</summary>
    public ShadowSnapshot.Snapshot? ShadowBefore(int eventIndex) => _shadowBefore.TryGetValue(eventIndex, out var sh) ? sh : null;

    /// <summary>Fast-path model memento taken before the decision at <paramref name="eventIndex"/>.</summary>
    public FastPath.FastPath.Capture? FastBefore(int eventIndex) => _fastBefore.TryGetValue(eventIndex, out var fs) ? fs : null;

    /// <summary>
    /// After a fast undo the live event stream is cut to <paramref name="keepCount"/> events without a
    /// rebuild; drop the bookkeeping for everything after that so later analysis sees a consistent stream.
    /// </summary>
    public void TruncateTracking(int keepCount)
    {
        foreach (var k in _eventInfo.Keys.Where(k => k >= keepCount).ToList()) _eventInfo.Remove(k);
        foreach (var k in _tracked.Keys.Where(k => k >= keepCount).ToList()) _tracked.Remove(k);
        foreach (var k in _checksumBefore.Keys.Where(k => k >= keepCount).ToList()) _checksumBefore.Remove(k);
        foreach (var k in _stateBefore.Keys.Where(k => k >= keepCount).ToList()) _stateBefore.Remove(k);
        foreach (var k in _shadowBefore.Keys.Where(k => k >= keepCount).ToList()) _shadowBefore.Remove(k);
        foreach (var k in _fastBefore.Keys.Where(k => k >= keepCount).ToList()) _fastBefore.Remove(k);
        foreach (var k in _idToRoot.Where(kv => kv.Value >= keepCount).Select(kv => kv.Key).ToList()) _idToRoot.Remove(k);
        Log.Debug($"Recorder: tracking truncated to {keepCount} events");
    }

    /// <summary>Full live combat state snapshot (same structure the game hashes for desync detection).</summary>
    public NetFullCombatState? CurrentState()
    {
        try { var rs = RunState; return rs == null ? null : NetFullCombatState.FromRun(rs, null); }
        catch (Exception ex) { Log.Write($"State capture failed: {ex.Message}"); return null; }
    }

    /// <summary>Checksum of the live combat state right now (same function multiplayer uses for desync detection).</summary>
    public uint? CurrentChecksum()
    {
        try
        {
            var rs = RunState;
            if (rs == null) return null;
            var full = NetFullCombatState.FromRun(rs, null);
            return _checksums.GenerateChecksum(full);
        }
        catch (Exception ex)
        {
            Log.Write($"Checksum failed: {ex.Message}");
            return null;
        }
    }

    // ── classification ───────────────────────────────────────────────────────

    /// <summary>A "player decision" is an action the player consciously took; these are the undo boundaries.</summary>
    public static bool IsPlayerDecision(GameAction a) =>
        a is PlayCardAction or UsePotionAction or DiscardPotionGameAction
          or EndPlayerTurnAction or UndoEndPlayerTurnAction or ConsoleCmdGameAction;

    public static bool IsPlayerDecision(in CombatReplayEvent e) =>
        e.eventType == CombatReplayEventType.GameAction && e.action is
            NetPlayCardAction or NetUsePotionAction or NetDiscardPotionGameAction
            or NetEndPlayerTurnAction or NetUndoEndPlayerTurnAction or NetConsoleCmdGameAction;

    public static string Describe(in CombatReplayEvent e) => e.eventType switch
    {
        CombatReplayEventType.GameAction => e.action?.ToString() ?? "GameAction(null)",
        CombatReplayEventType.HookAction => $"Hook#{e.hookId}",
        CombatReplayEventType.ResumeAction => $"Resume({e.actionId})",
        CombatReplayEventType.PlayerChoice => $"Choice#{e.choiceId}",
        _ => e.eventType.ToString(),
    };

    // ── event handlers ───────────────────────────────────────────────────────

    private void OnCombatSetUp(CombatState _)
    {
        ResetTracking("combat set up");
    }

    private void ResetTracking(string why)
    {
        _eventInfo.Clear();
        _idToRoot.Clear();
        _tracked.Clear();
        _checksumBefore.Clear();
        _stateBefore.Clear();
        _shadowBefore.Clear();
        _fastBefore.Clear();
        _lastSeenReplay = Replay;
        Log.Debug($"Recorder: tracking reset ({why})");
    }

    private void EnsureSameReplay(CombatReplay replay)
    {
        if (!ReferenceEquals(replay, _lastSeenReplay))
            ResetTracking("new replay object");
    }

    /// <summary>Called after the game's writer appended the event for this action (writer subscribed first).</summary>
    private void OnActionEnqueued(GameAction action)
    {
        try
        {
            if (!_combat.IsInProgress) return;
            var replay = Replay;
            if (replay == null) return;
            EnsureSameReplay(replay);

            int idx = replay.events.Count - 1;
            if (idx < 0 || !action.Id.HasValue) return;
            var ev = replay.events[idx];
            bool matches = action is GenericHookGameAction
                ? ev.eventType == CombatReplayEventType.HookAction
                : ev.eventType == CombatReplayEventType.GameAction;
            if (!matches)
            {
                Log.Write($"Recorder: enqueued {action} but last recorded event is {Describe(ev)} — not tracking");
                return;
            }

            uint id = action.Id.Value;
            _idToRoot[id] = idx;
            _eventInfo[idx] = new EventInfo(id, idx);

            var tracked = new TrackedAction { Action = action, EventIndex = idx };
            _tracked[idx] = tracked;
            if (IsPlayerDecision(action)) RewindEngine.OnLiveDecision();
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder.OnActionEnqueued error: {ex}");
        }
    }

    /// <summary>Raised before the queue assigns the new id; <c>NextActionId</c> is the id about to be used.</summary>
    private void OnActionResumed(uint oldId)
    {
        try
        {
            if (!_combat.IsInProgress) return;
            var replay = Replay;
            if (replay == null) return;
            EnsureSameReplay(replay);

            int idx = replay.events.Count - 1;
            if (idx < 0) return;
            var ev = replay.events[idx];
            if (ev.eventType != CombatReplayEventType.ResumeAction || ev.actionId != oldId)
            {
                Log.Write($"Recorder: resume({oldId}) but last recorded event is {Describe(ev)} — not tracking");
                return;
            }

            uint newId = _queue.NextActionId;
            int root = _idToRoot.TryGetValue(oldId, out var r) ? r : -1;
            _idToRoot[newId] = root;
            _eventInfo[idx] = new EventInfo(newId, root);
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder.OnActionResumed error: {ex}");
        }
    }

    private void OnBeforeActionExecuted(GameAction action)
    {
        try
        {
            if (!_combat.IsInProgress || !IsPlayerDecision(action)) return;
            // BeforeActionExecuted also fires when an action resumes after a player choice; only the
            // first start reflects the state "before" the decision.
            if (action.State != GameActionState.WaitingForExecution) return;
            var tracked = _tracked.Values.FirstOrDefault(t => ReferenceEquals(t.Action, action));
            if (tracked == null) return;
            var state = CurrentState();
            if (state != null)
            {
                _checksumBefore[tracked.EventIndex] = _checksums.GenerateChecksum(state);
                _stateBefore[tracked.EventIndex] = state;
            }
            // The fast-path memento is taken for live and replayed decisions alike (replayed ones become the
            // snapshots for undos after a redo); the research snapshot only for live ones.
            var fast = FastPath.FastPath.CaptureBeforeDecision(tracked.EventIndex, action);
            if (fast != null) _fastBefore[tracked.EventIndex] = fast;
            if (RewindEngine.ReplayModeActive) return;
            if (ShadowSnapshot.Enabled)
            {
                var shadow = ShadowSnapshot.Capture($"before event {tracked.EventIndex} ({action.GetType().Name})");
                if (shadow != null)
                {
                    _shadowBefore[tracked.EventIndex] = shadow;
                    Log.Debug($"shadow: captured {shadow.Values.Count} values / {shadow.ObjectCount} objects in {shadow.CaptureMs:F1} ms");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Recorder.OnBeforeActionExecuted error: {ex}");
        }
    }

    // ── analysis ─────────────────────────────────────────────────────────────

    private static bool IsDone(GameAction a) => a.State is GameActionState.Finished or GameActionState.Canceled;

    /// <summary>Root event index of the action that a ResumeAction event resumes, or -1 if unknown.</summary>
    private int RootOf(int resumeEventIndex, in CombatReplayEvent ev)
    {
        if (_eventInfo.TryGetValue(resumeEventIndex, out var info))
            return info.RootEventIndex;
        if (ev.actionId.HasValue && _idToRoot.TryGetValue(ev.actionId.Value, out var root))
            return root;
        return -1;
    }

    public static List<int> Boundaries(IReadOnlyList<CombatReplayEvent> events)
    {
        var list = new List<int>();
        for (int i = 0; i < events.Count; i++)
            if (IsPlayerDecision(events[i]))
                list.Add(i);
        return list;
    }

    /// <summary>
    /// Chooses the event index to truncate at for an undo: the latest player decision such that the
    /// kept prefix is "closed" — no kept action is still waiting for a choice/resume that lives in the
    /// removed suffix, and no action that is currently open in the live game is kept.
    /// Returns -1 when there is nothing to undo.
    /// </summary>
    public int ComputeUndoTarget(IReadOnlyList<CombatReplayEvent> events, out string reason)
    {
        reason = "";
        var boundaries = Boundaries(events);
        if (boundaries.Count == 0) { reason = "no player decisions recorded"; return -1; }

        int limit = events.Count - 1;
        foreach (var t in _tracked.Values)
        {
            if (!IsDone(t.Action) && t.EventIndex < limit)
                limit = t.EventIndex;
        }

        for (int guard = 0; guard < 64; guard++)
        {
            int target = -1;
            foreach (int b in boundaries)
                if (b <= limit) target = b;
            if (target < 0) { reason = "no closed prefix available"; return -1; }

            int minViolatingRoot = int.MaxValue;
            for (int k = target; k < events.Count; k++)
            {
                var ev = events[k];
                if (ev.eventType != CombatReplayEventType.ResumeAction) continue;
                int root = RootOf(k, ev);
                if (root < 0)
                {
                    // Unknown root: be conservative and treat as depending on something before target.
                    minViolatingRoot = Math.Min(minViolatingRoot, target - 1);
                }
                else if (root < target)
                {
                    minViolatingRoot = Math.Min(minViolatingRoot, root);
                }
            }
            if (minViolatingRoot == int.MaxValue)
                return target;

            Log.Debug($"Undo target {target} is not closed (resume depends on event {minViolatingRoot}); moving earlier");
            limit = minViolatingRoot;
        }
        reason = "could not find a closed prefix";
        return -1;
    }

    /// <summary>
    /// Splits the removed suffix (from <paramref name="from"/> to the end) into self-contained redo
    /// segments, each starting at a player decision. Segments whose resumptions spill into the next
    /// segment are merged. A trailing segment that contains an unfinished action is dropped because it
    /// cannot be replayed faithfully.
    /// </summary>
    public List<List<CombatReplayEvent>> SplitRedoSegments(IReadOnlyList<CombatReplayEvent> events, int from)
    {
        var result = new List<List<CombatReplayEvent>>();
        var boundaries = Boundaries(events).Where(b => b >= from).ToList();
        if (boundaries.Count == 0 || boundaries[0] != from)
            return result;

        int segStart = from;
        int bi = 1;
        while (segStart < events.Count)
        {
            int segEnd = bi < boundaries.Count ? boundaries[bi] : events.Count;
            // Extend while a resume after segEnd refers to a root inside [segStart, segEnd).
            bool extended = true;
            while (extended && segEnd < events.Count)
            {
                extended = false;
                for (int k = segEnd; k < events.Count; k++)
                {
                    var ev = events[k];
                    if (ev.eventType != CombatReplayEventType.ResumeAction) continue;
                    int root = RootOf(k, ev);
                    if (root >= segStart && root < segEnd)
                    {
                        // merge next segment
                        bi++;
                        segEnd = bi < boundaries.Count ? boundaries[bi] : events.Count;
                        extended = true;
                        break;
                    }
                }
            }

            bool hasOpen = false;
            for (int k = segStart; k < segEnd; k++)
                if (_tracked.TryGetValue(k, out var t) && !IsDone(t.Action))
                    hasOpen = true;

            if (hasOpen)
            {
                Log.Debug($"Redo segment [{segStart},{segEnd}) contains an unfinished action; dropping it and everything after");
                break;
            }

            result.Add(events.Skip(segStart).Take(segEnd - segStart).ToList());
            segStart = segEnd;
            bi++;
        }
        return result;
    }

    public string DumpEvents(IReadOnlyList<CombatReplayEvent> events)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < events.Count; i++)
        {
            string open = _tracked.TryGetValue(i, out var t) && !IsDone(t.Action) ? " [OPEN]" : "";
            string mark = IsPlayerDecision(events[i]) ? "*" : " ";
            sb.Append($"  {mark}{i,3}: {Describe(events[i])}{open}\n");
        }
        return sb.ToString();
    }
}
