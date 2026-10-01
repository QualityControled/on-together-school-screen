using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using OnTogetherSchoolScreen;
using PurrNet;
using UnityEngine;

try
{
    PacketRoundTrips();
    BoardReadiness();
    HostlessMixedLobby();
    IdleQueueLateJoin();
    QuickReconnect();
    PrefixInputChecks();
    DepartureVoteThreshold();
    HostAccessAndReset();
    if (args.Length == 2) CompiledTransportChecks.Run(args[0], args[1]);
    else if (args.Length != 0) throw new ArgumentException("Usage: MixedLobbyCheck [plugin.dll Assembly-CSharp.dll]");
    Console.WriteLine("PASS: selected unchanged product methods passed mixed-lobby simulation checks.");
    Console.WriteLine("LIMITATION: Unity, PurrNet relay/serialization, actual lobby connections, native drawing, audio, and browser rendering are not executed by this harness.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex.GetBaseException().Message);
    return 1;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static int QueueSize(Peer peer) => ((IList)peer.Plugin.Read("_videoQueue")).Count;
static string Video(Peer peer) => (string)peer.Plugin.Read("_videoId");
static int Count(Peer peer) => (int)peer.Plugin.Invoke("GetActivePeerCount");
static int Required(Peer peer) => (int)peer.Plugin.Invoke("GetRequiredVotes");

static void BoardReadiness()
{
    Time.unscaledTime = 0;
    var hub = new Hub(3);
    Peer peer = hub.Add(1, 100);
    peer.Board.isSpawned = false;
    peer.Call("SendBoardPayload", new Vector2(2f, 11f), Vector2.zero, 0);
    Assert(peer.Board.RpcAttempts == 0, "An unspawned board still reached the native RPC wrapper.");
    peer.Plugin.Write("_url", "AAAAAAAAAAA");
    peer.Call("AddVideoToQueue"); peer.Call("BroadcastPeerPresence"); peer.Call("RequestQueueStart");
    peer.Call("SendBoardCommand", "OPEN", "AAAAAAAAAAA", 0f, 1);
    Assert(peer.Board.RpcAttempts == 0 && QueueSize(peer) == 0 && Video(peer) == ""
        && (string)peer.Plugin.Read("_url") == "AAAAAAAAAAA", "Unready actions sent traffic, changed playback, or discarded the submitted link.");

    foreach (Action<QuadPainterGPU> makeUnready in new Action<QuadPainterGPU>[] {
        board => board.id = null, board => board.networkManager = null,
        board => board.PaintColors = null, board => board.PaintColors = [],
        board => board.gameObject.activeInHierarchy = false, board => board.gameObject.scene = new(false) })
    {
        var board = new QuadPainterGPU(); makeUnready(board); peer.Plugin.Write("_schoolBoard", board);
        peer.Call("SendBoardPayload", new Vector2(2f, 11f), Vector2.zero, 0);
        Assert(board.RpcAttempts == 0, "An incomplete/inactive board reached the RPC wrapper.");
    }

    var placeholder = new QuadPainterGPU { isSpawned = false };
    var live = new QuadPainterGPU();
    Resources.Objects = [placeholder, live];
    peer.Plugin.Write("_schoolBoard", null);
    peer.Call("FindSchoolBoard");
    Assert(ReferenceEquals(peer.Plugin.Read("_schoolBoard"), live) && peer.Plugin.BrowserStartCount == 1,
        "Lookup selected the unspawned scene copy instead of the spawned board.");
    live.isSpawned = false;
    Time.unscaledTime = 0.1f;
    peer.Call("FindSchoolBoard");
    Assert(peer.Plugin.Read("_schoolBoard") == null && peer.Plugin.Read("_schoolRenderer") == null,
        "Cached renderer kept a despawned board attached.");
    var replacement = new QuadPainterGPU();
    Resources.Objects = [placeholder, live, replacement];
    Time.unscaledTime = 0.6f;
    peer.Call("FindSchoolBoard");
    peer.Call("AddVideoToQueue"); hub.Drain();
    Assert(ReferenceEquals(peer.Plugin.Read("_schoolBoard"), replacement) && QueueSize(peer) == 1
        && replacement.RpcAttempts > 0, "The replacement board did not resume queueing after readiness.");
    int before = replacement.RpcAttempts;
    replacement.isSpawned = false;
    Time.unscaledTime = 2;
    peer.Call("UpdateQueuePlayback"); peer.Call("AdvanceQueue");
    Assert(QueueSize(peer) == 1 && replacement.RpcAttempts == before && Video(peer) == "",
        "Despawn discarded a pending video or started unsynchronized playback.");
    Resources.Objects = [];
    Console.WriteLine("PASS: unspawned/incomplete boards send no RPC or local playback; lookup ignores placeholders, releases stale caches, and recovers on a spawned replacement.");
}

static void PacketRoundTrips()
{
    // The actual marker methods are used here, including boundary peer tokens.
    for (int token = 1; token <= 0xFFFFF; token++)
    {
        float marker = (float)SchoolScreenPlugin.InvokeStatic("EncodeBoardMarker", token);
        object[] args = [marker, 0];
        Assert((bool)SchoolScreenPlugin.InvokeStatic("TryDecodeBoardMarker", args) && (int)args[1] == token,
            "Peer-token marker failed to round trip at " + token);
    }
    object[] drawingArgs = [0.5f, 0];
    Assert(!(bool)SchoolScreenPlugin.InvokeStatic("TryDecodeBoardMarker", drawingArgs), "Ordinary drawing UV was treated as a mod packet.");
    foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, 1f, -1f })
    {
        object[] invalidArgs = [invalid, 0];
        Assert(!(bool)SchoolScreenPlugin.InvokeStatic("TryDecodeBoardMarker", invalidArgs), "Invalid marker was accepted: " + invalid);
    }
    var random = new Random(111);
    const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
    var ids = new List<string> { "AAAAAAAAAAA", "___________", "-----------", "pqsgpG_OeM8", "QDia3e12czc" };
    for (int i = 0; i < 1000; i++) ids.Add(new string(Enumerable.Range(0, 11).Select(_ => alphabet[random.Next(64)]).ToArray()));
    foreach (string id in ids)
    {
        int first = (int)SchoolScreenPlugin.InvokeStatic("PackVideoIdGroup", id, 0, 4);
        int second = (int)SchoolScreenPlugin.InvokeStatic("PackVideoIdGroup", id, 4, 4);
        int third = (int)SchoolScreenPlugin.InvokeStatic("PackVideoIdGroup", id, 8, 3);
        float safe = BoardPacketEnvelope.EncodeFirstGroup(first);
        string decoded = (string)SchoolScreenPlugin.InvokeStatic("UnpackVideoId", (int)BoardPacketEnvelope.DecodeFirstGroup(safe), second, third);
        Assert(decoded == id, "Video ID failed to round trip: " + id);
    }
    Console.WriteLine("PASS: all 1,048,575 peer tokens and 1,005 video IDs round-trip through actual product methods.");
}

