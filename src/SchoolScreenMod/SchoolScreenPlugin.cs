using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PurrNet;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OnTogetherSchoolScreen
{
    [BepInPlugin("codex.ontogether.school-screen", "On-Together School Screen", "0.1.12")]
    public sealed partial class SchoolScreenPlugin : BaseUnityPlugin
    {
        private const float BoardCommandMarker = 2f;
        private const float LegacyBoardCommandMarker = -2f;
        private const float PeerTokenScale = 1048576f;
        private const int PeerTokenMask = 0xFFFFF;
        private const int QueueEventSequenceMask = 0xFFFFF;
        private const float NearBoardDistance = 3f;
        private const float SilentDistance = 18f;
        private const string HostMemberKey = "__local_host__";
        private const int DefaultQueueLimit = 3;
        private const int DefaultQueueCooldown = 10;
        private const float QueueAuthorityWait = 1.5f;
        private const float QueueElectionConflictWindow = 8f;
        private static SchoolScreenPlugin _instance;
        private readonly ConcurrentQueue<string> _networkQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _browserQueue = new ConcurrentQueue<string>();
        private readonly Dictionary<string, LobbyMember> _moddedPlayers = new Dictionary<string, LobbyMember>();
        private readonly HashSet<string> _blockedQueuePlayers = new HashSet<string>();
        private readonly HashSet<int> _peerSkipVotes = new HashSet<int>();
        private readonly Dictionary<int, float> _peerLastSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, string> _peerPlayerKeys = new Dictionary<int, string>();
        private readonly HashSet<long> _processedQueueEvents = new HashSet<long>();
        private readonly Dictionary<long, int> _queueAdmissionResults = new Dictionary<long, int>();
        private readonly Queue<long> _queueAdmissionHistory = new Queue<long>();
        private readonly Dictionary<string, float> _queueLastAcceptedAt = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _queueOwnerKeys = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _queueOwnerNames = new Dictionary<int, string>();
        private readonly Dictionary<long, QueueBatchRequest> _incomingQueueBatches = new Dictionary<long, QueueBatchRequest>();
        private readonly HashSet<long> _processedPlaybackEvents = new HashSet<long>();
        private readonly PlaybackSession _playbackSession = new PlaybackSession();
        private readonly HashSet<string> _queueTitleLookups = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _queueTitleCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<QueueEntry> _videoQueue = new List<QueueEntry>();
        private readonly object _pipeWriteLock = new object();
        private Harmony _harmony;
        private NamedPipeServerStream _pipe;
        private Process _browser;
        private Texture2D _videoTexture;
        private byte[] _pendingJpeg;
        private QuadPainterGPU _schoolBoard;
        private float _nextBoardSearch;
        private Renderer _schoolRenderer;
        private Material _schoolMaterial;
        private bool _panelOpen;
        private bool _guiInitialized;
        private volatile bool _pipeConnected;
        private bool _host;
        private bool _lobbyIdentityInitialized;
        private bool _lobbyStateObserved;
        private bool _observedLobbyActive;
        private string _observedLobbyCode;
        private string _status = "Looking for the school whiteboard…";
        private string _url = "";
        private string _videoId = "";
        private Rect _windowRect;
        private bool _windowPositioned;
        private GUIStyle _windowStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _subtitleStyle;
        private GUIStyle _sectionStyle;
        private GUIStyle _statusStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _placeholderStyle;
        private GUIStyle _primaryButtonStyle;
        private GUIStyle _controlButtonStyle;
        private GUIStyle _seekButtonStyle;
        private GUIStyle _hostBadgeStyle;
        private GUIStyle _viewerBadgeStyle;
        private GUIStyle _closeButtonStyle;
        private GUIStyle _volumeSliderStyle;
        private GUIStyle _volumeThumbStyle;
        private Texture2D _windowTexture;
        private Texture2D _fieldTexture;
        private Texture2D _buttonTexture;
        private Texture2D _buttonHoverTexture;
        private Texture2D _accentTexture;
        private Texture2D _accentHoverTexture;
        private Texture2D _badgeTexture;
        private Texture2D _statusTexture;
        private Texture2D _meterBackgroundTexture;
        private Texture2D _libraryRailTexture;
        private Texture2D _libraryCardTexture;
        private GUIStyle _libraryNavStyle;
        private GUIStyle _librarySelectedNavStyle;
        private GUIStyle _libraryCardStyle;
        private GUIStyle _libraryMetaStyle;
        private GUIStyle _libraryCardTitleStyle;
        private GUIStyle _libraryRightStyle;
        private GUIStyle _libraryHeadingStyle;
        private string _libraryNotice = "";
        private float _videoTime;
        private int _playerState = -1;
        private float _nextSync;
        private float _lastFrameTime;
        private float _nextVolumeUpdate;
        private float _maximumVolume = 100f;
        private int _effectiveVolume;
        private int _lastSentVolume = -1;
        private bool _muted;
        private int _activeTab;
        private bool _localVotedSkip;
        private float _nextLobbyHeartbeat;
        private int _reportedModCount;
        private int _voteCount;
        private int _votesRequired;
        private int _localPeerToken;
        private int _queueEventSequence;
        private int _playbackControllerToken;
        private int _moddedHostPeerToken;
        private int _queueCoordinatorToken;
        private int _announcedQueueCoordinatorToken;
        private float _queueAuthorityReadyAt;
        private float _queueAuthoritySince;
        private float _queueAuthorityObservedAt;
        private int _queueSnapshotRevision;
        private List<QueueEntry> _incomingQueueSnapshot;
        private int _incomingQueueSource;
        private int _incomingQueueRevision;
        private int _incomingQueueExpectedCount;
        private float _incomingQueueStartedAt;
        private int _pendingQueueSequence;
        private List<string> _pendingQueueVideoIds;
        private string _pendingQueueInput;
        private bool _pendingQueueClearInput;
        private float _pendingQueueStartedAt;
        private float _nextQueueRequestRetry;
        private int _queueLimit = DefaultQueueLimit;
        private int _queueCooldown = DefaultQueueCooldown;
        private ConfigEntry<int> _configuredQueueLimit;
        private ConfigEntry<int> _configuredQueueCooldown;
        private bool _queueStartRequested;
        private float _nextQueueStartAt;
        private int _playerVolume;
        private Vector2 _queueScroll;
        private Vector2 _accessScroll;
        private string _videoTitle = "";
        private string _queueStatus = "";
        private string _pipeName;

        private sealed class LobbyMember
        {
            public string Name;
            public int PeerToken;
        }

        private sealed class QueueEntry
        {
            public string VideoId;
            public string Title;
            public string AddedBy;
            public int OwnerPeerToken;
            public string OwnerPlayerKey;
            public int AdmissionSequence;
        }

        private sealed class QueueBatchRequest
        {
            public string[] VideoIds;
            public float StartedAt;
        }

        private void Awake()
        {
            _instance = this;
            _localPeerToken = CreatePeerToken();
            _queueAuthorityReadyAt = Time.unscaledTime + QueueAuthorityWait;
            _queueAuthoritySince = Time.unscaledTime;
            _queueAuthorityObservedAt = Time.unscaledTime;
            _configuredQueueLimit = Config.Bind("Queue", "MaxPendingPerPlayer", DefaultQueueLimit,
                new ConfigDescription("Maximum pending videos per player when you host the lobby. The playing video does not count.", new AcceptableValueRange<int>(1, 30)));
            _configuredQueueCooldown = Config.Bind("Queue", "AddCooldownSeconds", DefaultQueueCooldown,
                new ConfigDescription("Seconds between accepted queue additions per player when you host the lobby.", new AcceptableValueRange<int>(0, 300)));
            InitializeSavedPlaylists();
            _harmony = new Harmony("codex.ontogether.school-screen");
            bool boardDisplayPatch = TryPatch(
                AccessTools.Method(typeof(QuadPainterGPU), "LateUpdate"),
                postfix: new HarmonyMethod(typeof(SchoolScreenPlugin), nameof(AfterBoardUpdate)));
            bool boardNetworkPatch = TryPatch(
                AccessTools.Method(typeof(QuadPainterGPU), "FillTheBlanksRPC_Original_2"),
                prefix: new HarmonyMethod(typeof(SchoolScreenPlugin), nameof(ReceiveBoardCommand)));

            if (boardDisplayPatch && boardNetworkPatch)
                Logger.LogInfo("Installed school screen board handlers.");
            _videoTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _status = "Find the whiteboard in the school building.";
            Logger.LogInfo("On-Together School Screen loaded.");
        }

        private bool TryPatch(System.Reflection.MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (original == null)
            {
                Logger.LogWarning("Could not find a game method required by the school screen mod; skipping that patch.");
                return false;
            }

            try
            {
                _harmony.Patch(original, prefix: prefix, postfix: postfix);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Skipped incompatible patch for " + original.DeclaringType.FullName + "." + original.Name + ": " + ex.GetBaseException().Message);
                return false;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9)) _panelOpen = !_panelOpen;
            PumpVideoMetadata();
            UpdateHostState();
            if (_lobbyStateObserved && !_observedLobbyActive) return;
            FindSchoolBoard();
            PumpQueues();
            UpdateLobbyPresence();
            UpdateQueueCoordination();
            UpdatePendingQueueRequest();
            UpdateQueuePlayback();
            if (Time.unscaledTime >= _nextVolumeUpdate)
            {
                _nextVolumeUpdate = Time.unscaledTime + 0.25f;
                UpdateLocalVolume(false);
            }
            if (_pendingJpeg != null && Time.unscaledTime - _lastFrameTime >= (1f / 30f))
            {
                byte[] jpeg = Interlocked.Exchange(ref _pendingJpeg, null);
                if (jpeg != null && ImageConversion.LoadImage(_videoTexture, jpeg, false))
                {
                    _lastFrameTime = Time.unscaledTime;
                    ApplyTexture();
                }
            }
            if (IsBoardNetworkReady(_schoolBoard) && !string.IsNullOrEmpty(_videoId) && IsLocalPlaybackCoordinator() && Time.unscaledTime >= _nextSync)
            {
                _nextSync = Time.unscaledTime + 10.0f;
                SendBoardCommand("SYNC", _videoId, _videoTime, _playerState);
            }
        }

        private void FindSchoolBoard()
        {
            if (IsBoardNetworkReady(_schoolBoard) && _schoolRenderer != null && _schoolRenderer.gameObject != null) return;
            _schoolBoard = null;
            _schoolRenderer = null;
            _schoolMaterial = null;
            if (Time.unscaledTime < _nextBoardSearch) return;
            _nextBoardSearch = Time.unscaledTime + 0.5f;
            QuadPainterGPU[] boards = Resources.FindObjectsOfTypeAll<QuadPainterGPU>();
            for (int i = 0; i < boards.Length; i++)
            {
                QuadPainterGPU board = boards[i];
                if (!IsBoardNetworkReady(board)) continue;
                if (!string.Equals(board.gameObject.name, "DrawingBoard", StringComparison.Ordinal)) continue;
                Transform parent = board.transform;
                bool inSchool = false;
                while (parent != null)
                {
                    if (parent.name == "MD_SchoolInterior") { inSchool = true; break; }
                    parent = parent.parent;
                }
                if (!inSchool) continue;
                Renderer renderer = board.GetComponent<Renderer>();
                if (renderer == null) continue;
                _schoolBoard = board;
                _schoolRenderer = renderer;
                _schoolMaterial = _schoolRenderer.material;
                _status = "School whiteboard found. Press F9 to open controls.";
                StartBrowser();
                Logger.LogInfo("Attached to the network-spawned DrawingBoard inside MD_SchoolInterior.");
                break;
            }
        }

        private static bool IsBoardNetworkReady(QuadPainterGPU board)
        {
            // Scene/prefab copies can be visible before PurrNet assigns their identity.
            // Its RPC wrapper logs instead of throwing when called on an unspawned copy.
            return board != null && board.gameObject.scene.IsValid() && board.gameObject.activeInHierarchy
                && board.isSpawned && board.id.HasValue && board.networkManager != null
                && board.PaintColors != null && board.PaintColors.Length > 0;
        }

        private void StartBrowser()
        {
            if (_browser != null || _pipe != null) return;
            string folder = Path.GetDirectoryName(typeof(SchoolScreenPlugin).Assembly.Location);
            string browserPath = Path.Combine(folder, "SchoolScreenBrowser.exe");
            if (!File.Exists(browserPath))
            {
                _status = "Browser helper is missing beside the plugin.";
                return;
            }
            _pipeName = "OTSchoolScreen_" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
            Task.Run((Action)(() => PipeWorker(_pipe)));
            try
            {
                _browser = Process.Start(new ProcessStartInfo(browserPath, _pipeName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = folder
                });
                _status = "Starting local browser…";
            }
            catch (Exception ex)
            {
                _status = "Could not start browser: " + ex.Message;
            }
        }

        private void PipeWorker(NamedPipeServerStream pipe)
        {
            try
            {
                pipe.WaitForConnection();
                _pipeConnected = true;
                _browserQueue.Enqueue("STATUS|WebView2 connected");
                using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                {
                    while (pipe.IsConnected)
                    {
                        byte type = reader.ReadByte();
                        int length = reader.ReadInt32();
                        if (length < 0 || length > 4 * 1024 * 1024) break;
                        byte[] payload = reader.ReadBytes(length);
                        if (payload.Length != length) break;
                        if (type == (byte)'F') Interlocked.Exchange(ref _pendingJpeg, payload);
                        else if (type == (byte)'S') _browserQueue.Enqueue(Encoding.UTF8.GetString(payload));
                        else if (type == (byte)'E') _browserQueue.Enqueue("ERROR|" + Encoding.UTF8.GetString(payload));
                    }
                }
            }
            catch (Exception ex)
            {
                _browserQueue.Enqueue("ERROR|" + ex.GetBaseException().Message);
            }
            finally { _pipeConnected = false; }
        }

        private void PumpQueues()
        {
            string message;
            while (_networkQueue.TryDequeue(out message)) HandleNetworkCommand(message);
            while (_browserQueue.TryDequeue(out message)) HandleBrowserMessage(message);
        }

        private void HandleBrowserMessage(string message)
        {
            if (HandleVideoMetadataMessage(message)) return;
            if (message.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                _status = "WebView2 error: " + message.Substring(6);
                Logger.LogError(_status);
                return;
            }
            if (message.StartsWith("STATUS|", StringComparison.Ordinal))
            {
                _status = message.Substring(7);
                if (_pipeConnected)
                {
                    if (!string.IsNullOrEmpty(_videoId))
                    {
                        OpenBrowserVideo();
                        if (_playerState == 2) SendHelper("PAUSE");
                    }
                    UpdateLocalVolume(true);
                    RequestTitlesForQueue();
                }
                return;
            }
            if (message.StartsWith("STATE|", StringComparison.Ordinal))
            {
                string[] fields = message.Split('|');
                float seconds;
                int state;
                int loadId;
                if (fields.Length >= 6 && float.TryParse(fields[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds)
                    && int.TryParse(fields[2], out state) && int.TryParse(fields[5], out loadId)
                    && _playbackSession.Observe(fields[4], loadId, state))
                {
                    _videoTime = seconds;
                    _playerState = state;
                    if (fields.Length >= 4)
                    {
                        _videoTitle = fields[3].Trim();
                        if (!string.IsNullOrWhiteSpace(_videoTitle) && IsVideoId(_videoId))
                        {
                            _queueTitleCache[_videoId] = _videoTitle;
                            UpdateQueuedVideoTitle(_videoId, _videoTitle);
                        }
                    }
                }
                return;
            }
            if (message.StartsWith("TITLE|", StringComparison.Ordinal))
            {
                string[] fields = message.Split('|');
                if (fields.Length >= 3 && IsVideoId(fields[1]))
                {
                    string title = fields[2].Trim();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        _queueTitleCache[fields[1]] = title;
                        UpdateQueuedVideoTitle(fields[1], title);
                    }
                }
            }
        }

        private void UpdateQueuedVideoTitle(string videoId, string title)
        {
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                if (string.Equals(_videoQueue[i].VideoId, videoId, StringComparison.Ordinal))
                    _videoQueue[i].Title = title;
            }
        }

        private void RequestQueueTitle(string videoId)
        {
            if (!_pipeConnected || !IsVideoId(videoId) || (_queueTitleCache.ContainsKey(videoId) && _videoDurations.ContainsKey(videoId))) return;
            if (_queueTitleLookups.Add(videoId)) SendHelper("LOOKUP\t" + videoId);
        }

        private void RequestTitlesForQueue()
        {
            for (int i = 0; i < _videoQueue.Count; i++) RequestQueueTitle(_videoQueue[i].VideoId);
        }

        private void UpdateHostState()
        {
            TextChannelManager channelManager = null;
            bool isHost = false;
            try
            {
                channelManager = NetworkSingleton<TextChannelManager>.I;
                if (channelManager != null) isHost = ReadIsServer(channelManager);
            }
            catch { }

            string lobbyCode = null;
            bool? lobbyActive = null;
            try
            {
                MultiplayerManager multiplayer = MonoSingleton<MultiplayerManager>.I;
                if (multiplayer != null)
                {
                    lobbyActive = multiplayer.LobbyStatus;
                    lobbyCode = multiplayer.LobbyCode ?? "";
                }
            }
            catch { }

            bool reset = false;
            if (lobbyActive.HasValue)
            {
                if (_lobbyStateObserved && _observedLobbyActive && !lobbyActive.Value)
                {
                    ResetLobbyState();
                    reset = true;
                }
                _observedLobbyActive = lobbyActive.Value;
                _lobbyStateObserved = true;
            }
            if (lobbyCode != null)
            {
                if (!reset && _lobbyIdentityInitialized && !string.Equals(_observedLobbyCode, lobbyCode, StringComparison.Ordinal))
                    ResetLobbyState();
                _observedLobbyCode = lobbyCode;
                _lobbyIdentityInitialized = true;
            }
            _host = channelManager != null && isHost && lobbyActive != false;
            if (_host) _moddedHostPeerToken = _localPeerToken;
            else if (_moddedHostPeerToken == _localPeerToken) _moddedHostPeerToken = 0;
        }

        private void ResetLobbyState()
        {
            SendHelper("CLEAR");
            SendHelper("RESET_LOOKUPS");
            Interlocked.Exchange(ref _pendingJpeg, null);
            _videoId = "";
            _videoTitle = "";
            _videoTime = 0f;
            _playerState = -1;
            _playbackSession.Clear();
            _queueStartRequested = false;
            _nextQueueStartAt = 0f;
            _videoQueue.Clear();
            _peerSkipVotes.Clear();
            _peerLastSeen.Clear();
            _peerPlayerKeys.Clear();
            _processedQueueEvents.Clear();
            _queueAdmissionResults.Clear();
            _queueAdmissionHistory.Clear();
            _queueLastAcceptedAt.Clear();
            _queueOwnerKeys.Clear();
            _queueOwnerNames.Clear();
            _incomingQueueBatches.Clear();
            _processedPlaybackEvents.Clear();
            _moddedPlayers.Clear();
            _blockedQueuePlayers.Clear();
            _queueTitleLookups.Clear();
            _localVotedSkip = false;
            _reportedModCount = 0;
            _voteCount = 0;
            _votesRequired = 0;
            _localPeerToken = CreatePeerToken();
            _queueEventSequence = 0;
            _playbackControllerToken = 0;
            _moddedHostPeerToken = 0;
            _queueCoordinatorToken = 0;
            _announcedQueueCoordinatorToken = 0;
            _queueAuthorityReadyAt = Time.unscaledTime + QueueAuthorityWait;
            _queueAuthoritySince = Time.unscaledTime;
            _queueAuthorityObservedAt = Time.unscaledTime;
            _queueSnapshotRevision = 0;
            ClearIncomingQueueSnapshot();
            ClearPendingQueueRequest();
            _queueLimit = DefaultQueueLimit;
            _queueCooldown = DefaultQueueCooldown;
            _queueStatus = "";
            _activeTab = 0;
            _queueScroll = Vector2.zero;
            _accessScroll = Vector2.zero;
            _peerLastSeen[_localPeerToken] = Time.unscaledTime;
            _nextSync = 0f;
            _nextLobbyHeartbeat = 0f;
            _schoolBoard = null;
            _schoolRenderer = null;
            _schoolMaterial = null;
            _nextBoardSearch = 0f;
            string ignored;
            while (_networkQueue.TryDequeue(out ignored)) { }
            _status = "Lobby changed. Previous video and queue cleared.";
        }

        private void UpdateLobbyPresence()
        {
            if (!IsBoardNetworkReady(_schoolBoard) || Time.unscaledTime < _nextLobbyHeartbeat) return;
            _nextLobbyHeartbeat = Time.unscaledTime + 5f;
            BroadcastPeerPresence();
            PruneStalePeers();
            if (_host)
            {
                bool hadHost = _moddedPlayers.ContainsKey(HostMemberKey);
                EnsureHostMember();
                if (!hadHost) BroadcastVoteStatus();
            }
        }

        private void EnsureHostMember()
        {
            LobbyMember member;
            if (!_moddedPlayers.TryGetValue(HostMemberKey, out member))
            {
                member = new LobbyMember();
                _moddedPlayers.Add(HostMemberKey, member);
            }
            member.Name = GetLocalPlayerName();
            member.PeerToken = _localPeerToken;
            _peerPlayerKeys[_localPeerToken] = HostMemberKey;
            _moddedHostPeerToken = _localPeerToken;
        }

        private void PruneStalePeers()
        {
            var expired = new List<int>();
            foreach (KeyValuePair<int, float> entry in _peerLastSeen)
            {
                if (entry.Key != _localPeerToken && Time.unscaledTime - entry.Value > 20f)
                    expired.Add(entry.Key);
            }
            if (expired.Count == 0) return;
            for (int i = 0; i < expired.Count; i++)
            {
                _peerLastSeen.Remove(expired[i]);
                _peerSkipVotes.Remove(expired[i]);
                string playerKey;
                if (_peerPlayerKeys.TryGetValue(expired[i], out playerKey))
                {
                    _peerPlayerKeys.Remove(expired[i]);
                    if (!_peerPlayerKeys.ContainsValue(playerKey)) _moddedPlayers.Remove(playerKey);
                }
            }
            if (!_peerLastSeen.ContainsKey(_playbackControllerToken))
                _playbackControllerToken = GetLowestActivePeerToken();
            if (!_peerLastSeen.ContainsKey(_moddedHostPeerToken)) _moddedHostPeerToken = 0;
            if (_queueCoordinatorToken != _localPeerToken && !IsActivePeerToken(_queueCoordinatorToken))
            {
                _queueCoordinatorToken = 0;
                _announcedQueueCoordinatorToken = 0;
                _queueAuthorityReadyAt = Time.unscaledTime + 0.75f;
                ClearIncomingQueueSnapshot();
            }
            BroadcastVoteStatus(true);
            if (!string.IsNullOrEmpty(_videoId) && GetActiveVoteCount() >= GetRequiredVotes() && IsLocalPlaybackCoordinator())
                SkipCurrentVideo();
        }

        private string GetLocalPlayerName()
        {
            try
            {
                TextChannelManager manager = NetworkSingleton<TextChannelManager>.I;
                if (manager != null && !string.IsNullOrWhiteSpace(manager.UserName)) return manager.UserName.Trim();
            }
            catch { }
            return "Player";
        }

        private static int CreatePeerToken()
        {
            int token = BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0) & PeerTokenMask;
            return token == 0 ? 1 : token;
        }

        private static float EncodeBoardMarker(int peerToken)
        {
            int token = peerToken & PeerTokenMask;
            return BoardCommandMarker + (token / PeerTokenScale);
        }

        private static bool TryDecodeBoardMarker(float value, out int peerToken)
        {
            peerToken = 0;
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;
            float delta = value >= BoardCommandMarker ? value - BoardCommandMarker : LegacyBoardCommandMarker - value;
            if (delta < 0f || delta > PeerTokenMask / PeerTokenScale)
            {
                peerToken = 0;
                return false;
            }
            peerToken = Mathf.Clamp(Mathf.RoundToInt(delta * PeerTokenScale), 0, PeerTokenMask);
            return true;
        }

        private int GetActivePeerCount()
        {
            int count = 1; // The local mod always belongs to its own active session.
            foreach (KeyValuePair<int, float> peer in _peerLastSeen)
                if (peer.Key != _localPeerToken && peer.Value >= Time.unscaledTime - 20f) count++;
            return count;
        }

        private int GetActiveVoteCount()
        {
            int count = 0;
            foreach (int voter in _peerSkipVotes)
            {
                float lastSeen;
                if (voter == _localPeerToken || (_peerLastSeen.TryGetValue(voter, out lastSeen)
                    && lastSeen >= Time.unscaledTime - 20f)) count++;
            }
            return count;
        }

        private int GetLowestActivePeerToken()
        {
            int lowest = _localPeerToken;
            foreach (KeyValuePair<int, float> entry in _peerLastSeen)
                if (entry.Value >= Time.unscaledTime - 20f && entry.Key < lowest) lowest = entry.Key;
            return lowest;
        }

        private void BroadcastPeerPresence()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            _peerLastSeen[_localPeerToken] = Time.unscaledTime;
            SendPeerPacket(11, "PEER|" + _localPeerToken);
            if (IsLocalPlaybackCoordinator()) BroadcastQueueAuthority();
        }

        private void SendPeerPacket(int packetType, string localMessage)
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            var uv = new Vector2(EncodeBoardMarker(_localPeerToken), packetType);
            if (!SendBoardPayload(uv, Vector2.zero, 0)) return;
            if (!string.IsNullOrEmpty(localMessage)) HandleNetworkCommand(localMessage);
        }

        private static bool ReadIsServer(object instance)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty("isServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.PropertyType == typeof(bool)) return (bool)property.GetValue(instance, null);
                FieldInfo field = type.GetField("isServer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null && field.FieldType == typeof(bool)) return (bool)field.GetValue(instance);
            }
            return false;
        }

        private void UpdateLocalVolume(bool force)
        {
            float attenuation = 0f;
            try
            {
                TextChannelManager manager = NetworkSingleton<TextChannelManager>.I;
                if (_schoolBoard != null && manager != null && manager.MainPlayer != null)
                {
                    float distance = Vector3.Distance(_schoolBoard.transform.position, manager.MainPlayer.position);
                    if (distance <= NearBoardDistance) attenuation = 1f;
                    else if (distance < SilentDistance)
                    {
                        float remaining = 1f - ((distance - NearBoardDistance) / (SilentDistance - NearBoardDistance));
                        attenuation = Mathf.Pow(remaining, 0.65f);
                    }
                }
            }
            catch { attenuation = 0f; }

            _effectiveVolume = _muted ? 0 : Mathf.Clamp(Mathf.RoundToInt(_maximumVolume * attenuation), 0, 200);
            int playerVolume = Mathf.Clamp(_effectiveVolume, 0, 100);
            _playerVolume = playerVolume;
            if (force || _lastSentVolume < 0 || Math.Abs(playerVolume - _lastSentVolume) >= 2 || (playerVolume == 0) != (_lastSentVolume == 0))
            {
                _lastSentVolume = playerVolume;
                SendHelper("VOLUME\t" + playerVolume);
            }
        }

        private void ApplyTexture()
        {
            if (_schoolMaterial == null) return;
            if (_schoolMaterial.HasProperty("_MainTex")) _schoolMaterial.SetTexture("_MainTex", _videoTexture);
            else _schoolMaterial.mainTexture = _videoTexture;
        }

        private void OnGUI()
        {
            if (!_panelOpen) return;
            if (!_guiInitialized)
            {
                CreateGuiStyles();
                CreateLibraryGuiStyles();
                _guiInitialized = true;
            }
            if (!_windowPositioned)
            {
                _windowRect = new Rect(24, 72, LibraryWindowWidth, LibraryWindowHeight);
                _windowPositioned = true;
            }
            float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 780f, Screen.height / 660f));
            scale = Mathf.Max(.1f, scale);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Mathf.Max(0, Screen.width / scale - LibraryWindowWidth));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Mathf.Max(0, Screen.height / scale - LibraryWindowHeight));
            try { _windowRect = GUI.Window(61429, _windowRect, DrawLibraryControlPanel, GUIContent.none, _windowStyle); }
            finally { GUI.matrix = previousMatrix; }
        }

        private void CreateGuiStyles()
        {
            _windowTexture = MakeLibraryRoundedTexture(new Color32(27, 30, 41, 255), false);
            _fieldTexture = MakeGuiTexture(new Color32(16, 20, 31, 255));
            _buttonTexture = MakeGuiTexture(new Color32(39, 47, 65, 255));
            _buttonHoverTexture = MakeGuiTexture(new Color32(54, 64, 86, 255));
            _accentTexture = MakeGuiTexture(new Color32(163, 148, 255, 255));
            _accentHoverTexture = MakeGuiTexture(new Color32(183, 171, 255, 255));
            _badgeTexture = MakeGuiTexture(new Color32(54, 64, 85, 255));
            _statusTexture = MakeGuiTexture(new Color32(240, 105, 112, 255));
            _meterBackgroundTexture = MakeGuiTexture(new Color32(10, 13, 21, 255));

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(9, 9, 9, 9)
            };
            _windowStyle.normal.background = _windowTexture;
            _windowStyle.onNormal.background = _windowTexture;
            _windowStyle.normal.textColor = Color.white;
            _titleStyle = MakeLabelStyle(19, FontStyle.Bold, new Color32(244, 246, 255, 255), TextAnchor.MiddleLeft);
            _subtitleStyle = MakeLabelStyle(10, FontStyle.Bold, new Color32(151, 163, 188, 255), TextAnchor.MiddleLeft);
            _sectionStyle = MakeLabelStyle(10, FontStyle.Bold, new Color32(164, 177, 204, 255), TextAnchor.MiddleLeft);
            _statusStyle = MakeLabelStyle(12, FontStyle.Normal, new Color32(221, 227, 241, 255), TextAnchor.MiddleLeft);
            _hintStyle = MakeLabelStyle(10, FontStyle.Normal, new Color32(157, 169, 193, 255), TextAnchor.MiddleCenter);

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 10, 4, 4),
                normal = { background = _fieldTexture, textColor = new Color32(242, 245, 255, 255) },
                focused = { background = _fieldTexture, textColor = Color.white }
            };
            _placeholderStyle = MakeLabelStyle(12, FontStyle.Normal, new Color32(117, 129, 152, 255), TextAnchor.MiddleLeft);
            _primaryButtonStyle = MakeButtonStyle(13, FontStyle.Bold, _accentTexture, _accentHoverTexture, Color.white);
            _controlButtonStyle = MakeButtonStyle(13, FontStyle.Bold, _buttonTexture, _buttonHoverTexture, new Color32(239, 243, 255, 255));
            _seekButtonStyle = MakeButtonStyle(12, FontStyle.Normal, _buttonTexture, _buttonHoverTexture, new Color32(207, 216, 235, 255));
            _hostBadgeStyle = MakeButtonStyle(10, FontStyle.Bold, _accentTexture, _accentHoverTexture, Color.white);
            _viewerBadgeStyle = MakeButtonStyle(10, FontStyle.Bold, _badgeTexture, _buttonHoverTexture, new Color32(206, 216, 237, 255));
            _closeButtonStyle = MakeButtonStyle(11, FontStyle.Bold, _windowTexture, _buttonHoverTexture, new Color32(175, 185, 205, 255));
            _volumeSliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
            _volumeSliderStyle.normal.background = _buttonTexture;
            _volumeSliderStyle.hover.background = _buttonHoverTexture;
            _volumeThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                fixedWidth = 14,
                fixedHeight = 14
            };
            _volumeThumbStyle.normal.background = _accentTexture;
            _volumeThumbStyle.hover.background = _accentHoverTexture;
        }

        private static GUIStyle MakeLabelStyle(int size, FontStyle fontStyle, Color color, TextAnchor alignment)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fontStyle,
                normal = { textColor = color },
                alignment = alignment
            };
        }

        private static GUIStyle MakeButtonStyle(int size, FontStyle fontStyle, Texture2D normal, Texture2D hover, Color color)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = size,
                fontStyle = fontStyle,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(8, 8, 4, 4),
                border = new RectOffset(3, 3, 3, 3)
            };
            style.normal.background = normal;
            style.normal.textColor = color;
            style.hover.background = hover;
            style.hover.textColor = Color.white;
            style.active.background = hover;
            style.active.textColor = Color.white;
            style.focused.background = normal;
            style.focused.textColor = color;
            return style;
        }

        private static Texture2D MakeGuiTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void ShareVideo()
        {
            string id = ParseVideoId(_url);
            if (string.IsNullOrEmpty(id))
            {
                _status = "Paste a YouTube URL or an 11-character video ID.";
                return;
            }
            if (!IsBoardNetworkReady(_schoolBoard))
            {
                _status = "Connecting to the school whiteboard. Try again shortly.";
                return;
            }
            AddVideoToQueue();
        }

        private void Control(string command)
        {
            if (command == "CLEAR")
            {
                if (!_host) return;
                _localVotedSkip = false;
                SendBoardCommand("CLEAR");
                BroadcastVoteStatus();
                return;
            }
            SendBoardCommand(command);
        }

        private void Seek(float amount)
        {
            if (!_host) return;
            SendBoardCommand("SEEK", null, Mathf.Max(0, _videoTime + amount));
        }

        private void AddVideoToQueue()
        {
            if (_pendingQueueSequence != 0)
            {
                _queueStatus = "Waiting for the queue to confirm your video…";
                return;
            }
            string id = ParseVideoId(_url);
            if (string.IsNullOrEmpty(id))
            {
                _queueStatus = "Enter a valid YouTube link or video ID.";
                return;
            }

            if (!IsBoardNetworkReady(_schoolBoard))
            {
                _queueStatus = "Connecting to the school whiteboard. Try again shortly.";
                return;
            }
            BeginLocalQueueRequest(new List<string> { id }, true);
        }

        // Playlist UI can submit selected saved tracks without links or repeated cooldowns.
        private bool QueueSavedVideos(IEnumerable<string> videoIds)
        {
            if (_pendingQueueSequence != 0)
            {
                _queueStatus = "Waiting for the queue to confirm your videos…";
                return false;
            }
            var selected = new List<string>();
            if (videoIds != null)
            {
                foreach (string id in videoIds)
                {
                    if (!IsVideoId(id)) continue;
                    selected.Add(id);
                    if (selected.Count == 30) break;
                }
            }
            if (selected.Count == 0)
            {
                _queueStatus = "Choose at least one saved video.";
                return false;
            }
            if (!IsBoardNetworkReady(_schoolBoard))
            {
                _queueStatus = "Connecting to the school whiteboard. Try again shortly.";
                return false;
            }
            return BeginLocalQueueRequest(selected, false);
        }

        private bool BeginLocalQueueRequest(List<string> videoIds, bool clearInput)
        {
            _pendingQueueSequence = NextQueueEventSequence();
            _pendingQueueVideoIds = videoIds;
            _pendingQueueInput = _url;
            _pendingQueueClearInput = clearInput;
            _pendingQueueStartedAt = Time.unscaledTime;
            _nextQueueRequestRetry = Time.unscaledTime + 2f;
            _queueStatus = "Adding your video…";
            if (!SendQueueAddRequest())
            {
                ClearPendingQueueRequest();
                _queueStatus = "Could not add the video. Try again shortly.";
                return false;
            }
            return true;
        }

        private bool SendQueueAddRequest()
        {
            if (_pendingQueueSequence == 0 || _pendingQueueVideoIds == null || _pendingQueueVideoIds.Count == 0) return false;
            // Save the request locally: a synchronous local acknowledgment clears pending state.
            List<string> ids = _pendingQueueVideoIds;
            int sequence = _pendingQueueSequence;
            float marker = EncodeBoardMarker(_localPeerToken);
            bool batch = ids.Count > 1;
            if (batch)
            {
                if (!SendBoardPayload(new Vector2(marker, 169f), new Vector2(sequence, ids.Count), 0)) return false;
                BeginQueueBatch(_localPeerToken, sequence, ids.Count);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                Vector2 uv = new Vector2(marker, (sequence << 4) | 10);
                Vector2 previous = new Vector2(PackVideoIdGroup(id, 0, 4), PackVideoIdGroup(id, 4, 4));
                if (!SendBoardPayload(uv, previous, PackVideoIdGroup(id, 8, 3) | (i << 18))) return false;
                if (batch) ReceiveQueueBatchItem(_localPeerToken, sequence, i, id);
                else ApplyQueueAdd(_localPeerToken, sequence, id);
            }
            if (batch)
            {
                if (!SendBoardPayload(new Vector2(marker, 185f), new Vector2(sequence, ids.Count), 0)) return false;
                CompleteQueueBatch(_localPeerToken, sequence, ids.Count);
            }
            return true;
        }

        private void UpdatePendingQueueRequest()
        {
            if (_incomingQueueBatches.Count > 0)
            {
                var expired = new List<long>();
                foreach (KeyValuePair<long, QueueBatchRequest> batch in _incomingQueueBatches)
                    if (Time.unscaledTime - batch.Value.StartedAt >= 8f) expired.Add(batch.Key);
                for (int i = 0; i < expired.Count; i++) _incomingQueueBatches.Remove(expired[i]);
            }
            if (_incomingQueueSnapshot != null && Time.unscaledTime - _incomingQueueStartedAt >= 8f)
                ClearIncomingQueueSnapshot();
            if (_pendingQueueSequence == 0) return;
            if (Time.unscaledTime - _pendingQueueStartedAt >= 12f)
            {
                ClearPendingQueueRequest();
                _queueStatus = "Queue not confirmed. Update all mod users and try again.";
                return;
            }
            if (Time.unscaledTime >= _nextQueueRequestRetry && IsBoardNetworkReady(_schoolBoard))
            {
                _nextQueueRequestRetry = Time.unscaledTime + 2f;
                SendQueueAddRequest();
            }
        }

        private void ClearPendingQueueRequest()
        {
            OnPlaylistQueueAdmission(0);
            _pendingQueueSequence = 0;
            _pendingQueueVideoIds = null;
            _pendingQueueInput = null;
            _pendingQueueClearInput = false;
            _pendingQueueStartedAt = 0f;
            _nextQueueRequestRetry = 0f;
        }

        private void ToggleSkipVote()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            if (_host)
            {
                SkipCurrentVideo();
                return;
            }

            bool vote = !_localVotedSkip;
            SendPeerPacket(vote ? 12 : 13, (vote ? "VOTE|" : "UNVOTE|") + _localPeerToken);
        }

        private void SkipCurrentVideo()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            if ((!_host && !IsLocalPlaybackCoordinator()) || string.IsNullOrEmpty(_videoId)) return;
            AdvanceQueue();
        }

        private bool IsLocalPlaybackCoordinator()
        {
            return GetQueueCoordinatorToken() == _localPeerToken;
        }

        private int GetQueueCoordinatorToken()
        {
            if (_host)
            {
                if (_queueCoordinatorToken != _localPeerToken)
                    _queueAuthoritySince = _queueAuthorityObservedAt = Time.unscaledTime;
                return _queueCoordinatorToken = _localPeerToken;
            }
            if (IsActivePeerToken(_moddedHostPeerToken))
            {
                if (_queueCoordinatorToken != _moddedHostPeerToken)
                    _queueAuthoritySince = _queueAuthorityObservedAt = Time.unscaledTime;
                return _queueCoordinatorToken = _moddedHostPeerToken;
            }
            if (IsActivePeerToken(_queueCoordinatorToken)) return _queueCoordinatorToken;
            if (Time.unscaledTime < _queueAuthorityReadyAt) return 0;
            _queueCoordinatorToken = IsActivePeerToken(_playbackControllerToken)
                ? _playbackControllerToken : GetLowestActivePeerToken();
            _queueAuthoritySince = Time.unscaledTime;
            _queueAuthorityObservedAt = Time.unscaledTime;
            return _queueCoordinatorToken;
        }

        private void UpdateQueueCoordination()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            int coordinator = GetQueueCoordinatorToken();
            if (coordinator != _localPeerToken)
            {
                _announcedQueueCoordinatorToken = 0;
                return;
            }
            if (_host)
            {
                _queueLimit = Math.Max(1, Math.Min(30, _configuredQueueLimit.Value));
                _queueCooldown = Math.Max(0, Math.Min(300, _configuredQueueCooldown.Value));
            }
            if (_announcedQueueCoordinatorToken == coordinator) return;
            _announcedQueueCoordinatorToken = coordinator;
            BroadcastQueueSnapshot();
            BroadcastVoteStatus(true);
        }

        private void BroadcastQueueAuthority()
        {
            if (!IsLocalPlaybackCoordinator() || !IsBoardNetworkReady(_schoolBoard)) return;
            int ageTenths = Math.Min(0xFFFFFF, Math.Max(0, (int)((Time.unscaledTime - _queueAuthoritySince) * 10f)));
            SendBoardPayload(new Vector2(EncodeBoardMarker(_localPeerToken), 137f), new Vector2(ageTenths, 0), 1);
            SendBoardPayload(new Vector2(EncodeBoardMarker(_localPeerToken), 105f), new Vector2(_queueLimit, _queueCooldown), 0);
        }

        private bool IsActivePeerToken(int peerToken)
        {
            if (peerToken == _localPeerToken) return true;
            float lastSeen;
            return peerToken > 0 && _peerLastSeen.TryGetValue(peerToken, out lastSeen)
                && lastSeen >= Time.unscaledTime - 20f;
        }

        private bool CanAcceptQueueSnapshot(int peerToken)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask) return false;
            if (_host) return peerToken == _localPeerToken;
            if (IsActivePeerToken(_moddedHostPeerToken)) return peerToken == _moddedHostPeerToken;
            if (IsActivePeerToken(_queueCoordinatorToken)) return peerToken == _queueCoordinatorToken;
            if (IsActivePeerToken(_playbackControllerToken))
            {
                _queueCoordinatorToken = _playbackControllerToken;
                _queueAuthoritySince = _queueAuthorityObservedAt = Time.unscaledTime;
                return peerToken == _queueCoordinatorToken;
            }
            // An existing coordinator answers a late join before the newcomer elects itself.
            _queueCoordinatorToken = peerToken;
            _queueAuthoritySince = _queueAuthorityObservedAt = Time.unscaledTime;
            return true;
        }

        private bool CanAcceptPlaybackCommand(int peerToken, bool synchronization = false)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask) return false;
            if (_host) return peerToken == _localPeerToken;
            if (IsActivePeerToken(_moddedHostPeerToken)) return peerToken == _moddedHostPeerToken;
            if (IsActivePeerToken(_queueCoordinatorToken)) return peerToken == _queueCoordinatorToken;
            if (IsActivePeerToken(_playbackControllerToken)) return peerToken == _playbackControllerToken;
            if (_playbackControllerToken != 0) return peerToken == GetLowestActivePeerToken();
            // A newcomer can discover an existing video even when their token is lower.
            return synchronization || peerToken == GetLowestActivePeerToken();
        }

        private void UpdateQueuePlayback()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            if (string.IsNullOrEmpty(_videoId) && Time.unscaledTime < _nextQueueStartAt) return;
            if (_playbackSession.ShouldAdvance(IsLocalPlaybackCoordinator(), _queueStartRequested, _videoQueue.Count > 0))
                AdvanceQueue();
        }

        private void RequestQueueStart()
        {
            if (!string.IsNullOrEmpty(_videoId) || _videoQueue.Count == 0) return;
            SendPeerPacket(0, "QUEUE_START|" + _localPeerToken);
        }

        private void ScheduleQueueStart()
        {
            if (!string.IsNullOrEmpty(_videoId)) return;
            _queueStartRequested = true;
            // Give peer presence and a current-video sync time to arrive before electing an idle controller.
            _nextQueueStartAt = Time.unscaledTime + 0.75f;
        }

        private void AdvanceQueue()
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;
            if (!_host && !IsLocalPlaybackCoordinator()) return;
            _queueStartRequested = false;
            _localVotedSkip = false;
            if (_videoQueue.Count > 0)
            {
                QueueEntry next = _videoQueue[0];
                _videoQueue.RemoveAt(0);
                BroadcastQueueSnapshot();
                SendBoardCommand("OPEN", next.VideoId, 0f, 1);
            }
            else SendBoardCommand("CLEAR");
            BroadcastVoteStatus();
        }

        private void PlayQueuedVideo(int index)
        {
            if (!_host || index < 0 || index >= _videoQueue.Count || !IsBoardNetworkReady(_schoolBoard)) return;
            QueueEntry selected = _videoQueue[index];
            _videoQueue.RemoveAt(index);
            BroadcastQueueSnapshot();
            SendBoardCommand("OPEN", selected.VideoId, 0f, 1);
            _activeTab = 0;
        }

        private void RemoveQueuedVideo(int index)
        {
            if (!_host || index < 0 || index >= _videoQueue.Count || !IsBoardNetworkReady(_schoolBoard)) return;
            _videoQueue.RemoveAt(index);
            BroadcastQueueSnapshot();
        }

        private void BroadcastQueueSnapshot()
        {
            if (!IsBoardNetworkReady(_schoolBoard) || !IsLocalPlaybackCoordinator()) return;
            BroadcastQueueAuthority();
            int revision = _queueSnapshotRevision = (_queueSnapshotRevision % QueueEventSequenceMask) + 1;
            float marker = EncodeBoardMarker(_localPeerToken);
            SendBoardPayload(new Vector2(marker, 153f), new Vector2(revision, _videoQueue.Count), 0);
            // Legacy viewers understand the clear/items but do not support limits or acknowledgments.
            SendBoardPayload(new Vector2(marker, 7f), Vector2.zero, 0);
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                QueueEntry item = _videoQueue[i];
                string id = item.VideoId;
                int first = PackVideoIdGroup(id, 0, 4);
                int second = PackVideoIdGroup(id, 4, 4);
                int third = PackVideoIdGroup(id, 8, 3);
                int encodedOperation = (i << 4) | 8;
                SendBoardPayload(new Vector2(marker, encodedOperation), new Vector2(first, second), third);
                if (item.OwnerPeerToken <= 0)
                {
                    // An old-version queue has no ownership; attribute it to its coordinator.
                    item.OwnerPeerToken = _localPeerToken;
                    item.OwnerPlayerKey = GetQueueOwnerKey(_localPeerToken);
                    item.AddedBy = GetLocalPlayerName();
                }
                SendBoardPayload(new Vector2(marker, 73f), new Vector2(item.OwnerPeerToken, i), item.AdmissionSequence);
            }
            SendBoardPayload(new Vector2(marker, 121f), new Vector2(revision, _videoQueue.Count), 0);
        }

        private void ClearIncomingQueueSnapshot()
        {
            _incomingQueueSnapshot = null;
            _incomingQueueSource = 0;
            _incomingQueueRevision = 0;
            _incomingQueueExpectedCount = 0;
            _incomingQueueStartedAt = 0f;
        }

        private void BeginQueueSnapshot(int source, int revision, int count)
        {
            if (source == _localPeerToken || revision <= 0 || revision > QueueEventSequenceMask || count < 0 || count > 30
                || !CanAcceptQueueSnapshot(source)) return;
            _incomingQueueSnapshot = new List<QueueEntry>(count);
            _incomingQueueSource = source;
            _incomingQueueRevision = revision;
            _incomingQueueExpectedCount = count;
            _incomingQueueStartedAt = Time.unscaledTime;
        }

        private void CompleteQueueSnapshot(int source, int revision, int count)
        {
            if (!CanAcceptQueueSnapshot(source) || _incomingQueueSnapshot == null || source != _incomingQueueSource
                || revision != _incomingQueueRevision || count != _incomingQueueExpectedCount || count != _incomingQueueSnapshot.Count) return;
            for (int i = 0; i < _incomingQueueSnapshot.Count; i++)
                if (_incomingQueueSnapshot[i].OwnerPeerToken <= 0) return;
            _videoQueue.Clear();
            _videoQueue.AddRange(_incomingQueueSnapshot);
            var admissionCounts = new Dictionary<long, int>();
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                QueueEntry item = _videoQueue[i];
                long eventKey = ((long)item.OwnerPeerToken << 20) | (uint)item.AdmissionSequence;
                if (item.AdmissionSequence <= 0 || _queueAdmissionResults.ContainsKey(eventKey)) continue;
                int countForRequest;
                admissionCounts.TryGetValue(eventKey, out countForRequest);
                admissionCounts[eventKey] = countForRequest + 1;
            }
            foreach (KeyValuePair<long, int> admission in admissionCounts) RememberQueueAdmission(admission.Key, admission.Value << 4);
            ClearIncomingQueueSnapshot();
            RequestTitlesForQueue();
            if (_videoQueue.Count > 0 && string.IsNullOrEmpty(_videoId)) ScheduleQueueStart();
        }

        private void ApplyQueueOwner(int source, int ownerToken, int index, int sequence)
        {
            if (!CanAcceptQueueSnapshot(source) || ownerToken <= 0 || ownerToken > PeerTokenMask || index < 0 || index >= 30
                || sequence < 0 || sequence > QueueEventSequenceMask) return;
            List<QueueEntry> target = _incomingQueueSnapshot != null && _incomingQueueSource == source
                ? _incomingQueueSnapshot : _videoQueue;
            if (index >= target.Count) return;
            QueueEntry item = target[index];
            item.OwnerPeerToken = ownerToken;
            item.AdmissionSequence = sequence;
            item.OwnerPlayerKey = GetQueueOwnerKey(ownerToken);
            item.AddedBy = GetQueueOwnerName(ownerToken);
            _queueOwnerKeys[ownerToken] = item.OwnerPlayerKey;
            _queueOwnerNames[ownerToken] = item.AddedBy;
        }

        private void ApplyQueueAuthority(int source, int ageTenths)
        {
            if (source <= 0 || source > PeerTokenMask || ageTenths < 0 || ageTenths > 0xFFFFFF
                || (_host && source != _localPeerToken)) return;
            if (IsActivePeerToken(_moddedHostPeerToken) && source != _moddedHostPeerToken) return;
            if (IsActivePeerToken(_queueCoordinatorToken) && source != _queueCoordinatorToken
                && source != _moddedHostPeerToken)
            {
                if (Time.unscaledTime - _queueAuthorityObservedAt > QueueElectionConflictWindow) return;
                float localAge = Mathf.Max(0f, Time.unscaledTime - _queueAuthoritySince);
                float remoteAge = ageTenths / 10f;
                // Reconcile simultaneous elections without letting a fresh late join displace an established coordinator.
                if (!(remoteAge > localAge + 2f || (Math.Abs(remoteAge - localAge) <= 2f && source < _queueCoordinatorToken))) return;
            }
            bool hadActiveCoordinator = IsActivePeerToken(_queueCoordinatorToken);
            if (_queueCoordinatorToken != source)
            {
                ClearIncomingQueueSnapshot();
                if (!hadActiveCoordinator) _queueAuthorityObservedAt = Time.unscaledTime;
            }
            _peerLastSeen[source] = Time.unscaledTime;
            _queueCoordinatorToken = source;
            _queueAuthoritySince = Time.unscaledTime - ageTenths / 10f;
            _announcedQueueCoordinatorToken = 0;
        }

        private void BroadcastVoteStatus(bool includeSnapshot = false, bool transferIdleState = false)
        {
            _reportedModCount = GetActivePeerCount();
            _voteCount = GetActiveVoteCount();
            _votesRequired = GetRequiredVotes();
            if ((IsLocalPlaybackCoordinator() || transferIdleState) && IsBoardNetworkReady(_schoolBoard))
            {
                float marker = EncodeBoardMarker(_localPeerToken);
                if (includeSnapshot)
                {
                    SendBoardPayload(new Vector2(marker, 25f), Vector2.zero, 0);
                    foreach (KeyValuePair<int, float> peer in _peerLastSeen)
                        if (peer.Value >= Time.unscaledTime - 20f)
                            SendBoardPayload(new Vector2(marker, 57f), new Vector2(peer.Key, 0), 0);
                    foreach (int voter in _peerSkipVotes)
                    {
                        float lastSeen;
                        if (voter == _localPeerToken || (_peerLastSeen.TryGetValue(voter, out lastSeen)
                            && lastSeen >= Time.unscaledTime - 20f))
                            SendBoardPayload(new Vector2(marker, 41f), new Vector2(voter, 0), 0);
                    }
                }
                SendBoardPayload(new Vector2(marker, 9f), new Vector2(_reportedModCount, _voteCount), _votesRequired);
            }
        }

        private int GetRequiredVotes()
        {
            return Math.Max(1, (int)Math.Ceiling(GetActivePeerCount() * 0.30));
        }

        private bool SendBoardPayload(Vector2 uv, Vector2 prevUV, int colIndex)
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return false;
            // Clamp to the top-right corner on vanilla boards: just one cell can be erased.
            // A negative previous X prevents interpolation; erase mode ignores the color field.
            if (uv.y == 0f) uv.y = 16f;
            var safePrevUv = new Vector2(BoardPacketEnvelope.EncodeFirstGroup(prevUV.x), prevUV.y);
            try
            {
                _schoolBoard.FillTheBlanksRPC(uv, safePrevUv, colIndex, BoardPacketEnvelope.IsErase, BoardPacketEnvelope.IsBigErase);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not send lobby update: " + ex.GetBaseException().Message);
                return false;
            }
        }

        private static string ShortTitle(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
            int length = Math.Max(1, maxLength - 1);
            if (char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }

        private void SendHelper(string command)
        {
            if (_pipe == null || !_pipeConnected) return;
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            try
            {
                lock (_pipeWriteLock)
                {
                    _pipe.Write(bytes, 0, bytes.Length);
                    _pipe.Flush();
                }
            }
            catch (Exception ex) { _status = "Browser command failed: " + ex.Message; }
        }

        private void OpenBrowserVideo()
        {
            int loadId = _playbackSession.Begin(_videoId);
            SendHelper("OPEN\t" + _videoId + "\t" + _videoTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "\t" + loadId);
        }

        private int NextQueueEventSequence()
        {
            _queueEventSequence = (_queueEventSequence + 1) & QueueEventSequenceMask;
            if (_queueEventSequence == 0) _queueEventSequence = 1;
            return _queueEventSequence;
        }

        private bool AcceptPlaybackEvent(int peerToken, int sequence)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask || sequence <= 0 || sequence > QueueEventSequenceMask) return false;
            return _processedPlaybackEvents.Add(((long)peerToken << 20) | (uint)sequence);
        }

        private void SendBoardCommand(string command, string videoId = null, float seconds = 0f, int state = 1)
        {
            if (!IsBoardNetworkReady(_schoolBoard)) return;

            int operation;
            int encodedOperation;
            int sequence = 0;
            int firstGroup = 0;
            int secondGroup = 0;
            int thirdGroup = 0;
            Vector2 prevUv = Vector2.zero;
            switch (command)
            {
                case "OPEN":
                    if (!IsVideoId(videoId)) return;
                    sequence = NextQueueEventSequence();
                    operation = 1;
                    firstGroup = PackVideoIdGroup(videoId, 0, 4);
                    secondGroup = PackVideoIdGroup(videoId, 4, 4);
                    thirdGroup = PackVideoIdGroup(videoId, 8, 3);
                    prevUv = new Vector2(firstGroup, secondGroup);
                    break;
                case "PLAY": operation = 2; break;
                case "PAUSE": operation = 3; break;
                case "SEEK":
                    operation = 4;
                    thirdGroup = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, seconds) * 10f), 0, 16777215);
                    break;
                case "CLEAR": operation = 5; sequence = NextQueueEventSequence(); break;
                case "SYNC":
                    if (!IsVideoId(videoId)) return;
                    operation = 6;
                    int timeTenths = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(0f, seconds) * 10f), 0, 1048575);
                    encodedOperation = (timeTenths << 4) | operation | (state == 2 ? 8 : 0);
                    firstGroup = PackVideoIdGroup(videoId, 0, 4);
                    secondGroup = PackVideoIdGroup(videoId, 4, 4);
                    thirdGroup = PackVideoIdGroup(videoId, 8, 3);
                    prevUv = new Vector2(firstGroup, secondGroup);
                    goto Send;
                default: return;
            }

            encodedOperation = (sequence << 4) | operation;
            if (command != "SEEK" && command != "OPEN") thirdGroup = 0;

        Send:
            Vector2 uv = new Vector2(EncodeBoardMarker(_localPeerToken), encodedOperation);
            string localCommand;
            if (operation == 1) localCommand = "OPEN|" + videoId + "|0|" + _localPeerToken + "|" + sequence;
            else if (operation == 2) localCommand = "PLAY|" + _localPeerToken;
            else if (operation == 3) localCommand = "PAUSE|" + _localPeerToken;
            else if (operation == 4) localCommand = "SEEK|" + (thirdGroup / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + _localPeerToken;
            else if (operation == 5) localCommand = "CLEAR|" + _localPeerToken + "|" + sequence;
            else localCommand = "SYNC|" + videoId + "|" + ((encodedOperation >> 4) / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + ((encodedOperation & 8) != 0 ? "2" : "1") + "|" + _localPeerToken;

            if (SendBoardPayload(uv, prevUv, thirdGroup)) HandleNetworkCommand(localCommand);
        }

        private void HandleNetworkCommand(string message)
        {
            string[] fields = message.Split('|');
            if (fields.Length == 0) return;
            string command = fields[0];
            if (command == "QUEUE_PLAY" || command == "QUEUE_REMOVE") return; // Retired unrestricted entry actions.
            if (command == "OPEN" && fields.Length >= 5)
            {
                int peerToken, sequence;
                if (!IsVideoId(fields[1]) || !int.TryParse(fields[3], out peerToken) || !CanAcceptPlaybackCommand(peerToken)
                    || !int.TryParse(fields[4], out sequence) || !AcceptPlaybackEvent(peerToken, sequence)) return;
                _peerLastSeen[peerToken] = Time.unscaledTime;
                _videoId = fields[1];
                _videoTitle = "";
                _videoTime = ParseFloat(fields[2]);
                _playerState = 1;
                _playbackControllerToken = peerToken;
                _queueStartRequested = false;
                _nextSync = Time.unscaledTime + 10f;
                _localVotedSkip = false;
                _peerSkipVotes.Clear();
                OpenBrowserVideo();
                UpdateLocalVolume(true);
                BroadcastVoteStatus();
            }
            else if (command == "PLAY" || command == "PAUSE")
            {
                if (string.IsNullOrEmpty(_videoId)) return;
                if (command == "PLAY") _playbackSession.Resume();
                _playerState = command == "PLAY" ? 1 : 2;
                SendHelper(command);
            }
            else if (command == "SEEK" && fields.Length >= 2)
            {
                int source;
                if (fields.Length < 3 || !int.TryParse(fields[2], out source) || !CanAcceptPlaybackCommand(source)) return;
                _playbackSession.Resume();
                _videoTime = ParseFloat(fields[1]);
                SendHelper("SEEK\t" + _videoTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (command == "CLEAR" && fields.Length >= 3)
            {
                int peerToken, sequence;
                if (!int.TryParse(fields[1], out peerToken) || !CanAcceptPlaybackCommand(peerToken) || !int.TryParse(fields[2], out sequence)
                    || !AcceptPlaybackEvent(peerToken, sequence)) return;
                _videoId = "";
                _videoTitle = "";
                _playerState = -1;
                _videoTime = 0f;
                _playbackControllerToken = 0;
                _queueStartRequested = false;
                _playbackSession.Clear();
                _localVotedSkip = false;
                _peerSkipVotes.Clear();
                SendHelper("CLEAR");
                BroadcastVoteStatus();
            }
            else if (command == "SYNC" && fields.Length >= 4)
            {
                string id = fields[1];
                float synchronizedTime = ParseFloat(fields[2]);
                int synchronizedState;
                int.TryParse(fields[3], out synchronizedState);
                int syncSource = 0;
                if (fields.Length >= 5) int.TryParse(fields[4], out syncSource);
                if (!IsVideoId(id) || !CanAcceptPlaybackCommand(syncSource, true)) return;
                _peerLastSeen[syncSource] = Time.unscaledTime;
                float localTime = _videoTime;
                int localState = _playerState;
                if (syncSource != 0) _playbackControllerToken = syncSource;
                _playerState = synchronizedState;
                if (_videoId != id)
                {
                    _videoId = id;
                    _videoTitle = "";
                    _localVotedSkip = false;
                    _peerSkipVotes.Clear();
                    _videoTime = synchronizedTime;
                    _queueStartRequested = false;
                    OpenBrowserVideo();
                }
                else if (Math.Abs(localTime - synchronizedTime) > 1.5f || _playbackSession.HasEnded)
                {
                    _playbackSession.Resume();
                    _videoTime = synchronizedTime;
                    SendHelper("SEEK\t" + synchronizedTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (synchronizedState == 2 && localState != 2) SendHelper("PAUSE");
                else if (synchronizedState == 1 && localState != 1) SendHelper("PLAY");
                UpdateLocalVolume(true);
            }
            else if (command == "PEER" && fields.Length >= 2)
            {
                int peerToken;
                if (int.TryParse(fields[1], out peerToken)) HandlePeerPresence(peerToken);
            }
            else if (command == "QUEUE_START" && fields.Length >= 2)
            {
                int peerToken;
                if (!int.TryParse(fields[1], out peerToken) || peerToken <= 0 || peerToken > PeerTokenMask) return;
                _peerLastSeen[peerToken] = Time.unscaledTime;
                if (_videoQueue.Count > 0) ScheduleQueueStart();
            }
            else if ((command == "VOTE" || command == "UNVOTE") && fields.Length >= 2)
            {
                int peerToken;
                if (int.TryParse(fields[1], out peerToken)) ApplyPeerVote(peerToken, command == "VOTE");
            }
            else if (command == "QUEUE_ADD" && fields.Length >= 4)
            {
                int peerToken, sequence;
                if (int.TryParse(fields[1], out peerToken) && int.TryParse(fields[2], out sequence))
                {
                    int index = 0;
                    if (fields.Length >= 5 && !int.TryParse(fields[4], out index)) return;
                    long eventKey = ((long)peerToken << 20) | (uint)sequence;
                    if (_incomingQueueBatches.ContainsKey(eventKey)) ReceiveQueueBatchItem(peerToken, sequence, index, fields[3]);
                    else if (index == 0) ApplyQueueAdd(peerToken, sequence, fields[3]);
                }
            }
            else if ((command == "QUEUE_BATCH_BEGIN" || command == "QUEUE_BATCH_END") && fields.Length >= 4)
            {
                int peerToken, sequence, count;
                if (!int.TryParse(fields[1], out peerToken) || !int.TryParse(fields[2], out sequence)
                    || !int.TryParse(fields[3], out count)) return;
                if (command == "QUEUE_BATCH_BEGIN") BeginQueueBatch(peerToken, sequence, count);
                else CompleteQueueBatch(peerToken, sequence, count);
            }
            else if (command == "QUEUE_CLEAR")
            {
                int peerToken;
                if (fields.Length < 2 || !int.TryParse(fields[1], out peerToken)
                    || peerToken == _localPeerToken || !CanAcceptQueueSnapshot(peerToken)) return;
                if (_incomingQueueSnapshot != null && _incomingQueueSource == peerToken) _incomingQueueSnapshot.Clear();
                else _videoQueue.Clear();
            }
            else if (command == "QUEUE_ITEM" && fields.Length >= 3)
            {
                int index;
                if (!int.TryParse(fields[1], out index) || index < 0 || index >= 30 || !IsVideoId(fields[2])) return;
                int peerToken;
                if (fields.Length < 4 || !int.TryParse(fields[3], out peerToken)
                    || peerToken == _localPeerToken || !CanAcceptQueueSnapshot(peerToken)) return;
                string title;
                _queueTitleCache.TryGetValue(fields[2], out title);
                QueueEntry item = new QueueEntry { VideoId = fields[2], Title = string.IsNullOrWhiteSpace(title) ? fields[2] : title, AddedBy = "" };
                List<QueueEntry> target = _incomingQueueSnapshot != null && _incomingQueueSource == peerToken
                    ? _incomingQueueSnapshot : _videoQueue;
                if (index < target.Count) target[index] = item;
                else if (index == target.Count) target.Add(item);
                else return;
                RequestQueueTitle(item.VideoId);
                // A newcomer may become the idle coordinator before the first video starts.
                // Give an existing-video sync time to arrive before advancing this snapshot.
                if (_incomingQueueSnapshot == null && string.IsNullOrEmpty(_videoId)) ScheduleQueueStart();
            }
            else if (command == "QUEUE_AUTHORITY" && fields.Length >= 3)
            {
                int source, age;
                if (int.TryParse(fields[1], out source) && int.TryParse(fields[2], out age)) ApplyQueueAuthority(source, age);
            }
            else if ((command == "QUEUE_BEGIN" || command == "QUEUE_END") && fields.Length >= 4)
            {
                int source, revision, count;
                if (!int.TryParse(fields[1], out source) || !int.TryParse(fields[2], out revision)
                    || !int.TryParse(fields[3], out count)) return;
                if (command == "QUEUE_BEGIN") BeginQueueSnapshot(source, revision, count);
                else CompleteQueueSnapshot(source, revision, count);
            }
            else if (command == "QUEUE_OWNER" && fields.Length >= 5)
            {
                int source, owner, index, sequence;
                if (int.TryParse(fields[1], out source) && int.TryParse(fields[2], out owner)
                    && int.TryParse(fields[3], out index) && int.TryParse(fields[4], out sequence)) ApplyQueueOwner(source, owner, index, sequence);
            }
            else if (command == "QUEUE_RESULT" && fields.Length >= 5)
            {
                int source, requester, sequence, result;
                if (int.TryParse(fields[1], out source) && int.TryParse(fields[2], out requester)
                    && int.TryParse(fields[3], out sequence) && int.TryParse(fields[4], out result))
                    ApplyQueueAdmissionResult(source, requester, sequence, result);
            }
            else if (command == "QUEUE_RULES" && fields.Length >= 4)
            {
                int source, limit, cooldown;
                if (!int.TryParse(fields[1], out source) || !CanAcceptQueueSnapshot(source)
                    || !int.TryParse(fields[2], out limit) || limit < 1 || limit > 30
                    || !int.TryParse(fields[3], out cooldown) || cooldown < 0 || cooldown > 300) return;
                _queueLimit = limit;
                _queueCooldown = cooldown;
            }
            else if (command == "VOTE_RESET" && fields.Length >= 2)
            {
                int source;
                if (!int.TryParse(fields[1], out source) || !CanAcceptQueueSnapshot(source)) return;
                _peerSkipVotes.Clear();
                _localVotedSkip = false;
            }
            else if ((command == "VOTE_ENTRY" || command == "PEER_STATE") && fields.Length >= 3)
            {
                int source, token;
                if (!int.TryParse(fields[1], out source) || !CanAcceptQueueSnapshot(source)
                    || !int.TryParse(fields[2], out token) || token <= 0 || token > PeerTokenMask) return;
                _peerLastSeen[token] = Time.unscaledTime;
                if (command == "VOTE_ENTRY")
                {
                    _peerSkipVotes.Add(token);
                    if (token == _localPeerToken) _localVotedSkip = true;
                }
            }
            else if (command == "VOTE_STATUS" && fields.Length >= 5)
            {
                int source;
                if (!int.TryParse(fields[4], out source) || !CanAcceptQueueSnapshot(source)) return;
                int.TryParse(fields[1], out _reportedModCount);
                int.TryParse(fields[2], out _voteCount);
                int.TryParse(fields[3], out _votesRequired);
            }
        }

        private void HandlePeerPresence(int peerToken)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask) return;
            bool isNewPeer = !_peerLastSeen.ContainsKey(peerToken);
            _peerLastSeen[peerToken] = Time.unscaledTime;
            if (isNewPeer && peerToken != _localPeerToken && IsLocalPlaybackCoordinator()
                && !string.IsNullOrEmpty(_videoId) && !_playbackSession.HasEnded)
                SendBoardCommand("SYNC", _videoId, _videoTime, _playerState);
            bool sendsSnapshot = IsLocalPlaybackCoordinator();
            if (isNewPeer && peerToken != _localPeerToken && sendsSnapshot)
                BroadcastQueueSnapshot();
            BroadcastVoteStatus(isNewPeer && peerToken != _localPeerToken,
                isNewPeer && peerToken != _localPeerToken && sendsSnapshot);
        }

        private void ApplyPeerVote(int peerToken, bool vote)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask || string.IsNullOrEmpty(_videoId)) return;
            _peerLastSeen[peerToken] = Time.unscaledTime;
            if (vote) _peerSkipVotes.Add(peerToken);
            else _peerSkipVotes.Remove(peerToken);
            if (peerToken == _localPeerToken) _localVotedSkip = vote;
            // Restore individual vote state after a concurrent late-join snapshot.
            BroadcastVoteStatus(true);
            if (GetActiveVoteCount() >= GetRequiredVotes() && IsLocalPlaybackCoordinator())
                SkipCurrentVideo();
        }

        private void ApplyQueueAdd(int peerToken, int sequence, string videoId)
        {
            if (!IsVideoId(videoId)) return;
            ApplyQueueBatchAdd(peerToken, sequence, new[] { videoId });
        }

        private void BeginQueueBatch(int peerToken, int sequence, int count)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask || sequence <= 0 || sequence > QueueEventSequenceMask
                || count < 2 || count > 30) return;
            long eventKey = ((long)peerToken << 20) | (uint)sequence;
            var obsolete = new List<long>();
            string requesterKey = GetQueueOwnerKey(peerToken);
            foreach (long key in _incomingQueueBatches.Keys)
                if (key != eventKey && string.Equals(GetQueueOwnerKey((int)(key >> 20)), requesterKey, StringComparison.Ordinal)) obsolete.Add(key);
            for (int i = 0; i < obsolete.Count; i++) _incomingQueueBatches.Remove(obsolete[i]);
            if (!_incomingQueueBatches.ContainsKey(eventKey) && _incomingQueueBatches.Count >= 64) return;
            _incomingQueueBatches[eventKey] = new QueueBatchRequest { VideoIds = new string[count], StartedAt = Time.unscaledTime };
        }

        private void ReceiveQueueBatchItem(int peerToken, int sequence, int index, string videoId)
        {
            QueueBatchRequest request;
            long eventKey = ((long)peerToken << 20) | (uint)sequence;
            if (!_incomingQueueBatches.TryGetValue(eventKey, out request) || index < 0 || index >= request.VideoIds.Length
                || !IsVideoId(videoId)) return;
            request.VideoIds[index] = videoId;
        }

        private void CompleteQueueBatch(int peerToken, int sequence, int count)
        {
            QueueBatchRequest request;
            long eventKey = ((long)peerToken << 20) | (uint)sequence;
            if (!_incomingQueueBatches.TryGetValue(eventKey, out request) || count != request.VideoIds.Length) return;
            _incomingQueueBatches.Remove(eventKey);
            for (int i = 0; i < count; i++) if (!IsVideoId(request.VideoIds[i])) return;
            ApplyQueueBatchAdd(peerToken, sequence, request.VideoIds);
        }

        private void ApplyQueueBatchAdd(int peerToken, int sequence, IList<string> videoIds)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask || sequence <= 0 || sequence > QueueEventSequenceMask
                || videoIds == null || videoIds.Count < 1 || videoIds.Count > 30) return;
            for (int i = 0; i < videoIds.Count; i++) if (!IsVideoId(videoIds[i])) return;
            _peerLastSeen[peerToken] = Time.unscaledTime;
            // Requests never mutate a viewer's queue. Only the established coordinator admits them.
            if (!IsLocalPlaybackCoordinator()) return;
            if (!_host && Time.unscaledTime - _queueAuthoritySince < QueueAuthorityWait) return;
            string playerKey = GetQueueOwnerKey(peerToken);
            if (peerToken != _localPeerToken && !_peerPlayerKeys.ContainsKey(peerToken)) return;
            long eventKey = ((long)peerToken << 20) | (uint)sequence;
            int result;
            if (_queueAdmissionResults.TryGetValue(eventKey, out result))
            {
                SendQueueAdmissionResult(peerToken, sequence, result);
                return;
            }
            if (_host && _blockedQueuePlayers.Contains(playerKey)) result = 1;
            else if (CountPendingQueueEntries(playerKey) >= _queueLimit) result = 2;
            else
            {
                float lastAccepted;
                float remaining = _queueLastAcceptedAt.TryGetValue(playerKey, out lastAccepted)
                    ? _queueCooldown - (Time.unscaledTime - lastAccepted) : 0f;
                result = remaining > 0f ? (Math.Min(300, (int)Math.Ceiling(remaining)) << 4) | 3
                    : _videoQueue.Count >= 30 ? 4 : 0;
            }
            if (result == 0)
            {
                int acceptedCount = Math.Min(videoIds.Count, Math.Min(_queueLimit - CountPendingQueueEntries(playerKey), 30 - _videoQueue.Count));
                result = acceptedCount << 4;
                for (int i = 0; i < acceptedCount; i++)
                {
                    string videoId = videoIds[i];
                    string title;
                    _queueTitleCache.TryGetValue(videoId, out title);
                    _videoQueue.Add(new QueueEntry
                    {
                        VideoId = videoId,
                        Title = string.IsNullOrWhiteSpace(title) ? videoId : title,
                        OwnerPeerToken = peerToken,
                        AdmissionSequence = sequence,
                        OwnerPlayerKey = playerKey,
                        AddedBy = GetQueueOwnerName(peerToken)
                    });
                    RequestQueueTitle(videoId);
                }
                _queueLastAcceptedAt[playerKey] = Time.unscaledTime;
                ScheduleQueueStart();
            }
            RememberQueueAdmission(eventKey, result);
            // A rejection also corrects the optimistic queue held by an older mod viewer.
            BroadcastQueueSnapshot();
            SendQueueAdmissionResult(peerToken, sequence, result);
        }

        private int CountPendingQueueEntries(string playerKey)
        {
            int count = 0;
            for (int i = 0; i < _videoQueue.Count; i++)
                if (string.Equals(_videoQueue[i].OwnerPlayerKey, playerKey, StringComparison.Ordinal)) count++;
            return count;
        }

        private string GetQueueOwnerKey(int peerToken)
        {
            if (peerToken == _localPeerToken) return "__local_queue_player__";
            string playerKey;
            if (_peerPlayerKeys.TryGetValue(peerToken, out playerKey)) return playerKey;
            return _queueOwnerKeys.TryGetValue(peerToken, out playerKey) ? playerKey : "token:" + peerToken;
        }

        private string GetQueueOwnerName(int peerToken)
        {
            if (peerToken == _localPeerToken) return GetLocalPlayerName();
            string playerKey;
            LobbyMember member;
            if (_peerPlayerKeys.TryGetValue(peerToken, out playerKey) && _moddedPlayers.TryGetValue(playerKey, out member)
                && !string.IsNullOrWhiteSpace(member.Name)) return member.Name;
            string name;
            return _queueOwnerNames.TryGetValue(peerToken, out name) ? name : "Player";
        }

        private void RememberQueueAdmission(long eventKey, int result)
        {
            bool existing = _queueAdmissionResults.ContainsKey(eventKey);
            _queueAdmissionResults[eventKey] = result;
            if (existing) return;
            _queueAdmissionHistory.Enqueue(eventKey);
            while (_queueAdmissionHistory.Count > 1024)
                _queueAdmissionResults.Remove(_queueAdmissionHistory.Dequeue());
        }

        private void SendQueueAdmissionResult(int peerToken, int sequence, int result)
        {
            if (!IsLocalPlaybackCoordinator()) return;
            if (!SendBoardPayload(new Vector2(EncodeBoardMarker(_localPeerToken), 89f), new Vector2(peerToken, sequence), result)) return;
            if (peerToken == _localPeerToken) ApplyQueueAdmissionResult(_localPeerToken, peerToken, sequence, result);
        }

        private void ApplyQueueAdmissionResult(int source, int requester, int sequence, int result)
        {
            if (!CanAcceptQueueSnapshot(source) || requester <= 0 || requester > PeerTokenMask || sequence <= 0
                || sequence > QueueEventSequenceMask || result < 0) return;
            int reason = result & 15;
            int value = result >> 4;
            if (reason > 4 || (reason == 0 ? value < 1 || value > 30 : reason == 3 ? value < 1 || value > 300 : value != 0)) return;
            long eventKey = ((long)requester << 20) | (uint)sequence;
            // Future coordinators retain receipts that they observed in this lobby.
            RememberQueueAdmission(eventKey, result);
            if (requester != _localPeerToken || sequence != _pendingQueueSequence) return;
            if (reason == 0)
            {
                if (_pendingQueueClearInput && string.Equals(_url, _pendingQueueInput, StringComparison.Ordinal)) _url = "";
                int selectedCount = _pendingQueueVideoIds == null ? 1 : _pendingQueueVideoIds.Count;
                _queueStatus = value < selectedCount ? "Added " + value + " of " + selectedCount + " videos; " + (selectedCount - value) + " skipped (queue limits)."
                    : value == 1 ? "Added to the queue." : "Added " + value + " videos to the queue.";
            }
            else if (reason == 1) _queueStatus = "The host has blocked you from adding videos.";
            else if (reason == 2) _queueStatus = "You already have " + _queueLimit + " videos waiting. Let one play first.";
            else if (reason == 3) _queueStatus = "Wait " + Math.Max(1, result >> 4) + " seconds before adding another video.";
            else if (reason == 4) _queueStatus = "The queue is full. Try again after a video plays.";
            else return;
            bool fromLink = _pendingQueueClearInput;
            if (!fromLink) OnPlaylistQueueAdmission(reason == 0 ? value : 0);
            ClearPendingQueueRequest();
            if (fromLink) _activeTab = 1;
        }

        private static int PackVideoIdGroup(string videoId, int start, int count)
        {
            int packed = 0;
            for (int i = 0; i < count; i++) packed = (packed * 64) + VideoIdCharacterValue(videoId[start + i]);
            return packed;
        }

        private static int VideoIdCharacterValue(char character)
        {
            if (character >= 'A' && character <= 'Z') return character - 'A';
            if (character >= 'a' && character <= 'z') return character - 'a' + 26;
            if (character >= '0' && character <= '9') return character - '0' + 52;
            if (character == '_') return 62;
            if (character == '-') return 63;
            return -1;
        }

        private static string UnpackVideoId(int firstGroup, int secondGroup, int thirdGroup)
        {
            var result = new StringBuilder(11);
            AppendVideoIdGroup(result, firstGroup, 4);
            AppendVideoIdGroup(result, secondGroup, 4);
            AppendVideoIdGroup(result, thirdGroup, 3);
            string id = result.ToString();
            return IsVideoId(id) ? id : null;
        }

        private static void AppendVideoIdGroup(StringBuilder result, int packed, int count)
        {
            int divisor = 1 << ((count - 1) * 6);
            for (int i = 0; i < count; i++)
            {
                int value = (packed / divisor) & 63;
                result.Append(value < 26 ? (char)('A' + value) : value < 52 ? (char)('a' + value - 26) : value < 62 ? (char)('0' + value - 52) : value == 62 ? '_' : '-');
                if (divisor > 1) packed %= divisor;
                divisor >>= 6;
            }
        }

        private bool BindPeerIdentity(int peerToken, PlayerID sender)
        {
            // PurrNet preserves the original RPC sender when the server relays a board packet.
            // The token is only a protocol identifier; access controls use the actual sender.
            string playerKey = "player:" + sender.ToString();
            string existingKey;
            if (_peerPlayerKeys.TryGetValue(peerToken, out existingKey)
                && !string.Equals(existingKey, playerKey, StringComparison.Ordinal)) return false;

            LobbyMember member;
            if (!_moddedPlayers.TryGetValue(playerKey, out member))
            {
                member = new LobbyMember();
                _moddedPlayers.Add(playerKey, member);
            }
            if (member.PeerToken != 0 && member.PeerToken != peerToken)
            {
                // One network player counts once, even if their mod creates a new session token.
                _peerLastSeen.Remove(member.PeerToken);
                _peerSkipVotes.Remove(member.PeerToken);
                _peerPlayerKeys.Remove(member.PeerToken);
                // A reconnecting mod has empty playback state. An existing peer must
                // coordinate and send its snapshot before the returning player can follow.
                if (_playbackControllerToken == member.PeerToken) _playbackControllerToken = GetLowestActivePeerToken();
                if (_moddedHostPeerToken == member.PeerToken) _moddedHostPeerToken = 0;
                if (_queueCoordinatorToken == member.PeerToken)
                {
                    _queueCoordinatorToken = 0;
                    _announcedQueueCoordinatorToken = 0;
                    _queueAuthorityReadyAt = Time.unscaledTime + 0.75f;
                    ClearIncomingQueueSnapshot();
                }
            }
            member.PeerToken = peerToken;
            member.Name = GetRemotePlayerName(sender);
            _peerPlayerKeys[peerToken] = playerKey;
            _queueOwnerKeys[peerToken] = playerKey;
            _queueOwnerNames[peerToken] = member.Name;
            RebindQueuedOwner(_videoQueue, peerToken, playerKey, member.Name);
            if (_incomingQueueSnapshot != null) RebindQueuedOwner(_incomingQueueSnapshot, peerToken, playerKey, member.Name);
            if (IsLobbyHostSender(sender))
            {
                _moddedHostPeerToken = peerToken;
                // A valid packet from the actual host establishes its authority immediately,
                // even when a new viewer has not received the host's heartbeat yet.
                _peerLastSeen[peerToken] = Time.unscaledTime;
            }
            // New remote viewers stay unannounced until HandlePeerPresence sends their snapshot.
            return true;
        }

        private void RebindQueuedOwner(List<QueueEntry> entries, int peerToken, string playerKey, string name)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                QueueEntry item = entries[i];
                string previousKey;
                if (item.OwnerPeerToken == peerToken || string.Equals(item.OwnerPlayerKey, playerKey, StringComparison.Ordinal)
                    || (_queueOwnerKeys.TryGetValue(item.OwnerPeerToken, out previousKey)
                        && string.Equals(previousKey, playerKey, StringComparison.Ordinal)))
                {
                    if (item.OwnerPeerToken != peerToken) item.AdmissionSequence = 0;
                    item.OwnerPeerToken = peerToken;
                    item.OwnerPlayerKey = playerKey;
                    item.AddedBy = name;
                }
            }
        }

        private static string GetRemotePlayerName(PlayerID sender)
        {
            try
            {
                PlayerPanelController panel = NetworkSingleton<PlayerPanelController>.I;
                if (panel != null && panel.PlayerIDs != null && panel.IDInfos != null)
                {
                    int index = panel.PlayerIDs.IndexOf(sender);
                    if (index >= 0 && index < panel.IDInfos.Count && panel.IDInfos[index].Name != null)
                    {
                        string name = Encoding.Unicode.GetString(panel.IDInfos[index].Name).TrimEnd('\0').Trim();
                        if (!string.IsNullOrWhiteSpace(name)) return ShortTitle(name, 24);
                    }
                }
            }
            catch { }
            return "Player " + sender.ToString();
        }

        private static bool IsLobbyHostSender(PlayerID sender)
        {
            try
            {
                PlayerPanelController panel = NetworkSingleton<PlayerPanelController>.I;
                if (panel == null || panel.PlayerIDs == null || panel.PlayerSteamIDs == null || string.IsNullOrEmpty(panel.HostId)) return false;
                int index = panel.PlayerIDs.IndexOf(sender);
                return index >= 0 && index < panel.PlayerSteamIDs.Count
                    && string.Equals(panel.PlayerSteamIDs[index], panel.HostId, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool IsUnsignedPacketInteger(float value, int maximum)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f
                && value <= maximum && value == Math.Truncate(value);
        }

        private static bool IsValidBoardPacket(float operationValue, Vector2 payload, int colIndex)
        {
            if (!IsUnsignedPacketInteger(operationValue, 0xFFFFFF)
                || !IsUnsignedPacketInteger(payload.x, 0xFFFFFF)
                || !IsUnsignedPacketInteger(payload.y, 0xFFFFFF)
                || colIndex < 0 || colIndex > 0xFFFFFF) return false;
            int operation = (int)operationValue;
            int type = operation & 15;
            int extra = operation >> 4;
            bool emptyGroups = payload.x == 0f && payload.y == 0f;
            if (type == 1) return extra > 0 && colIndex <= 0x3FFFF;
            if (type == 10) return extra > 0 && (colIndex >> 18) < 30;
            if (type == 6 || type == 14) return colIndex <= 0x3FFFF;
            if (type == 8) return extra < 30 && colIndex <= 0x3FFFF;
            if (type == 9)
            {
                if (extra == 0) return payload.x >= 1f && payload.y <= payload.x
                    && colIndex == Math.Max(1, (int)Math.Ceiling(payload.x * 0.30));
                if (extra == 1) return emptyGroups && colIndex == 0;
                if (extra == 2 || extra == 3) return payload.x >= 1f && payload.x <= PeerTokenMask
                    && payload.y == 0f && colIndex == 0;
                if (extra == 4) return payload.x >= 1f && payload.x <= PeerTokenMask && payload.y < 30f && colIndex <= QueueEventSequenceMask;
                if (extra == 5)
                {
                    int reason = colIndex & 15;
                    int value = colIndex >> 4;
                    return payload.x >= 1f && payload.x <= PeerTokenMask && payload.y >= 1f && payload.y <= QueueEventSequenceMask
                        && reason <= 4 && (reason == 0 ? value >= 1 && value <= 30 : reason == 3 ? value >= 1 && value <= 300 : value == 0);
                }
                if (extra == 6) return payload.x >= 1f && payload.x <= 30f && payload.y <= 300f && colIndex == 0;
                if (extra == 7 || extra == 9) return payload.x >= 1f && payload.x <= QueueEventSequenceMask
                    && payload.y <= 30f && colIndex == 0;
                if (extra == 8) return payload.y == 0f && colIndex == 1;
                if (extra == 10 || extra == 11) return payload.x >= 1f && payload.x <= QueueEventSequenceMask
                    && payload.y >= 2f && payload.y <= 30f && colIndex == 0;
                return false;
            }
            if (type == 0) return extra <= 1 && emptyGroups && colIndex == 0;
            if (type == 5) return extra > 0 && emptyGroups && colIndex == 0;
            if (type == 4) return extra == 0 && emptyGroups;
            if (type == 2 || type == 3 || type == 7 || type == 11 || type == 12 || type == 13)
                return extra == 0 && emptyGroups && colIndex == 0;
            return false;
        }

        private static bool ReceiveBoardCommand(QuadPainterGPU __instance, Vector2 uv, Vector2 prevUV, int colIndex, RPCInfo rpcInfo)
        {
            SchoolScreenPlugin plugin = _instance;
            if (plugin == null || __instance != plugin._schoolBoard) return true;
            int peerToken;
            if (!TryDecodeBoardMarker(uv.x, out peerToken)) return true;
            // Local actions are applied explicitly. A relayed echo must not affect a later video.
            if (peerToken == plugin._localPeerToken) return false;
            prevUV.x = BoardPacketEnvelope.DecodeFirstGroup(prevUV.x);
            if (peerToken <= 0 || !IsValidBoardPacket(uv.y, prevUV, colIndex)) return false;
            if (!plugin.BindPeerIdentity(peerToken, rpcInfo.sender)) return false;

            int encodedOperation = Mathf.RoundToInt(uv.y);
            int packetType = encodedOperation & 15;
            if (packetType == 0)
            {
                plugin._networkQueue.Enqueue("QUEUE_START|" + peerToken);
                return false;
            }
            if (packetType == 7)
            {
                plugin._networkQueue.Enqueue("QUEUE_CLEAR|" + peerToken);
                return false;
            }
            if (packetType == 8)
            {
                int index = encodedOperation >> 4;
                string id = UnpackVideoId(Mathf.RoundToInt(prevUV.x), Mathf.RoundToInt(prevUV.y), colIndex);
                if (id != null) plugin._networkQueue.Enqueue("QUEUE_ITEM|" + index + "|" + id + "|" + peerToken);
                return false;
            }
            if (packetType == 9)
            {
                int subtype = encodedOperation >> 4;
                if (subtype == 0)
                    plugin._networkQueue.Enqueue("VOTE_STATUS|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y) + "|" + colIndex + "|" + peerToken);
                else if (subtype == 1) plugin._networkQueue.Enqueue("VOTE_RESET|" + peerToken);
                else if (subtype == 2 || subtype == 3)
                    plugin._networkQueue.Enqueue((subtype == 2 ? "VOTE_ENTRY|" : "PEER_STATE|") + peerToken + "|" + Mathf.RoundToInt(prevUV.x));
                else if (subtype == 4)
                    plugin._networkQueue.Enqueue("QUEUE_OWNER|" + peerToken + "|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y) + "|" + colIndex);
                else if (subtype == 5)
                    plugin._networkQueue.Enqueue("QUEUE_RESULT|" + peerToken + "|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y) + "|" + colIndex);
                else if (subtype == 6)
                    plugin._networkQueue.Enqueue("QUEUE_RULES|" + peerToken + "|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y));
                else if (subtype == 7 || subtype == 9)
                    plugin._networkQueue.Enqueue((subtype == 7 ? "QUEUE_END|" : "QUEUE_BEGIN|") + peerToken + "|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y));
                else if (subtype == 8) plugin._networkQueue.Enqueue("QUEUE_AUTHORITY|" + peerToken + "|" + Mathf.RoundToInt(prevUV.x));
                else if (subtype == 10 || subtype == 11)
                    plugin._networkQueue.Enqueue((subtype == 10 ? "QUEUE_BATCH_BEGIN|" : "QUEUE_BATCH_END|") + peerToken + "|" + Mathf.RoundToInt(prevUV.x) + "|" + Mathf.RoundToInt(prevUV.y));
                return false;
            }
            if (packetType == 10)
            {
                int sequence = encodedOperation >> 4;
                string id = UnpackVideoId(Mathf.RoundToInt(prevUV.x), Mathf.RoundToInt(prevUV.y), colIndex & 0x3FFFF);
                if (id != null) plugin._networkQueue.Enqueue("QUEUE_ADD|" + peerToken + "|" + sequence + "|" + id + "|" + (colIndex >> 18));
                return false;
            }
            if (packetType == 11)
            {
                plugin._networkQueue.Enqueue("PEER|" + peerToken);
                return false;
            }
            if (packetType == 12 || packetType == 13)
            {
                plugin._networkQueue.Enqueue((packetType == 12 ? "VOTE|" : "UNVOTE|") + peerToken);
                return false;
            }
            if (packetType == 15)
            {
                // Older releases allowed any peer to replace playback with an arbitrary queue entry.
                return false;
            }

            int operation = encodedOperation & 7;
            string command;
            if (operation == 1)
            {
                string id = UnpackVideoId(Mathf.RoundToInt(prevUV.x), Mathf.RoundToInt(prevUV.y), colIndex);
                if (id != null) plugin._networkQueue.Enqueue("OPEN|" + id + "|0|" + peerToken + "|" + (encodedOperation >> 4));
            }
            else if (operation == 2) plugin._networkQueue.Enqueue("PLAY|" + peerToken);
            else if (operation == 3) plugin._networkQueue.Enqueue("PAUSE|" + peerToken);
            else if (operation == 4) plugin._networkQueue.Enqueue("SEEK|" + (colIndex / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + peerToken);
            else if (operation == 5) plugin._networkQueue.Enqueue("CLEAR|" + peerToken + "|" + (encodedOperation >> 4));
            else if (operation == 6)
            {
                string id = UnpackVideoId(Mathf.RoundToInt(prevUV.x), Mathf.RoundToInt(prevUV.y), colIndex);
                if (id != null)
                {
                    float time = (encodedOperation >> 4) / 10f;
                    int state = (encodedOperation & 8) != 0 ? 2 : 1;
                    command = "SYNC|" + id + "|" + time.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + state + "|" + peerToken;
                    plugin._networkQueue.Enqueue(command);
                }
            }
            return false;
        }

        private static float ParseFloat(string value)
        {
            float parsed;
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed) ? Mathf.Max(0, parsed) : 0;
        }

        private static string ParseVideoId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string value = input.Trim();
            if (IsVideoId(value)) return value;
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) Uri.TryCreate("https://" + value, UriKind.Absolute, out uri);
            if (uri == null) return null;
            string id = null;
            if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.Trim('/').Split('/')[0];
            else if (uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            {
                id = QueryValue(uri.Query, "v");
                if (string.IsNullOrEmpty(id))
                {
                    string[] path = uri.AbsolutePath.Trim('/').Split('/');
                    if (path.Length >= 2 && (path[0] == "embed" || path[0] == "shorts" || path[0] == "live")) id = path[1];
                }
            }
            return IsVideoId(id) ? id : null;
        }

        private static bool IsVideoId(string value)
        {
            if (value == null || value.Length != 11) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private static string QueryValue(string query, string key)
        {
            string[] pairs = (query ?? "").TrimStart('?').Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int separator = pairs[i].IndexOf('=');
                if (separator < 0) continue;
                string name = Uri.UnescapeDataString(pairs[i].Substring(0, separator).Replace('+', ' '));
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pairs[i].Substring(separator + 1).Replace('+', ' '));
            }
            return null;
        }

        private void OnDestroy()
        {
            DisposeVideoMetadata();
            DisposeLibraryGuiTextures();
            try { _harmony?.UnpatchSelf(); } catch { }
            try { SendHelper("CLOSE"); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { if (_browser != null && !_browser.HasExited) _browser.Kill(); } catch { }
            if (_videoTexture != null) Destroy(_videoTexture);
            DestroyGuiTexture(_windowTexture);
            DestroyGuiTexture(_fieldTexture);
            DestroyGuiTexture(_buttonTexture);
            DestroyGuiTexture(_buttonHoverTexture);
            DestroyGuiTexture(_accentTexture);
            DestroyGuiTexture(_accentHoverTexture);
            DestroyGuiTexture(_badgeTexture);
            DestroyGuiTexture(_statusTexture);
            DestroyGuiTexture(_meterBackgroundTexture);
        }

        private static void DestroyGuiTexture(Texture2D texture)
        {
            if (texture != null) Destroy(texture);
        }

        private static void AfterBoardUpdate(QuadPainterGPU __instance)
        {
            SchoolScreenPlugin plugin = _instance;
            if (plugin == null || plugin._schoolRenderer == null || __instance == null || __instance.gameObject != plugin._schoolRenderer.gameObject) return;
            plugin.ApplyTexture();
        }

    }
}
