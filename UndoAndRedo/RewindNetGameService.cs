using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

namespace UndoAndRedo;

/// <summary>
/// A singleplayer net service whose <see cref="Type"/> reports <c>Replay</c> while the rewind engine is
/// feeding recorded events, and <c>Singleplayer</c> otherwise.
///
/// Why: the game's synchronizers switch on <c>NetService.Type</c>. In <c>Replay</c> mode they do not
/// enqueue live requests (the replay feeder does it), player choices are read from the recorded stream
/// instead of the UI, and turn-end bookkeeping actions are not generated (they are in the stream).
/// As soon as replay ends we need normal singleplayer behaviour again. Wrapping the real service and
/// switching one property is far safer than swapping service instances inside every synchronizer.
/// </summary>
internal sealed class RewindNetGameService : INetGameService
{
    private readonly NetSingleplayerGameService _inner = new();

    public ulong NetId => _inner.NetId;
    public bool IsConnected => _inner.IsConnected;
    public bool IsGameLoading => _inner.IsGameLoading;
    public NetGameType Type => RewindEngine.ReplayModeActive ? NetGameType.Replay : NetGameType.Singleplayer;
    public PlatformType Platform => _inner.Platform;

    public event Action<NetErrorInfo>? Disconnected
    {
        add => _inner.Disconnected += value;
        remove => _inner.Disconnected -= value;
    }

    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage => _inner.SendMessage(message, playerId);
    public void SendMessage<T>(T message) where T : INetMessage => _inner.SendMessage(message);
    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage => _inner.RegisterMessageHandler(handler);
    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage => _inner.UnregisterMessageHandler(handler);
    public void Update() => _inner.Update();
    public void Disconnect(NetError reason, bool now = false) => _inner.Disconnect(reason, now);
    public ConnectionStats? GetStatsForPeer(ulong peerId) => new ConnectionStats(peerId);
    public void SetGameLoading(bool isLoading) => _inner.SetGameLoading(isLoading);
    public void SetBufferMessages(bool bufferMessages) => _inner.SetBufferMessages(bufferMessages);
    public string? GetRawLobbyIdentifier() => _inner.GetRawLobbyIdentifier();
}