static void HostlessMixedLobby()
{
    Time.unscaledTime = 0;
    var hub = new Hub(10); // Vanilla server + other vanilla members are represented in the lobby roster.
    Peer a = hub.Add(1, 200), b = hub.Add(2, 100);
    a.Call("BroadcastPeerPresence"); b.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Count(a) == 2 && Count(b) == 2 && Required(a) == 1, "Vanilla members entered the modded-only vote denominator.");
    Assert(!(bool)a.Plugin.Read("_host") && !(bool)b.Plugin.Read("_host"), "Hostless scenario unexpectedly had a modded host.");
    a.Plugin.Write("_url", "https://www.youtube.com/watch?v=pqsgpG_OeM8"); a.Call("AddVideoToQueue"); hub.Drain();
    b.Plugin.Write("_url", "QDia3e12czc"); b.Call("AddVideoToQueue"); hub.Drain();
    Assert(QueueSize(a) == 2 && QueueSize(b) == 2, "Modded viewers could not queue without a modded host.");
    Time.unscaledTime = 1;
    a.Call("UpdateQueuePlayback"); hub.Drain(); b.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(Video(a) == "pqsgpG_OeM8" && Video(b) == Video(a), "Idle hostless queue did not elect one coordinator and start its first video.");
    Assert(QueueSize(a) == 1 && QueueSize(b) == 1, "Hostless playback skipped or duplicated queued entries.");
    a.Call("ToggleSkipVote"); hub.Drain();
    Assert(Video(a) == "QDia3e12czc" && Video(b) == Video(a), "One yes vote among two modded members did not meet the 30% threshold.");
    b.Plugin.Write("_url", "AAAAAAAAAAA"); b.Call("AddVideoToQueue"); hub.Drain();
    Peer c = hub.Add(3, 300);
    c.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Video(c) == Video(b) && QueueSize(c) == QueueSize(b), "Late viewer did not receive current playback and pending queue without a modded server.");
    Peer d = hub.Add(4, 400);
    d.Call("BroadcastPeerPresence"); hub.Drain();
    // Existing peers emit their next heartbeat so each newly joined modded peer sees the whole mod roster.
    a.Call("BroadcastPeerPresence"); b.Call("BroadcastPeerPresence"); c.Call("BroadcastPeerPresence"); hub.Drain();
    foreach (Peer peer in hub.Peers) Assert(Count(peer) == 4 && Required(peer) == 2, "Four modded peers should require two yes votes despite ten lobby players.");
    a.Call("ToggleSkipVote"); hub.Drain();
    Assert(Video(b) == "QDia3e12czc", "A single vote incorrectly skipped with four modded members.");
    Peer e = hub.Add(5, 500);
    e.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Count(e) == 5 && Required(e) == 2, "Late joiner did not receive the existing modded roster.");
    Assert(((HashSet<int>)e.Plugin.Read("_peerSkipVotes")).SetEquals(new[] { 200 }), "Late joiner lost an existing partial yes vote.");
    Assert((int)e.Plugin.Read("_voteCount") == 1 && !(bool)e.Plugin.Read("_localVotedSkip"), "Late joiner vote display or personal vote state is incorrect.");
    // Queue an old snapshot at a viewer, then remove the vote before draining it.
    // The coordinator's newer voter snapshot must restore the final personal state.
    b.Call("BroadcastVoteStatus", true, false);
    a.Call("ToggleSkipVote"); hub.Drain();
    Assert(!(bool)a.Plugin.Read("_localVotedSkip") && hub.Peers.All(peer => ((HashSet<int>)peer.Plugin.Read("_peerSkipVotes")).Count == 0),
        "An older voter snapshot overruled a later local vote removal.");
    a.Call("ToggleSkipVote"); hub.Drain();
    c.Call("ToggleSkipVote"); hub.Drain();
    Assert(Video(b) == "AAAAAAAAAAA" && hub.Peers.All(peer => Video(peer) == Video(b)), "Two yes votes failed to skip for four modded members.");
    int oldController = (int)a.Plugin.Read("_playbackControllerToken");
    Assert(oldController == 100, "Unexpected initial hostless coordinator.");
    hub.Remove(b);
    Time.unscaledTime = 23;
    a.Call("BroadcastPeerPresence"); c.Call("BroadcastPeerPresence"); d.Call("BroadcastPeerPresence"); hub.Drain();
    e.Call("BroadcastPeerPresence"); hub.Drain();
    a.Call("PruneStalePeers"); // Followers have not run their own periodic prune yet.
    foreach (Peer peer in hub.Peers) Assert(Count(peer) == 4, "Expired modded peer still entered the vote denominator.");
    Assert((int)a.Plugin.Read("_playbackControllerToken") == 200, "Coordinator did not transfer to the remaining lowest token.");
    a.Plugin.Write("_url", "-----------"); a.Call("AddVideoToQueue"); hub.Drain();
    PlaybackSession session = (PlaybackSession)a.Plugin.Read("_playbackSession");
    session.Observe(session.VideoId, session.LoadId, 1); session.Observe(session.VideoId, session.LoadId, 0);
    a.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(hub.Peers.All(peer => Video(peer) == "-----------"), "Queued video did not automatically start after the new coordinator observed playback ending.");
    Assert(hub.VanillaPacketCount > 0, "The simulated vanilla recipient saw no traffic.");
    Console.WriteLine($"PASS: unmodded server plus modded viewers queue/play, synchronize a late joiner, count only modded voters, elect on leave, and auto-advance; {hub.VanillaPacketCount} outgoing packets checked for vanilla-safe flags.");
}

