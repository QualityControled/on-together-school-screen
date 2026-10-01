using BepInEx;
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
    [BepInPlugin("codex.ontogether.school-screen", "On-Together School Screen", "0.1.11")]
    public sealed class SchoolScreenPlugin : BaseUnityPlugin
    {
        private const float BoardCommandMarker = 2f;
        private const float LegacyBoardCommandMarker = -2f;
        private const float PeerTokenScale = 1048576f;
        private const int PeerTokenMask = 0xFFFFF;
        private const int QueueEventSequenceMask = 0xFFFFF;
        private const float NearBoardDistance = 3f;
        private const float SilentDistance = 18f;
        private const string HostMemberKey = "__local_host__";
        private static SchoolScreenPlugin _instance;
        private readonly ConcurrentQueue<string> _networkQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _browserQueue = new ConcurrentQueue<string>();
        private readonly Dictionary<string, LobbyMember> _moddedPlayers = new Dictionary<string, LobbyMember>();
        private readonly HashSet<string> _blockedQueuePlayers = new HashSet<string>();
        private readonly HashSet<int> _peerSkipVotes = new HashSet<int>();
        private readonly Dictionary<int, float> _peerLastSeen = new Dictionary<int, float>();
        private readonly Dictionary<int, string> _peerPlayerKeys = new Dictionary<int, string>();
        private readonly HashSet<long> _processedQueueEvents = new HashSet<long>();
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
        }

        private void Awake()
        {
            _instance = this;
            _localPeerToken = CreatePeerToken();
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
            UpdateHostState();
            if (_lobbyStateObserved && !_observedLobbyActive) return;
            FindSchoolBoard();
            PumpQueues();
            UpdateLobbyPresence();
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
            if (_schoolBoard != null && !string.IsNullOrEmpty(_videoId) && IsLocalPlaybackCoordinator() && Time.unscaledTime >= _nextSync)
            {
                _nextSync = Time.unscaledTime + 10.0f;
                SendBoardCommand("SYNC", _videoId, _videoTime, _playerState);
            }
        }

        private void FindSchoolBoard()
        {
            if (_schoolRenderer != null && _schoolRenderer.gameObject != null) return;
            QuadPainterGPU[] boards = Resources.FindObjectsOfTypeAll<QuadPainterGPU>();
            for (int i = 0; i < boards.Length; i++)
            {
                QuadPainterGPU board = boards[i];
                if (board == null || !board.gameObject.scene.IsValid() || !board.gameObject.activeInHierarchy) continue;
                if (!string.Equals(board.gameObject.name, "DrawingBoard", StringComparison.Ordinal)) continue;
                Transform parent = board.transform;
                bool inSchool = false;
                while (parent != null)
                {
                    if (parent.name == "MD_SchoolInterior") { inSchool = true; break; }
                    parent = parent.parent;
                }
                if (!inSchool) continue;
                _schoolBoard = board;
                _schoolRenderer = board.GetComponent<Renderer>();
                if (_schoolRenderer == null) continue;
                _schoolMaterial = _schoolRenderer.material;
                _status = "School whiteboard found. Press F9 to open controls.";
                StartBrowser();
                Logger.LogInfo("Attached to the DrawingBoard inside MD_SchoolInterior.");
                break;
            }
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
            if (!_pipeConnected || !IsVideoId(videoId) || _queueTitleCache.ContainsKey(videoId)) return;
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
            string ignored;
            while (_networkQueue.TryDequeue(out ignored)) { }
            _status = "Lobby changed. Previous video and queue cleared.";
        }

        private void UpdateLobbyPresence()
        {
            if (_schoolBoard == null || Time.unscaledTime < _nextLobbyHeartbeat) return;
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
            if (_schoolBoard == null) return;
            _peerLastSeen[_localPeerToken] = Time.unscaledTime;
            SendPeerPacket(11, "PEER|" + _localPeerToken);
        }

        private void SendPeerPacket(int packetType, string localMessage)
        {
            if (_schoolBoard == null) return;
            var uv = new Vector2(EncodeBoardMarker(_localPeerToken), packetType);
            SendBoardPayload(uv, Vector2.zero, 0);
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
                _guiInitialized = true;
            }
            if (!_windowPositioned)
            {
                _windowRect = new Rect(24, 86, 456, 478);
                _windowPositioned = true;
            }
            _windowRect = GUI.Window(61429, _windowRect, DrawControlPanel, GUIContent.none, _windowStyle);
        }

        private void DrawControlPanel(int windowId)
        {
            const float width = 456;
            GUI.DrawTexture(new Rect(0, 0, width, 3), _accentTexture);
            GUI.Label(new Rect(20, 12, 310, 26), "Study screen", _titleStyle);
            GUI.Label(new Rect(20, 37, 310, 16), "SCHOOL WHITEBOARD  ·  YOUTUBE", _subtitleStyle);
            GUI.Box(new Rect(344, 18, 72, 24), _host ? "HOST" : "VIEWER", _host ? _hostBadgeStyle : _viewerBadgeStyle);
            if (GUI.Button(new Rect(422, 17, 22, 24), "X", _closeButtonStyle)) _panelOpen = false;

            GUI.DrawTexture(new Rect(18, 61, 420, 1), _badgeTexture);
            const float tabGap = 6f;
            float tabWidth = (416f - tabGap * 2f) / 3f;
            if (GUI.Button(new Rect(20, 69, tabWidth, 27), "SCREEN", _activeTab == 0 ? _primaryButtonStyle : _controlButtonStyle)) _activeTab = 0;
            if (GUI.Button(new Rect(20 + tabWidth + tabGap, 69, tabWidth, 27), "QUEUE", _activeTab == 1 ? _primaryButtonStyle : _controlButtonStyle)) _activeTab = 1;
            if (GUI.Button(new Rect(20 + (tabWidth + tabGap) * 2, 69, tabWidth, 27), "ACCESS", _activeTab == 2 ? _primaryButtonStyle : _controlButtonStyle)) _activeTab = 2;

            if (_activeTab == 0) DrawScreenTab();
            else if (_activeTab == 1) DrawQueueTab();
            else DrawAccessTab();
            GUI.DragWindow(new Rect(0, 0, 336, 60));
        }

        private void DrawScreenTab()
        {
            bool isError = _status.StartsWith("WebView2 error", StringComparison.Ordinal) || _status.StartsWith("Could not start", StringComparison.Ordinal);
            string statusText = isError ? _status
                : _schoolRenderer == null ? "Whiteboard not found yet"
                : !_pipeConnected ? "Starting video player…"
                : string.IsNullOrEmpty(_videoId) ? "Ready to play a video"
                : string.IsNullOrWhiteSpace(_videoTitle) ? "Loading video title…"
                : "Now playing · " + ShortTitle(_videoTitle, 45);
            GUI.DrawTexture(new Rect(21, 111, 8, 8), isError ? _statusTexture : (_pipeConnected ? _accentTexture : _buttonHoverTexture));
            GUI.Label(new Rect(37, 102, 400, 25), statusText, _statusStyle);

            GUI.Label(new Rect(20, 132, 420, 18), "YOUTUBE VIDEO", _sectionStyle);
            Rect inputRect = new Rect(20, 153, 416, 36);
            _url = GUI.TextField(inputRect, _url, _inputStyle);
            if (string.IsNullOrEmpty(_url)) GUI.Label(new Rect(inputRect.x + 10, inputRect.y + 8, inputRect.width - 20, 20), "Paste a YouTube link or video ID", _placeholderStyle);

            bool boardHasVideo = !string.IsNullOrEmpty(_videoId) || _videoQueue.Count > 0;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = _schoolBoard != null;
            if (GUI.Button(new Rect(20, 197, 416, 38), boardHasVideo ? "ADD TO QUEUE" : "PLAY ON THE BOARD", _primaryButtonStyle))
            {
                if (boardHasVideo)
                {
                    AddVideoToQueue();
                    _activeTab = 1;
                }
                else ShareVideo();
            }

            GUI.Label(new Rect(20, 243, 416, 16), "PLAYBACK", _sectionStyle);
            GUI.enabled = !string.IsNullOrEmpty(_videoId);
            const float gap = 6;
            float controlWidth = (416 - 2 * gap) / 3;
            if (GUI.Button(new Rect(20, 262, controlWidth, 33), "Play", _controlButtonStyle)) Control("PLAY");
            if (GUI.Button(new Rect(20 + controlWidth + gap, 262, controlWidth, 33), "Pause", _controlButtonStyle)) Control("PAUSE");
            GUI.enabled = _host && !string.IsNullOrEmpty(_videoId);
            if (GUI.Button(new Rect(20 + 2 * (controlWidth + gap), 262, controlWidth, 33), "Stop", _controlButtonStyle)) Control("CLEAR");
            if (GUI.Button(new Rect(20, 302, (416 - gap) / 2, 30), "−15 sec", _seekButtonStyle)) Seek(-15);
            if (GUI.Button(new Rect(20 + (416 + gap) / 2, 302, (416 - gap) / 2, 30), "+15 sec", _seekButtonStyle)) Seek(15);
            GUI.enabled = previousEnabled;

            GUI.DrawTexture(new Rect(18, 343, 420, 1), _badgeTexture);
            GUI.Label(new Rect(20, 350, 300, 18), "VOLUME", _sectionStyle);
            GUI.Label(new Rect(326, 350, 110, 18), Mathf.RoundToInt(_maximumVolume) + "%", _hintStyle);
            float previousVolume = _maximumVolume;
            _maximumVolume = GUI.HorizontalSlider(new Rect(20, 376, 300, 18), _maximumVolume, 0f, 200f, _volumeSliderStyle, _volumeThumbStyle);
            if (Mathf.Abs(previousVolume - _maximumVolume) > 0.1f) UpdateLocalVolume(true);
            GUI.Label(new Rect(0, 392, 60, 15), "0%", _hintStyle);
            GUI.Label(new Rect(140, 392, 60, 15), "100%", _hintStyle);
            GUI.Label(new Rect(280, 392, 60, 15), "200%", _hintStyle);
            GUI.DrawTexture(new Rect(20, 409, 300, 9), _meterBackgroundTexture);
            GUI.DrawTexture(new Rect(20, 409, 300 * (_playerVolume / 100f), 9), _accentTexture);
            GUI.Label(new Rect(20, 421, 300, 17), _playerVolume == 0 ? "Silent" : "Current volume · " + _playerVolume + "%", _hintStyle);
            if (GUI.Button(new Rect(340, 372, 96, 42), _muted ? "UNMUTE" : "MUTE", _controlButtonStyle))
            {
                _muted = !_muted;
                UpdateLocalVolume(true);
            }
        }

        private void DrawQueueTab()
        {
            GUI.Label(new Rect(20, 103, 416, 20), "ADD A VIDEO TO THE QUEUE", _sectionStyle);
            Rect inputRect = new Rect(20, 128, 296, 36);
            _url = GUI.TextField(inputRect, _url, _inputStyle);
            if (string.IsNullOrEmpty(_url)) GUI.Label(new Rect(inputRect.x + 10, inputRect.y + 8, inputRect.width - 20, 20), "Paste a YouTube link or video ID", _placeholderStyle);
            if (GUI.Button(new Rect(324, 128, 112, 36), "ADD VIDEO", _primaryButtonStyle)) AddVideoToQueue();
            GUI.Label(new Rect(20, 169, 416, 18), _queueStatus, _hintStyle);

            GUI.Label(new Rect(20, 196, 210, 18), "UP NEXT  ·  " + _videoQueue.Count, _sectionStyle);
            if (!string.IsNullOrEmpty(_videoId))
            {
                string voteText = _host ? "Host can skip now" : (_voteCount + "/" + _votesRequired + " votes · " + _reportedModCount + " modded players");
                GUI.Label(new Rect(218, 196, 218, 18), voteText, _hintStyle);
                if (_host)
                {
                    if (GUI.Button(new Rect(20, 219, 196, 34), "SKIP NOW", _primaryButtonStyle)) SkipCurrentVideo();
                }
                else
                {
                    string voteLabel = _localVotedSkip ? "REMOVE MY VOTE" : "VOTE TO SKIP";
                    if (GUI.Button(new Rect(20, 219, 196, 34), voteLabel, _controlButtonStyle)) ToggleSkipVote();
                }
            }
            else if (_videoQueue.Count > 0)
            {
                if (GUI.Button(new Rect(20, 219, 196, 34), "PLAY NEXT", _primaryButtonStyle)) RequestQueueStart();
            }
            else GUI.Label(new Rect(20, 219, 416, 34), "Add a video to start watching.", _hintStyle);

            GUI.DrawTexture(new Rect(18, 262, 420, 1), _badgeTexture);
            Rect viewRect = new Rect(20, 271, 416, 178);
            float contentHeight = Mathf.Max(viewRect.height, _videoQueue.Count * 38f);
            _queueScroll = GUI.BeginScrollView(viewRect, _queueScroll, new Rect(0, 0, viewRect.width - 18, contentHeight));
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                QueueEntry item = _videoQueue[i];
                float y = i * 38f;
                string title = string.IsNullOrWhiteSpace(item.Title) ? item.VideoId : item.Title;
                GUI.Label(new Rect(2, y + 6, _host ? 220 : 391, 24), (i + 1) + ".  " + ShortTitle(title, _host ? 26 : 52), _statusStyle);
                if (_host)
                {
                    if (GUI.Button(new Rect(229, y + 2, 88, 30), "PLAY", _controlButtonStyle)) PlayQueuedVideo(i);
                    if (GUI.Button(new Rect(323, y + 2, 68, 30), "REMOVE", _seekButtonStyle)) RemoveQueuedVideo(i);
                }
            }
            GUI.EndScrollView();
            GUI.Label(new Rect(20, 451, 416, 16), _host ? "You can skip any time or play a queued video." : "Videos play in order. Vote to skip the current video.", _hintStyle);
        }

        private void DrawAccessTab()
        {
            if (!_host)
            {
                GUI.Label(new Rect(20, 110, 416, 40), "Queue blocking is available when the game host has the mod installed.", _statusStyle);
                return;
            }

            GUI.Label(new Rect(20, 105, 416, 20), "QUEUE ACCESS", _sectionStyle);
            GUI.Label(new Rect(20, 127, 416, 30), "Block players here if they abuse the queue.", _hintStyle);
            Rect viewRect = new Rect(20, 164, 416, 284);
            float contentHeight = Mathf.Max(viewRect.height, _moddedPlayers.Count * 42f);
            _accessScroll = GUI.BeginScrollView(viewRect, _accessScroll, new Rect(0, 0, viewRect.width - 18, contentHeight));
            var keys = new List<string>(_moddedPlayers.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                LobbyMember member = _moddedPlayers[key];
                float y = i * 42f;
                string name = key == HostMemberKey ? member.Name + " (you)" : member.Name;
                GUI.Label(new Rect(2, y + 7, 258, 25), name, _statusStyle);
                if (key != HostMemberKey)
                {
                    bool blocked = _blockedQueuePlayers.Contains(key);
                    if (GUI.Button(new Rect(267, y + 2, 120, 32), blocked ? "ALLOW QUEUE" : "BLOCK QUEUE", blocked ? _seekButtonStyle : _controlButtonStyle))
                    {
                        if (blocked) _blockedQueuePlayers.Remove(key);
                        else _blockedQueuePlayers.Add(key);
                    }
                }
            }
            GUI.EndScrollView();
        }

        private void CreateGuiStyles()
        {
            _windowTexture = MakeGuiTexture(new Color32(24, 29, 43, 247));
            _fieldTexture = MakeGuiTexture(new Color32(16, 20, 31, 255));
            _buttonTexture = MakeGuiTexture(new Color32(39, 47, 65, 255));
            _buttonHoverTexture = MakeGuiTexture(new Color32(54, 64, 86, 255));
            _accentTexture = MakeGuiTexture(new Color32(133, 119, 255, 255));
            _accentHoverTexture = MakeGuiTexture(new Color32(153, 141, 255, 255));
            _badgeTexture = MakeGuiTexture(new Color32(54, 64, 85, 255));
            _statusTexture = MakeGuiTexture(new Color32(240, 105, 112, 255));
            _meterBackgroundTexture = MakeGuiTexture(new Color32(10, 13, 21, 255));

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(1, 1, 1, 1)
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
            if (_schoolBoard == null)
            {
                _status = "The school whiteboard is not ready yet.";
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
            string id = ParseVideoId(_url);
            if (string.IsNullOrEmpty(id))
            {
                _queueStatus = "Enter a valid YouTube link or video ID.";
                return;
            }

            if (_schoolBoard == null)
            {
                _queueStatus = "The school whiteboard is not ready yet.";
                return;
            }
            int sequence = NextQueueEventSequence();
            int encodedOperation = (sequence << 4) | 10;
            Vector2 uv = new Vector2(EncodeBoardMarker(_localPeerToken), encodedOperation);
            Vector2 previous = new Vector2(PackVideoIdGroup(id, 0, 4), PackVideoIdGroup(id, 4, 4));
            SendBoardPayload(uv, previous, PackVideoIdGroup(id, 8, 3));
            HandleNetworkCommand("QUEUE_ADD|" + _localPeerToken + "|" + sequence + "|" + id);
            _queueStatus = "Added to the queue.";
            _url = "";
        }

        private void ToggleSkipVote()
        {
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
            if ((!_host && !IsLocalPlaybackCoordinator()) || string.IsNullOrEmpty(_videoId)) return;
            AdvanceQueue();
        }

        private bool IsLocalPlaybackCoordinator()
        {
            if (_host) return true;
            if (IsActivePeerToken(_moddedHostPeerToken)) return _moddedHostPeerToken == _localPeerToken;
            int coordinator = IsActivePeerToken(_playbackControllerToken) ? _playbackControllerToken : GetLowestActivePeerToken();
            return coordinator == _localPeerToken;
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
            if (IsActivePeerToken(_playbackControllerToken)) return peerToken == _playbackControllerToken;
            return _playbackControllerToken == 0 || peerToken == GetLowestActivePeerToken();
        }

        private bool CanAcceptPlaybackCommand(int peerToken, bool synchronization = false)
        {
            if (peerToken <= 0 || peerToken > PeerTokenMask) return false;
            if (_host) return peerToken == _localPeerToken;
            if (IsActivePeerToken(_moddedHostPeerToken)) return peerToken == _moddedHostPeerToken;
            if (IsActivePeerToken(_playbackControllerToken)) return peerToken == _playbackControllerToken;
            if (_playbackControllerToken != 0) return peerToken == GetLowestActivePeerToken();
            // A newcomer can discover an existing video even when their token is lower.
            return synchronization || peerToken == GetLowestActivePeerToken();
        }

        private void UpdateQueuePlayback()
        {
            if (_schoolBoard == null) return;
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
            if (!_host || index < 0 || index >= _videoQueue.Count || _schoolBoard == null) return;
            QueueEntry selected = _videoQueue[index];
            _videoQueue.RemoveAt(index);
            BroadcastQueueSnapshot();
            SendBoardCommand("OPEN", selected.VideoId, 0f, 1);
            _activeTab = 0;
        }

        private void RemoveQueuedVideo(int index)
        {
            if (!_host || index < 0 || index >= _videoQueue.Count) return;
            _videoQueue.RemoveAt(index);
            BroadcastQueueSnapshot();
        }

        private void BroadcastQueueSnapshot()
        {
            if (_schoolBoard == null) return;
            SendBoardPayload(new Vector2(EncodeBoardMarker(_localPeerToken), 7f), Vector2.zero, 0);
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                string id = _videoQueue[i].VideoId;
                int first = PackVideoIdGroup(id, 0, 4);
                int second = PackVideoIdGroup(id, 4, 4);
                int third = PackVideoIdGroup(id, 8, 3);
                int encodedOperation = (i << 4) | 8;
                SendBoardPayload(new Vector2(EncodeBoardMarker(_localPeerToken), encodedOperation), new Vector2(first, second), third);
            }
        }

        private void BroadcastVoteStatus(bool includeSnapshot = false, bool transferIdleState = false)
        {
            _reportedModCount = GetActivePeerCount();
            _voteCount = GetActiveVoteCount();
            _votesRequired = GetRequiredVotes();
            if ((IsLocalPlaybackCoordinator() || transferIdleState) && _schoolBoard != null)
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

        private void SendBoardPayload(Vector2 uv, Vector2 prevUV, int colIndex)
        {
            // Clamp to the top-right corner on vanilla boards: just one cell can be erased.
            // A negative previous X prevents interpolation; erase mode ignores the color field.
            if (uv.y == 0f) uv.y = 16f;
            var safePrevUv = new Vector2(BoardPacketEnvelope.EncodeFirstGroup(prevUV.x), prevUV.y);
            try { _schoolBoard.FillTheBlanksRPC(uv, safePrevUv, colIndex, BoardPacketEnvelope.IsErase, BoardPacketEnvelope.IsBigErase); }
            catch (Exception ex) { Logger.LogWarning("Could not send lobby update: " + ex.GetBaseException().Message); }
        }

        private static string ShortTitle(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
            return value.Substring(0, Math.Max(1, maxLength - 1)) + "…";
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
            if (_schoolBoard == null) return;

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

            HandleNetworkCommand(localCommand);
            SendBoardPayload(uv, prevUv, thirdGroup);
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
                    ApplyQueueAdd(peerToken, sequence, fields[3]);
            }
            else if (command == "QUEUE_CLEAR")
            {
                int peerToken;
                if (fields.Length < 2 || !int.TryParse(fields[1], out peerToken)
                    || peerToken == _localPeerToken || !CanAcceptQueueSnapshot(peerToken)) return;
                _videoQueue.Clear();
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
                if (index < _videoQueue.Count) _videoQueue[index] = item;
                else if (index == _videoQueue.Count) _videoQueue.Add(item);
                else return;
                RequestQueueTitle(item.VideoId);
                // A newcomer may become the idle coordinator before the first video starts.
                // Give an existing-video sync time to arrive before advancing this snapshot.
                if (string.IsNullOrEmpty(_videoId)) ScheduleQueueStart();
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
            int previousCoordinator = GetLowestActivePeerToken();
            _peerLastSeen[peerToken] = Time.unscaledTime;
            if (isNewPeer && peerToken != _localPeerToken && IsLocalPlaybackCoordinator()
                && !string.IsNullOrEmpty(_videoId) && !_playbackSession.HasEnded)
                SendBoardCommand("SYNC", _videoId, _videoTime, _playerState);
            bool sendsSnapshot = _host || (!IsActivePeerToken(_moddedHostPeerToken) && (IsActivePeerToken(_playbackControllerToken)
                ? _playbackControllerToken == _localPeerToken : _localPeerToken == previousCoordinator));
            if (isNewPeer && peerToken != _localPeerToken && sendsSnapshot && _videoQueue.Count > 0)
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
            if (peerToken <= 0 || peerToken > PeerTokenMask || sequence <= 0 || sequence > QueueEventSequenceMask || !IsVideoId(videoId)) return;
            long eventKey = ((long)peerToken << 20) | (uint)sequence;
            if (!_processedQueueEvents.Add(eventKey)) return;
            _peerLastSeen[peerToken] = Time.unscaledTime;

            string playerKey;
            if (_host && _peerPlayerKeys.TryGetValue(peerToken, out playerKey) && _blockedQueuePlayers.Contains(playerKey))
            {
                BroadcastQueueSnapshot();
                return;
            }
            if (_videoQueue.Count >= 30)
            {
                if (_host) BroadcastQueueSnapshot();
                return;
            }

            string title;
            _queueTitleCache.TryGetValue(videoId, out title);
            _videoQueue.Add(new QueueEntry { VideoId = videoId, Title = string.IsNullOrWhiteSpace(title) ? videoId : title, AddedBy = "" });
            RequestQueueTitle(videoId);
            ScheduleQueueStart();
            if (_host) BroadcastQueueSnapshot();
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
                if (_playbackControllerToken == member.PeerToken) _playbackControllerToken = peerToken;
            }
            member.PeerToken = peerToken;
            member.Name = GetRemotePlayerName(sender);
            _peerPlayerKeys[peerToken] = playerKey;
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
            if (type == 1 || type == 10) return extra > 0 && colIndex <= 0x3FFFF;
            if (type == 6 || type == 14) return colIndex <= 0x3FFFF;
            if (type == 8) return extra < 30 && colIndex <= 0x3FFFF;
            if (type == 9)
            {
                if (extra == 0) return payload.x >= 1f && payload.y <= payload.x
                    && colIndex == Math.Max(1, (int)Math.Ceiling(payload.x * 0.30));
                if (extra == 1) return emptyGroups && colIndex == 0;
                return (extra == 2 || extra == 3) && payload.x >= 1f && payload.x <= PeerTokenMask
                    && payload.y == 0f && colIndex == 0;
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
                else plugin._networkQueue.Enqueue((subtype == 2 ? "VOTE_ENTRY|" : "PEER_STATE|") + peerToken + "|" + Mathf.RoundToInt(prevUV.x));
                return false;
            }
            if (packetType == 10)
            {
                int sequence = encodedOperation >> 4;
                string id = UnpackVideoId(Mathf.RoundToInt(prevUV.x), Mathf.RoundToInt(prevUV.y), colIndex);
                if (id != null) plugin._networkQueue.Enqueue("QUEUE_ADD|" + peerToken + "|" + sequence + "|" + id);
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
