using System;
using System.Collections.Generic;
using UnityEngine;

namespace OnTogetherSchoolScreen
{
    public sealed partial class SchoolScreenPlugin
    {
        private const float LibraryWindowWidth = 740f;
        private const float LibraryWindowHeight = 620f;
        private const float LibraryFooterY = 534f;
        private readonly List<Texture2D> _libraryUiTextures = new List<Texture2D>();
        private string _libraryHoverTooltip = "";

        private void CreateLibraryGuiStyles()
        {
            _libraryRailTexture = MakeGuiTexture(new Color32(17, 21, 32, 255));
            _libraryUiTextures.Add(_libraryRailTexture);
            _libraryCardTexture = MakeLibraryRoundedTexture(new Color32(38, 43, 59, 255));
            Texture2D selected = MakeLibraryRoundedTexture(new Color32(56, 47, 80, 255));
            Texture2D button = MakeLibraryRoundedTexture(new Color32(39, 44, 60, 255));
            Texture2D hover = MakeLibraryRoundedTexture(new Color32(58, 63, 81, 255));
            Texture2D primary = MakeLibraryRoundedTexture(new Color32(163, 148, 255, 255));
            Texture2D primaryHover = MakeLibraryRoundedTexture(new Color32(183, 171, 255, 255));
            Color ink = new Color32(238, 241, 251, 255);
            Color muted = new Color32(182, 191, 213, 255);
            _primaryButtonStyle = MakeButtonStyle(13, FontStyle.Bold, primary, primaryHover, new Color32(30, 22, 63, 255));
            _controlButtonStyle = MakeButtonStyle(12, FontStyle.Bold, button, hover, ink);
            _seekButtonStyle = MakeButtonStyle(11, FontStyle.Normal, button, hover, muted);
            _primaryButtonStyle.border = _controlButtonStyle.border = _seekButtonStyle.border = new RectOffset(9, 9, 9, 9);
            _libraryNavStyle = MakeButtonStyle(12, FontStyle.Bold, _libraryRailTexture, hover, muted);
            _librarySelectedNavStyle = MakeButtonStyle(12, FontStyle.Bold, selected, selected, new Color32(178, 163, 255, 255));
            _libraryNavStyle.alignment = _librarySelectedNavStyle.alignment = TextAnchor.MiddleLeft;
            _libraryNavStyle.padding = _librarySelectedNavStyle.padding = new RectOffset(13, 8, 4, 4);
            _libraryNavStyle.border = _librarySelectedNavStyle.border = new RectOffset(9, 9, 9, 9);
            _libraryCardStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = _libraryCardTexture },
                border = new RectOffset(9, 9, 9, 9),
                padding = new RectOffset(10, 10, 8, 8)
            };
            _libraryMetaStyle = MakeLabelStyle(11, FontStyle.Normal, muted, TextAnchor.MiddleLeft);
            _libraryMetaStyle.wordWrap = true;
            _libraryRightStyle = MakeLabelStyle(11, FontStyle.Normal, muted, TextAnchor.MiddleRight);
            _libraryHeadingStyle = MakeLabelStyle(23, FontStyle.Bold, ink, TextAnchor.MiddleLeft);
            _libraryCardTitleStyle = MakeLabelStyle(13, FontStyle.Bold, ink, TextAnchor.UpperLeft);
            _libraryCardTitleStyle.wordWrap = true;
            _libraryCardTitleStyle.clipping = TextClipping.Clip;
            _statusStyle.wordWrap = true;
            _hintStyle.fontSize = 11;
        }

        private Texture2D MakeLibraryRoundedTexture(Color32 color, bool tracked = true)
        {
            const int size = 24;
            const float radius = 9f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float alpha = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)));
                    Color32 pixel = color;
                    pixel.a = (byte)Mathf.RoundToInt(color.a * alpha);
                    pixels[y * size + x] = pixel;
                }
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            if (tracked) _libraryUiTextures.Add(texture);
            return texture;
        }

        private void DrawLibraryControlPanel(int windowId)
        {
            // Recompute hover text every GUI pass; Unity's shared GUI.tooltip can retain old text.
            _libraryHoverTooltip = "";
            GUI.DrawTexture(new Rect(0, 0, 150, LibraryFooterY), _libraryRailTexture);
            GUI.DrawTexture(new Rect(150, 0, 1, LibraryFooterY), _badgeTexture);
            GUI.Label(new Rect(18, 22, 116, 53), "Study\nscreen", _titleStyle);
            string[] tabs = { "Now playing", "Lobby queue", "Playlists", "Access" };
            for (int i = 0; i < tabs.Length; i++)
            {
                if (GUI.Button(new Rect(12, 111 + i * 46, 126, 37), tabs[i], _activeTab == i ? _librarySelectedNavStyle : _libraryNavStyle))
                {
                    _activeTab = i;
                    _libraryNotice = "";
                }
            }
            GUI.Box(new Rect(18, 460, 81, 23), _host ? "HOST" : "VIEWER", _host ? _hostBadgeStyle : _viewerBadgeStyle);
            GUI.Label(new Rect(18, 490, 120, 20), "F9  ·  Open / close", _libraryMetaStyle);
            if (GUI.Button(new Rect(701, 12, 24, 26), "X", _closeButtonStyle)) _panelOpen = false;

            Rect area = new Rect(174, 26, 544, 490);
            // Keep footer input IDs stable while asynchronous queue rows and thumbnails change.
            DrawLibraryPlayerFooter();
            if (_activeTab == 0) DrawLibraryNowPlaying(area);
            else if (_activeTab == 1) DrawLibraryQueue(area);
            else if (_activeTab == 2) DrawLibraryPlaylists(area);
            else DrawLibraryAccess(area);
            DrawLibraryTooltip();
            GUI.DragWindow(new Rect(0, 0, 690, 63));
        }

        private void DrawLibraryNowPlaying(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y, area.width - 42, 30), "Now playing", _libraryHeadingStyle);
            bool hasVideo = !string.IsNullOrEmpty(_videoId);
            string title = hasVideo ? (string.IsNullOrWhiteSpace(_videoTitle) ? "Loading video title…" : _videoTitle) : "Choose a video or browse your playlists.";
            DrawLibraryTooltipLabel(new Rect(area.x, area.y + 38, area.width, 39), title, hasVideo ? title : "", _libraryCardTitleStyle);
            Rect preview = new Rect(area.x, area.y + 83, area.width, 230);
            DrawVideoThumbnail(preview, _videoId);
            bool livePreview = _pipeConnected && _videoTexture != null && _videoTexture.width > 2 && Time.unscaledTime - _lastFrameTime < 4f;
            if (livePreview)
                GUI.DrawTexture(preview, _videoTexture, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(preview.x + 20, preview.y + 81, preview.width - 40, 32), !hasVideo && !livePreview ? "School screen" : "", _libraryHeadingStyle);
            GUI.Label(new Rect(preview.x + 20, preview.y + 119, preview.width - 40, 27), !hasVideo && !livePreview ? IsBoardNetworkReady(_schoolBoard) ? "Ready to watch together" : "Connecting to the school whiteboard…" : "", _libraryMetaStyle);
            bool enabled = GUI.enabled;
            bool ready = IsBoardNetworkReady(_schoolBoard);
            GUI.enabled = enabled && ready && hasVideo;
            if (GUI.Button(new Rect(area.x, area.y + 326, 119, 35), _playerState == 2 ? "Play" : "Pause", _primaryButtonStyle))
                Control(_playerState == 2 ? "PLAY" : "PAUSE");
            if (GUI.Button(new Rect(area.x + 126, area.y + 326, 152, 35), _host ? "Skip now" : _localVotedSkip ? "Remove my vote" : "Vote to skip", _controlButtonStyle))
            {
                if (_host) SkipCurrentVideo(); else ToggleSkipVote();
            }
            GUI.enabled = enabled && ready && hasVideo && _host;
            if (GUI.Button(new Rect(area.x + 285, area.y + 326, 80, 35), "Stop", _controlButtonStyle)) Control("CLEAR");
            if (GUI.Button(new Rect(area.x + 372, area.y + 326, 82, 35), "−15 sec", _seekButtonStyle)) Seek(-15f);
            if (GUI.Button(new Rect(area.x + 461, area.y + 326, 83, 35), "+15 sec", _seekButtonStyle)) Seek(15f);
            GUI.enabled = enabled;
            string duration = hasVideo ? GetVideoDurationText(_videoId) : "--";
            string time = hasVideo ? FormatLibraryTime(_videoTime) + (duration == "--" ? "" : " / " + duration) : "";
            GUI.Label(new Rect(area.x, area.y + 367, 165, 20), time, _libraryMetaStyle);
            string vote = !_host && hasVideo ? _voteCount + "/" + _votesRequired + " skip votes · " + _reportedModCount + " modded viewers" : "";
            GUI.Label(new Rect(area.x + 168, area.y + 367, area.width - 168, 20), vote, _libraryRightStyle);
            DrawLibraryAddVideo(new Rect(area.x, area.y + 408, area.width, 36), !hasVideo && _videoQueue.Count == 0);
            bool browserError = _status.StartsWith("WebView2 error", StringComparison.Ordinal) || _status.StartsWith("Could not start", StringComparison.Ordinal);
            string notice = browserError || !_pipeConnected ? _status : !string.IsNullOrWhiteSpace(_queueStatus) ? _queueStatus : _libraryNotice;
            GUI.Label(new Rect(area.x, area.y + 451, area.width, 36), notice, _libraryMetaStyle);
        }

        private void DrawLibraryAddVideo(Rect rect, bool firstVideo = false)
        {
            float buttonWidth = 146f;
            Rect input = new Rect(rect.x, rect.y, rect.width - buttonWidth - 8, rect.height);
            _url = GUI.TextField(input, _url, _inputStyle);
            if (string.IsNullOrEmpty(_url)) GUI.Label(new Rect(input.x + 10, input.y + 7, input.width - 20, input.height - 14), "Paste a YouTube link or video ID", _placeholderStyle);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && IsBoardNetworkReady(_schoolBoard) && _pendingQueueSequence == 0;
            if (GUI.Button(new Rect(rect.xMax - buttonWidth, rect.y, buttonWidth, rect.height), _pendingQueueSequence != 0 ? "ADDING…" : firstVideo ? "PLAY ON THE BOARD" : "ADD TO QUEUE", _primaryButtonStyle)) AddVideoToQueue();
            GUI.enabled = enabled;
        }

        private void DrawLibraryQueue(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y, 345, 30), "Lobby queue", _libraryHeadingStyle);
            DrawSaveQueueShortcut(new Rect(area.xMax - 103, area.y + 1, 103, 31));
            int ownCount = CountPendingQueueEntries(GetQueueOwnerKey(_localPeerToken));
            GUI.Label(new Rect(area.x, area.y + 34, area.width, 20), "Up next · " + _videoQueue.Count + "   |   Your videos · " + ownCount + " / " + _queueLimit, _libraryMetaStyle);
            DrawLibraryAddVideo(new Rect(area.x, area.y + 62, area.width, 35));
            GUI.Label(new Rect(area.x, area.y + 104, area.width, 35), _queueStatus, _libraryMetaStyle);
            Rect view = new Rect(area.x, area.y + 146, area.width, 280);
            float rowHeight = 90f;
            float rowWidth = view.width - 18;
            bool pointerInView = view.Contains(Event.current.mousePosition);
            _queueScroll = GUI.BeginScrollView(view, _queueScroll, new Rect(0, 0, rowWidth, Mathf.Max(view.height, _videoQueue.Count * rowHeight)));
            if (_videoQueue.Count == 0)
                GUI.Label(new Rect(8, 30, rowWidth - 16, 65), "The queue is empty.\nAdd a link or choose a saved video from Playlists.", _libraryMetaStyle);
            for (int i = 0; i < _videoQueue.Count; i++)
            {
                QueueEntry item = _videoQueue[i];
                float y = i * rowHeight;
                GUI.Box(new Rect(0, y, rowWidth, 82), GUIContent.none, _libraryCardStyle);
                GUI.Label(new Rect(8, y + 29, 20, 23), (i + 1).ToString(), _hintStyle);
                DrawVideoThumbnail(new Rect(34, y + 10, 100, 56), item.VideoId);
                string title = !string.IsNullOrWhiteSpace(item.Title) && item.Title != item.VideoId ? item.Title : "Loading video title…";
                float titleWidth = rowWidth - 149 - (_host ? 92 : 10);
                DrawLibraryTooltipLabel(new Rect(147, y + 10, titleWidth, 43), ShortTitle(title, 110), title, _libraryCardTitleStyle, pointerInView);
                string duration = GetVideoDurationText(item.VideoId);
                string attribution = string.IsNullOrWhiteSpace(item.AddedBy) ? "Added by a viewer" : "Added by " + item.AddedBy;
                GUI.Label(new Rect(147, y + 57, titleWidth, 20), duration == "--" ? attribution : duration + " · " + attribution, _libraryMetaStyle);
                if (_host)
                {
                    bool enabled = GUI.enabled;
                    GUI.enabled = enabled && IsBoardNetworkReady(_schoolBoard);
                    if (GUI.Button(new Rect(rowWidth - 83, y + 9, 74, 28), "Play now", _controlButtonStyle)) { GUI.enabled = enabled; PlayQueuedVideo(i); break; }
                    if (GUI.Button(new Rect(rowWidth - 83, y + 44, 74, 28), "Remove", _seekButtonStyle)) { GUI.enabled = enabled; RemoveQueuedVideo(i); break; }
                    GUI.enabled = enabled;
                }
            }
            GUI.EndScrollView();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && IsBoardNetworkReady(_schoolBoard);
            if (!string.IsNullOrEmpty(_videoId))
            {
                if (GUI.Button(new Rect(area.x, area.y + 442, 165, 35), _host ? "SKIP NOW" : _localVotedSkip ? "REMOVE MY VOTE" : "VOTE TO SKIP", _host ? _primaryButtonStyle : _controlButtonStyle))
                {
                    if (_host) SkipCurrentVideo(); else ToggleSkipVote();
                }
                GUI.Label(new Rect(area.x + 180, area.y + 440, area.width - 180, 39), _host ? "Host can skip any time" : _voteCount + "/" + _votesRequired + " votes · " + _reportedModCount + " modded viewers", _libraryRightStyle);
            }
            else if (_videoQueue.Count > 0 && GUI.Button(new Rect(area.x, area.y + 442, 165, 35), "PLAY NEXT", _primaryButtonStyle)) RequestQueueStart();
            GUI.enabled = wasEnabled;
        }

        private void DrawLibraryAccess(Rect area)
        {
            GUI.Label(new Rect(area.x, area.y, area.width - 42, 30), "Access", _libraryHeadingStyle);
            if (!_host)
            {
                GUI.Label(new Rect(area.x, area.y + 48, area.width, 60), "Queue access is managed by the lobby host.\nEveryone can add videos unless the host blocks them.", _libraryMetaStyle);
                GUI.Label(new Rect(area.x, area.y + 116, area.width, 35), "Queue limits · " + _queueLimit + " waiting videos per person, " + _queueCooldown + " seconds between additions", _libraryMetaStyle);
                return;
            }
            GUI.Label(new Rect(area.x, area.y + 39, area.width, 28), "Manage who can add videos to this lobby.", _libraryMetaStyle);
            bool changed = false;
            GUI.Label(new Rect(area.x, area.y + 81, 242, 30), "Waiting videos per person", _libraryMetaStyle);
            if (GUI.Button(new Rect(area.x + 340, area.y + 80, 31, 31), "−", _controlButtonStyle)) { _configuredQueueLimit.Value = Math.Max(1, _configuredQueueLimit.Value - 1); changed = true; }
            GUI.Label(new Rect(area.x + 378, area.y + 82, 80, 26), _configuredQueueLimit.Value.ToString(), _hintStyle);
            if (GUI.Button(new Rect(area.x + 470, area.y + 80, 31, 31), "+", _controlButtonStyle)) { _configuredQueueLimit.Value = Math.Min(30, _configuredQueueLimit.Value + 1); changed = true; }
            GUI.Label(new Rect(area.x, area.y + 123, 260, 30), "Seconds between additions", _libraryMetaStyle);
            if (GUI.Button(new Rect(area.x + 340, area.y + 122, 31, 31), "−", _controlButtonStyle)) { _configuredQueueCooldown.Value = Math.Max(0, _configuredQueueCooldown.Value - 5); changed = true; }
            GUI.Label(new Rect(area.x + 378, area.y + 124, 80, 26), _configuredQueueCooldown.Value + " sec", _hintStyle);
            if (GUI.Button(new Rect(area.x + 470, area.y + 122, 31, 31), "+", _controlButtonStyle)) { _configuredQueueCooldown.Value = Math.Min(300, _configuredQueueCooldown.Value + 5); changed = true; }
            if (changed)
            {
                _queueLimit = _configuredQueueLimit.Value;
                _queueCooldown = _configuredQueueCooldown.Value;
                Config.Save();
                BroadcastQueueSnapshot();
            }
            GUI.Label(new Rect(area.x, area.y + 174, area.width, 20), "MODDED VIEWERS", _sectionStyle);
            Rect view = new Rect(area.x, area.y + 205, area.width, 278);
            float rowWidth = view.width - 18;
            var keys = new List<string>(_moddedPlayers.Keys);
            keys.Sort(StringComparer.Ordinal);
            bool pointerInView = view.Contains(Event.current.mousePosition);
            _accessScroll = GUI.BeginScrollView(view, _accessScroll, new Rect(0, 0, rowWidth, Mathf.Max(view.height, keys.Count * 51f)));
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                LobbyMember member = _moddedPlayers[key];
                float y = i * 51f;
                GUI.Box(new Rect(0, y, rowWidth, 43), GUIContent.none, _libraryCardStyle);
                DrawLibraryTooltipLabel(new Rect(12, y + 8, rowWidth - 155, 28), member.Name + (key == HostMemberKey ? " (you)" : ""), member.Name, _statusStyle, pointerInView);
                if (key == HostMemberKey) continue;
                bool blocked = _blockedQueuePlayers.Contains(key);
                if (GUI.Button(new Rect(rowWidth - 130, y + 6, 119, 31), blocked ? "Allow videos" : "Block videos", blocked ? _primaryButtonStyle : _controlButtonStyle))
                {
                    if (blocked) _blockedQueuePlayers.Remove(key); else _blockedQueuePlayers.Add(key);
                }
            }
            GUI.EndScrollView();
        }

        private void DrawLibraryPlayerFooter()
        {
            GUI.DrawTexture(new Rect(0, LibraryFooterY, LibraryWindowWidth, 1), _badgeTexture);
            GUI.DrawTexture(new Rect(1, LibraryFooterY + 1, LibraryWindowWidth - 2, LibraryWindowHeight - LibraryFooterY - 2), _buttonTexture);
            DrawVideoThumbnail(new Rect(18, LibraryFooterY + 20, 75, 44), _videoId);
            GUI.Label(new Rect(105, LibraryFooterY + 13, 275, 16), string.IsNullOrEmpty(_videoId) ? "SCHOOL SCREEN" : "NOW PLAYING", _sectionStyle);
            string title = string.IsNullOrEmpty(_videoId) ? "Ready to watch together" : string.IsNullOrWhiteSpace(_videoTitle) ? "Loading video title…" : _videoTitle;
            DrawLibraryTooltipLabel(new Rect(105, LibraryFooterY + 35, 274, 38), ShortTitle(title, 80), title, _libraryCardTitleStyle);
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && IsBoardNetworkReady(_schoolBoard) && !string.IsNullOrEmpty(_videoId);
            if (GUI.Button(new Rect(390, LibraryFooterY + 27, 61, 33), _playerState == 2 ? "Play" : "Pause", _controlButtonStyle)) Control(_playerState == 2 ? "PLAY" : "PAUSE");
            GUI.enabled = enabled;
            GUI.Label(new Rect(467, LibraryFooterY + 13, 95, 20), "Volume", _libraryMetaStyle);
            GUI.Label(new Rect(569, LibraryFooterY + 13, 61, 20), Mathf.RoundToInt(_maximumVolume) + "%", _libraryRightStyle);
            float volume = GUI.HorizontalSlider(new Rect(467, LibraryFooterY + 43, 163, 18), _maximumVolume, 0f, 200f, _volumeSliderStyle, _volumeThumbStyle);
            if (Mathf.Abs(volume - _maximumVolume) > .1f) { _maximumVolume = volume; UpdateLocalVolume(true); }
            GUI.Label(new Rect(466, LibraryFooterY + 60, 35, 17), "0%", _libraryMetaStyle);
            GUI.Label(new Rect(594, LibraryFooterY + 60, 36, 17), "200%", _libraryRightStyle);
            if (GUI.Button(new Rect(641, LibraryFooterY + 27, 79, 33), _muted ? "Unmute" : "Mute", _controlButtonStyle)) { _muted = !_muted; UpdateLocalVolume(true); }
        }

        private void DrawLibraryTooltipLabel(Rect rect, string text, string tooltip, GUIStyle style, bool pointerInView = true)
        {
            GUI.Label(rect, text, style);
            RegisterLibraryTooltip(rect, tooltip, pointerInView);
        }

        private bool DrawLibraryTooltipButton(Rect rect, string text, string tooltip, GUIStyle style, bool pointerInView = true)
        {
            RegisterLibraryTooltip(rect, tooltip, pointerInView);
            return GUI.Button(rect, text, style);
        }

        private void RegisterLibraryTooltip(Rect rect, string tooltip, bool pointerInView)
        {
            // Scroll-view hit tests must also respect their visible parent viewport.
            if (Event.current.type == EventType.Repaint && pointerInView && rect.Contains(Event.current.mousePosition)
                && !string.IsNullOrWhiteSpace(tooltip))
                _libraryHoverTooltip = tooltip;
        }

        private void DrawLibraryTooltip()
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrWhiteSpace(_libraryHoverTooltip)) return;
            Vector2 pointer = Event.current.mousePosition;
            float width = Mathf.Min(380f, LibraryWindowWidth - 30f);
            var content = new GUIContent(_libraryHoverTooltip);
            float height = Mathf.Clamp(_libraryMetaStyle.CalcHeight(content, width - 22) + 18, 39, 92);
            float x = Mathf.Clamp(pointer.x + 15, 8, LibraryWindowWidth - width - 8);
            float y = Mathf.Clamp(pointer.y + 19, 8, LibraryWindowHeight - height - 8);
            // Draw only during repaint without allocating control IDs for the floating decoration.
            _libraryCardStyle.Draw(new Rect(x, y, width, height), GUIContent.none, false, false, false, false);
            _libraryMetaStyle.Draw(new Rect(x + 11, y + 8, width - 22, height - 16), content, false, false, false, false);
        }

        private static string FormatLibraryTime(float seconds)
        {
            int time = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return time >= 3600 ? time / 3600 + ":" + ((time / 60) % 60).ToString("00") + ":" + (time % 60).ToString("00")
                : time / 60 + ":" + (time % 60).ToString("00");
        }

        private void DisposeLibraryGuiTextures()
        {
            for (int i = 0; i < _libraryUiTextures.Count; i++) DestroyGuiTexture(_libraryUiTextures[i]);
            _libraryUiTextures.Clear();
        }
    }
}