static void HostAccessAndReset()
{
    Time.unscaledTime = 0;
    var hub = new Hub(8);
    Peer host = hub.Add(0, 900, true), guest = hub.Add(1, 100);
    host.Call("EnsureHostMember"); host.Call("BroadcastPeerPresence"); guest.Call("BroadcastPeerPresence"); hub.Drain();
    Assert((string)((Dictionary<int, string>)host.Plugin.Read("_peerPlayerKeys"))[100] == "player:001", "Access binding did not use the actual network sender.");
    ((HashSet<string>)host.Plugin.Read("_blockedQueuePlayers")).Add("player:001");
    guest.Plugin.Write("_url", "AAAAAAAAAAA"); guest.Call("AddVideoToQueue"); hub.Drain();
    Assert(QueueSize(host) == 0 && QueueSize(guest) == 0, "Blocked sender's optimistic queue entry was not rejected by the host snapshot.");
    bool rebound = (bool)host.Call("BindPeerIdentity", 100, new PlayerID(2));
    Assert(!rebound, "Another network sender hijacked an existing peer token.");
    ((HashSet<string>)host.Plugin.Read("_blockedQueuePlayers")).Clear();
    guest.Plugin.Write("_url", "AAAAAAAAAAA"); guest.Call("AddVideoToQueue"); hub.Drain();
    host.Plugin.Write("_url", "___________"); host.Call("AddVideoToQueue"); hub.Drain();
    Time.unscaledTime = 1; host.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(Video(host) == "AAAAAAAAAAA", "Modded host queue did not start.");
    Assert(!(bool)guest.Call("IsLocalPlaybackCoordinator"), "A lower viewer token overruled the actual modded lobby host.");
    Peer lateHostViewer = hub.Add(2, 50);
    lateHostViewer.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Video(lateHostViewer) == "AAAAAAAAAAA" && QueueSize(lateHostViewer) == 1,
        "Late viewer did not accept the modded host's immediate playback/queue snapshot before a separate host heartbeat.");
    host.Call("BroadcastQueueSnapshot"); host.Call("BroadcastQueueSnapshot"); hub.Drain();
    Assert(QueueSize(guest) == 1 && QueueSize(lateHostViewer) == 1, "Repeated queue snapshots duplicated an existing index.");
    guest.Call("HandleNetworkCommand", "QUEUE_ITEM|5|___________|900");
    Assert(QueueSize(guest) == 1, "A gapped queue item appended without its earlier indices.");
    guest.Call("SendPeerPacket", 7, "QUEUE_CLEAR|100"); hub.Drain();
    Assert(QueueSize(host) == 1 && QueueSize(guest) == 1, "Viewer forged an authoritative queue-clear packet.");
    guest.Call("SendBoardCommand", "OPEN", "-----------", 0f, 1); hub.Drain();
    Assert(Video(host) == "AAAAAAAAAAA" && Video(guest) == Video(host), "Viewer replaced host-controlled playback with an arbitrary OPEN packet.");
    host.Call("ToggleSkipVote"); hub.Drain();
    Assert(Video(host) == "___________" && Video(guest) == Video(host), "Host could not skip without votes.");
    host.Plugin.Write("_url", "-----------"); host.Call("AddVideoToQueue"); hub.Drain();
    host.Call("UpdateHostState");
    // The game may keep the same textual lobby code while disconnecting. Status must still reset it.
    host.Lobby.LobbyStatus = false;
    host.Call("UpdateHostState");
    Assert(Video(host) == "" && QueueSize(host) == 0, "Lobby reset retained previous playback/queue.");
    Assert(((HashSet<int>)host.Plugin.Read("_peerSkipVotes")).Count == 0 && Count(host) == 1, "Lobby reset retained votes or remote participants.");
    Assert(((Dictionary<int, string>)host.Plugin.Read("_peerPlayerKeys")).Count == 0 && ((HashSet<string>)host.Plugin.Read("_blockedQueuePlayers")).Count == 0,
        "Lobby reset retained old identity bindings/access restrictions.");
    Assert(((ConcurrentQueue<string>)host.Plugin.Read("_networkQueue")).IsEmpty, "Lobby reset retained pending old-lobby packets.");
    Assert(host.Plugin.HelperCommands.Contains("CLEAR"), "Lobby reset did not stop the local browser.");
    Assert(!(bool)host.Plugin.Read("_host"), "Disconnected player retained host permission.");
    Console.WriteLine("PASS: actual RPC sender identity enforces host queue blocks, rejects token rebinding, host skips without votes, and lobby reset clears shared state.");
}

static void IdleQueueLateJoin()
{
    Time.unscaledTime = 0;
    var hub = new Hub(12);
    Peer a = hub.Add(1, 200), b = hub.Add(2, 100);
    a.Call("BroadcastPeerPresence"); b.Call("BroadcastPeerPresence"); hub.Drain();
    a.Plugin.Write("_url", "AAAAAAAAAAA"); a.Call("AddVideoToQueue"); hub.Drain();
    b.Plugin.Write("_url", "___________"); b.Call("AddVideoToQueue"); hub.Drain();
    Peer lower = hub.Add(3, 50);
    lower.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Count(lower) == 3 && Required(lower) == 1, "Lower-token idle joiner did not receive the existing modded roster before becoming coordinator.");
    Assert(QueueSize(lower) == 2, "Lower-token idle joiner lost the waiting queue.");
    Time.unscaledTime = 1;
    a.Call("UpdateQueuePlayback"); b.Call("UpdateQueuePlayback"); lower.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(hub.Peers.All(peer => Video(peer) == "AAAAAAAAAAA" && QueueSize(peer) == 1), "New idle coordinator failed to start the same FIFO entry for all peers.");
    Console.WriteLine("PASS: a lower-token viewer joining before idle playback inherits both the modded roster and queue before coordination.");
}

static void PrefixInputChecks()
{
    Time.unscaledTime = 0;
    var hub = new Hub(6);
    Peer receiver = hub.Add(1, 100);
    receiver.Activate();
    float marker = (float)SchoolScreenPlugin.InvokeStatic("EncodeBoardMarker", 200);
    var info = new RPCInfo { sender = new PlayerID(2) };
    bool ordinary = (bool)SchoolScreenPlugin.InvokeStatic("ReceiveBoardCommand", receiver.Board, new Vector2(0.5f, 0.5f), Vector2.zero, 1, info);
    Assert(ordinary, "Normal school-board drawing was suppressed.");
    bool otherBoard = (bool)SchoolScreenPlugin.InvokeStatic("ReceiveBoardCommand", new QuadPainterGPU(), new Vector2(marker, 11), new Vector2(-1, 0), 0, info);
    Assert(otherBoard, "School-screen prefix suppressed a packet on another drawing board.");
    Packet[] malformed =
    [
        new(new Vector2(marker, float.NaN), new Vector2(-1, 0), 0, true, false),
        new(new Vector2(marker, float.PositiveInfinity), new Vector2(-1, 0), 0, true, false),
        new(new Vector2(marker, 11.5f), new Vector2(-1, 0), 0, true, false),
        new(new Vector2(marker, 11), new Vector2(float.NaN, 0), 0, true, false),
        new(new Vector2(marker, 11), new Vector2(-1, float.PositiveInfinity), 0, true, false),
        new(new Vector2(marker, 11), new Vector2(-1, 0), -1, true, false),
        new(new Vector2(marker, 11), new Vector2(-1, 0), 16777216, true, false),
        new(new Vector2(marker, 15), new Vector2(-1, 0), 0, true, false),
        new(new Vector2(marker, 25), new Vector2(-2, 0), 0, true, false),
        new(new Vector2(marker, 9), new Vector2(-5, 3), 1, true, false),
        new(new Vector2(marker, 1), new Vector2(-1, 0), 0, true, false),
    ];
    foreach (Packet packet in malformed)
    {
        bool result = (bool)SchoolScreenPlugin.InvokeStatic("ReceiveBoardCommand", receiver.Board, packet.Uv, packet.Previous, packet.Color, info);
        Assert(!result, "Malformed mod marker escaped into vanilla drawing.");
    }
    Assert(((ConcurrentQueue<string>)receiver.Plugin.Read("_networkQueue")).IsEmpty
        && ((Dictionary<int, string>)receiver.Plugin.Read("_peerPlayerKeys")).Count == 0,
        "Malformed packets queued commands or discovered false modded members.");
    Console.WriteLine("PASS: ordinary drawing and other boards pass through; malformed mod packets are dropped before identity or command state changes.");
}

static void QuickReconnect()
{
    Time.unscaledTime = 0;
    var hub = new Hub(10);
    Peer remaining = hub.Add(1, 200), incumbent = hub.Add(2, 100);
    remaining.Call("BroadcastPeerPresence"); incumbent.Call("BroadcastPeerPresence"); hub.Drain();
    remaining.Plugin.Write("_url", "AAAAAAAAAAA"); remaining.Call("AddVideoToQueue"); hub.Drain();
    incumbent.Plugin.Write("_url", "___________"); incumbent.Call("AddVideoToQueue"); hub.Drain();
    Time.unscaledTime = 1; incumbent.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(Video(remaining) == "AAAAAAAAAAA" && (int)remaining.Plugin.Read("_playbackControllerToken") == 100,
        "Quick-reconnect setup did not start incumbent-controlled playback.");
    hub.Remove(incumbent);
    Time.unscaledTime = 2;
    // Same actual game sender returns before the old token's 20-second expiry,
    // with a new lower token and empty local playback/queue state.
    Peer returning = hub.Add(2, 50);
    returning.Call("BroadcastPeerPresence"); hub.Drain();
    Assert(Video(returning) == "AAAAAAAAAAA" && QueueSize(returning) == 1,
        "Quickly reconnected sender did not recover current playback and queue.");
    Assert((int)remaining.Plugin.Read("_playbackControllerToken") == 200
        && (int)returning.Plugin.Read("_playbackControllerToken") == 200,
        "An empty reconnecting browser inherited coordination before restoration.");
    Assert(Count(remaining) == 2 && Count(returning) == 2, "Old reconnect token was counted as a third modded player.");
    PlaybackSession session = (PlaybackSession)remaining.Plugin.Read("_playbackSession");
    session.Observe(session.VideoId, session.LoadId, 1); session.Observe(session.VideoId, session.LoadId, 0);
    remaining.Call("UpdateQueuePlayback"); hub.Drain();
    Assert(hub.Peers.All(peer => Video(peer) == "___________" && QueueSize(peer) == 0),
        "Restored reconnect lobby did not advance its remaining queued video.");
    Console.WriteLine("PASS: same-sender quick reconnect with a new lower token restores playback/queue, counts once, and advances through a remaining coordinator.");
}

static void DepartureVoteThreshold()
{
    Time.unscaledTime = 0;
    var hub = new Hub(12);
    var members = Enumerable.Range(1, 7).Select(id => hub.Add(id, id * 100)).ToList();
    foreach (Peer peer in members) peer.Call("BroadcastPeerPresence");
    hub.Drain();
    Peer coordinator = members[0];
    coordinator.Plugin.Write("_url", "AAAAAAAAAAA"); coordinator.Call("AddVideoToQueue"); hub.Drain();
    coordinator.Plugin.Write("_url", "___________"); coordinator.Call("AddVideoToQueue"); hub.Drain();
    Time.unscaledTime = 1; coordinator.Call("UpdateQueuePlayback"); hub.Drain();
    members[1].Call("ToggleSkipVote"); hub.Drain(); members[2].Call("ToggleSkipVote"); hub.Drain();
    Assert(Required(coordinator) == 3 && Video(coordinator) == "AAAAAAAAAAA", "Seven modded peers should require three yes votes.");
    hub.Remove(members[6]);
    Time.unscaledTime = 22;
    foreach (Peer peer in hub.Peers) peer.Call("BroadcastPeerPresence");
    hub.Drain();
    foreach (Peer peer in hub.Peers) { peer.Call("PruneStalePeers"); hub.Drain(); }
    Assert(hub.Peers.All(peer => Video(peer) == "___________"), "Existing two yes votes did not trigger skip when departure lowered required votes from three to two.");
    Console.WriteLine("PASS: departed members expire and an already-met lower vote threshold advances playback.");
}

sealed class Peer
{
    public int Id;
    public SchoolScreenPlugin Plugin = new();
    public QuadPainterGPU Board = new();
    public TextChannelManager Channel;
    public MultiplayerManager Lobby = new() { LobbyCode = "simulated-lobby" };
    public void Activate() => Plugin.Activate(Channel, Lobby);
    public object Call(string name, params object[] args) { Activate(); return Plugin.Invoke(name, args); }
}

sealed class Hub
{
    public readonly List<Peer> Peers = new();
    public int VanillaPacketCount;
    Exception deliveryFailure;
    public Hub(int lobbyPlayerCount)
    {
        var panel = new PlayerPanelController();
        for (int i = 0; i < lobbyPlayerCount; i++)
        {
            panel.PlayerIDs.Add(new PlayerID(i));
            panel.IDInfos.Add(new PlayerIDInfo { Name = Encoding.Unicode.GetBytes("Lobby player " + i) });
            panel.PlayerSteamIDs.Add("steam:" + i);
        }
        NetworkSingleton<PlayerPanelController>.I = panel;
    }
    public Peer Add(int id, int token, bool host = false)
    {
        Peer peer = new()
        {
            Id = id,
            Channel = new TextChannelManager { isServer = host, UserName = "Lobby player " + id, localPlayer = new PlayerID(id), MainPlayerController = new PlayerController { localPlayer = new PlayerID(id) } }
        };
        peer.Plugin.Write("_localPeerToken", token); peer.Plugin.Write("_host", host); peer.Plugin.Write("_schoolBoard", peer.Board);
        peer.Board.Deliver = packet =>
        {
            try { Deliver(peer, packet); }
            catch (Exception ex) { deliveryFailure = ex; throw; }
        };
        Peers.Add(peer);
        return peer;
    }
    public void Remove(Peer peer) => Peers.Remove(peer);
    void Deliver(Peer sender, Packet packet)
    {
        // These are the relevant observed predicates from vanilla FillTheBlanks/PaintOnTexture.
        // No interpolation when previous.x <= 0, no palette lookup when erase=true.
        if (!packet.Erase || packet.BigErase || !(packet.Previous.x < 0f) || !(packet.Uv.x >= 1f) || !(packet.Uv.y >= 1f))
            throw new InvalidOperationException($"Unsafe vanilla recipient packet: type={packet.Uv.y}, prevX={packet.Previous.x}, color={packet.Color}, erase={packet.Erase}, big={packet.BigErase}");
        VanillaPacketCount++;
        foreach (Peer receiver in Peers.ToArray())
        {
            receiver.Activate();
            bool runsVanilla = (bool)SchoolScreenPlugin.InvokeStatic("ReceiveBoardCommand", receiver.Board, packet.Uv, packet.Previous, packet.Color, new RPCInfo { sender = new PlayerID(sender.Id) });
            if (runsVanilla) throw new InvalidOperationException("Mod packet was not intercepted on a modded recipient.");
        }
        sender.Activate();
    }
    public void Drain()
    {
        if (deliveryFailure != null) throw new InvalidOperationException("Recorded packet delivery failure: " + deliveryFailure.GetBaseException().Message);
        for (int round = 0; round < 100; round++)
        {
            bool processed = false;
            foreach (Peer peer in Peers.ToArray())
            {
                var queue = (ConcurrentQueue<string>)peer.Plugin.Read("_networkQueue");
                while (queue.TryDequeue(out string message))
                {
                    processed = true;
                    peer.Call("HandleNetworkCommand", message);
                    if (deliveryFailure != null) throw new InvalidOperationException("Recorded packet delivery failure: " + deliveryFailure.GetBaseException().Message);
                }
            }
            if (!processed) return;
        }
        throw new InvalidOperationException("Simulated relay did not quiesce.");
    }
}
