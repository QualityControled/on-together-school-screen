using System;
using System.Collections.Generic;
using UnityEngine;

namespace OnTogetherSchoolScreen
{
    public sealed partial class SchoolScreenPlugin
    {
        private SavedPlaylistStore _savedPlaylistStore;
        private readonly List<SavedPlaylist> _savedPlaylists = new List<SavedPlaylist>();
        private readonly HashSet<int> _selectedSavedTracks = new HashSet<int>();
        private string _selectedSavedPlaylistName = "";
        private string _savedPlaylistLink = "";
        private string _savedPlaylistMessage = "";
        private string _savedPlaylistErrorDetail = "";
        private bool _savedPlaylistStoreReady;
        private bool _savedPlaylistShowQueueStatus;
        private Vector2 _savedCollectionsScroll;
        private Vector2 _savedTracksScroll;
        private GUIStyle _savedPlaylistCardTitleStyle;
        private GUIStyle _savedPlaylistTrackTitleStyle;
        private GUIStyle _savedPlaylistHintStyle;
        private GUIStyle _savedPlaylistMessageStyle;
        private int _savedPlaylistNamePrompt; // 1: new collection, 2: save the queue.
        private string _savedPlaylistNameDraft = "";
        private List<SavedPlaylistEntry> _savedPlaylistQueueDraft;
        private SavedPlaylist _savedPlaylistDeletePrompt;
        private string _savedPlaylistOverwriteName = "";
        private List<SavedPlaylistEntry> _savedPlaylistOverwriteEntries;
        private SavedPlaylist _savedPlaylistUndo;
        private float _nextSavedPlaylistTitleRefresh;
        private string _submittedSavedPlaylistName = "";
        private List<SavedPlaylistSubmittedTrack> _submittedSavedPlaylistTracks;

        private sealed class SavedPlaylistSubmittedTrack
        {
            public int Index;
            public string VideoId;
        }

        // Called from Awake. Loading local data never accesses GUI.skin or creates GUI styles.
        private void InitializeSavedPlaylists()
        {
            try
            {
                _savedPlaylistStore = new SavedPlaylistStore(BepInEx.Paths.ConfigPath);
                ReloadSavedPlaylists("");
            }
            catch (Exception exception)
            {
                ReportSavedPlaylistError("Saved playlists could not be opened. Your existing file is unchanged.", exception.Message);
            }
        }

        private void DrawLibraryPlaylists(Rect area)
        {
            EnsureSavedPlaylistGuiStyles();
            RefreshSavedPlaylistCachedTitles();
            GUI.BeginGroup(area);
            try
            {
                float width = area.width;
                GUI.Label(new Rect(0, 0, width - 126, 28), "Your playlists", _libraryHeadingStyle ?? _titleStyle);
                SavedPlaylist openPlaylist = FindSavedPlaylist(_selectedSavedPlaylistName);
                string capacity = openPlaylist == null ? _savedPlaylists.Count + " / " + SavedPlaylistStore.MaxPlaylists + " playlists"
                    : openPlaylist.Entries.Count + " / " + SavedPlaylistStore.MaxEntries + " videos";
                GUI.Label(new Rect(0, 31, width, 18), "Saved privately on this computer · " + capacity, _savedPlaylistHintStyle);
                if (GUI.Button(new Rect(width - 110, 1, 110, 29), "RELOAD", _seekButtonStyle))
                {
                    if (ReloadSavedPlaylists(_selectedSavedPlaylistName)) SetSavedPlaylistMessage("Library refreshed.");
                }

                if (_savedPlaylistNamePrompt != 0)
                {
                    DrawSavedPlaylistNamePrompt(width, area.height);
                    return;
                }
                if (_savedPlaylistDeletePrompt != null)
                {
                    DrawSavedPlaylistDeletePrompt(width, area.height);
                    return;
                }
                if (_savedPlaylistOverwriteEntries != null)
                {
                    DrawSavedPlaylistOverwritePrompt(width, area.height);
                    return;
                }

                SavedPlaylist selected = FindSavedPlaylist(_selectedSavedPlaylistName);
                if (selected == null) DrawSavedPlaylistCollections(width, area.height);
                else DrawSavedPlaylistTracks(selected, width, area.height);
                DrawSavedPlaylistFooter(width, area.height);
            }
            finally { GUI.EndGroup(); }
        }

        private void EnsureSavedPlaylistGuiStyles()
        {
            if (_savedPlaylistCardTitleStyle != null) return;
            // This method is reached only from OnGUI.
            _savedPlaylistCardTitleStyle = new GUIStyle(_statusStyle)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                wordWrap = false,
                clipping = TextClipping.Clip
            };
            _savedPlaylistTrackTitleStyle = new GUIStyle(_statusStyle)
            {
                wordWrap = true,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.UpperLeft
            };
            _savedPlaylistHintStyle = new GUIStyle(_hintStyle) { alignment = TextAnchor.MiddleLeft };
            _savedPlaylistMessageStyle = new GUIStyle(_hintStyle)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
                clipping = TextClipping.Clip
            };
        }

        private void DrawSavedPlaylistCollections(float width, float height)
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null;
            if (GUI.Button(new Rect(0, 61, (width - 8) / 2f, 34), "NEW PLAYLIST", _primaryButtonStyle))
            {
                _savedPlaylistNamePrompt = 1;
                _savedPlaylistNameDraft = "";
                ClearSavedPlaylistMessage();
            }
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null && _videoQueue.Count > 0;
            if (GUI.Button(new Rect((width + 8) / 2f, 61, (width - 8) / 2f, 34), "SAVE QUEUE", _controlButtonStyle))
                BeginSaveQueuePlaylistPrompt();
            GUI.enabled = previousEnabled;

            if (_savedPlaylists.Count == 0)
            {
                GUI.DrawTexture(new Rect(0, 110, width, Math.Max(130f, height - 166f)), _fieldTexture);
                GUI.Label(new Rect(20, 145, width - 40, 30), "Build your study collection", _titleStyle);
                GUI.Label(new Rect(20, 186, width - 40, 56), _savedPlaylistStoreReady
                    ? "Create a playlist and add YouTube videos, or save the lobby's current queue."
                    : "Your saved playlists could not be loaded. Reload after checking the BepInEx log.", _savedPlaylistMessageStyle);
                return;
            }

            Rect view = new Rect(0, 107, width, Math.Max(90f, height - 154f));
            int columns = width >= 510f ? 3 : 2;
            float cardWidth = (view.width - 18f - 8f * (columns - 1)) / columns;
            const float cardHeight = 145f;
            int rows = (_savedPlaylists.Count + columns - 1) / columns;
            bool pointerInView = view.Contains(Event.current.mousePosition);
            _savedCollectionsScroll = GUI.BeginScrollView(view, _savedCollectionsScroll,
                new Rect(0, 0, width - 18f, Math.Max(view.height, rows * (cardHeight + 10f))));
            for (int i = 0; i < _savedPlaylists.Count; i++)
            {
                SavedPlaylist playlist = _savedPlaylists[i];
                Rect card = new Rect((i % columns) * (cardWidth + 8f), (i / columns) * (cardHeight + 10f), cardWidth, cardHeight);
                bool open = GUI.Button(card, GUIContent.none, _controlButtonStyle);
                Rect cover = new Rect(card.x + 8, card.y + 8, card.width - 16, 91);
                if (playlist.Entries.Count > 0)
                {
                    DrawVideoThumbnail(cover, playlist.Entries[0].VideoId);
                    RequestQueueTitle(playlist.Entries[0].VideoId);
                }
                else
                {
                    GUI.DrawTexture(cover, _fieldTexture);
                    GUI.Label(new Rect(cover.x + 12, cover.y + 27, cover.width - 24, 32), "A fresh collection", _savedPlaylistHintStyle);
                }
                DrawLibraryTooltipLabel(new Rect(card.x + 10, card.y + 105, card.width - 20, 22),
                    ShortTitle(playlist.Name, Math.Max(12, Mathf.FloorToInt((card.width - 20) / 7f))), playlist.Name, _savedPlaylistCardTitleStyle, pointerInView);
                GUI.Label(new Rect(card.x + 10, card.y + 126, card.width - 20, 15),
                    playlist.Entries.Count + (playlist.Entries.Count == 1 ? " video" : " videos"), _savedPlaylistHintStyle);
                if (open) SelectSavedPlaylist(playlist.Name);
            }
            GUI.EndScrollView();
        }

        private void DrawSavedPlaylistTracks(SavedPlaylist playlist, float width, float height)
        {
            bool previousEnabled = GUI.enabled;
            if (GUI.Button(new Rect(0, 60, 61, 30), "BACK", _seekButtonStyle))
            {
                _selectedSavedPlaylistName = "";
                _selectedSavedTracks.Clear();
                return;
            }
            DrawLibraryTooltipLabel(new Rect(71, 59, width - 159, 31), playlist.Name, playlist.Name, _savedPlaylistCardTitleStyle);
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null;
            if (GUI.Button(new Rect(width - 80, 60, 80, 30), "DELETE", _seekButtonStyle))
            {
                _savedPlaylistDeletePrompt = playlist;
                ClearSavedPlaylistMessage();
            }
            GUI.enabled = previousEnabled && _selectedSavedTracks.Count > 0 && _pendingQueueSequence == 0 && IsBoardNetworkReady(_schoolBoard);
            if (GUI.Button(new Rect(0, 100, width - 174, 34), _pendingQueueSequence != 0
                ? "ADDING…" : "QUEUE SELECTED (" + _selectedSavedTracks.Count + ")", _primaryButtonStyle))
            {
                var indexes = new List<int>();
                for (int i = 0; i < playlist.Entries.Count; i++)
                    if (_selectedSavedTracks.Contains(i)) indexes.Add(i);
                QueueSavedPlaylistIndexes(playlist, indexes);
            }
            GUI.enabled = previousEnabled;
            if (GUI.Button(new Rect(width - 166, 100, 91, 34), "SELECT ALL", _seekButtonStyle)) SelectAllSavedTracks(playlist);
            if (GUI.Button(new Rect(width - 67, 100, 67, 34), "CLEAR", _seekButtonStyle)) _selectedSavedTracks.Clear();

            Rect input = new Rect(0, 145, width - 102, 33);
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null;
            _savedPlaylistLink = GUI.TextField(input, _savedPlaylistLink, 2048, _inputStyle);
            if (string.IsNullOrEmpty(_savedPlaylistLink))
                GUI.Label(new Rect(input.x + 10, input.y + 7, input.width - 20, 20), "Paste a YouTube link to save here", _placeholderStyle);
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null && playlist.Entries.Count < SavedPlaylistStore.MaxEntries;
            if (GUI.Button(new Rect(width - 94, 145, 94, 33), "SAVE VIDEO", _controlButtonStyle)) AddLinkToSavedPlaylist(playlist);
            GUI.enabled = previousEnabled;

            Rect view = new Rect(0, 188, width, Math.Max(80f, height - 234f));
            float contentWidth = view.width - 18f;
            const float rowHeight = 65f;
            bool pointerInView = view.Contains(Event.current.mousePosition);
            _savedTracksScroll = GUI.BeginScrollView(view, _savedTracksScroll,
                new Rect(0, 0, contentWidth, Math.Max(view.height, playlist.Entries.Count * rowHeight)));
            if (playlist.Entries.Count == 0)
                GUI.Label(new Rect(12, 15, contentWidth - 24, 52), "This playlist is empty. Paste a video link above to get started.", _savedPlaylistMessageStyle);
            for (int i = 0; i < playlist.Entries.Count; i++)
            {
                SavedPlaylistEntry entry = playlist.Entries[i];
                float y = i * rowHeight;
                GUI.DrawTexture(new Rect(0, y, contentWidth, rowHeight - 5), _fieldTexture);
                if (GUI.Button(new Rect(4, y + 19, 23, 23), _selectedSavedTracks.Contains(i) ? "✓" : "", _seekButtonStyle))
                {
                    if (!_selectedSavedTracks.Add(i)) _selectedSavedTracks.Remove(i);
                }
                DrawVideoThumbnail(new Rect(34, y + 7, 80, 45), entry.VideoId);
                string title = GetSavedVideoTitle(entry);
                DrawLibraryTooltipLabel(new Rect(123, y + 10, Math.Max(70f, contentWidth - 243), 40),
                    title, title, _savedPlaylistTrackTitleStyle, pointerInView);
                GUI.enabled = previousEnabled && _pendingQueueSequence == 0 && IsBoardNetworkReady(_schoolBoard);
                if (GUI.Button(new Rect(contentWidth - 112, y + 15, 71, 30), "QUEUE", _controlButtonStyle))
                    QueueSavedPlaylistIndexes(playlist, new[] { i });
                GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null;
                if (DrawLibraryTooltipButton(new Rect(contentWidth - 33, y + 15, 29, 30), "×", "Remove from this playlist", _seekButtonStyle, pointerInView))
                {
                    RemoveSavedPlaylistTrack(playlist, i);
                    break;
                }
                GUI.enabled = previousEnabled;
            }
            GUI.EndScrollView();
            GUI.enabled = previousEnabled;
        }

        private void DrawSavedPlaylistFooter(float width, float height)
        {
            string message = _savedPlaylistShowQueueStatus ? _queueStatus : _savedPlaylistMessage;
            float messageWidth = _savedPlaylistUndo == null ? width : width - 82f;
            // Keep the passive label control present while asynchronous admissions change its text.
            DrawLibraryTooltipLabel(new Rect(0, height - 39, messageWidth, 37),
                message ?? "", _savedPlaylistErrorDetail, _savedPlaylistMessageStyle);
            if (_savedPlaylistUndo != null)
            {
                bool previousEnabled = GUI.enabled;
                GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null;
                if (GUI.Button(new Rect(width - 74, height - 36, 74, 30), "UNDO", _seekButtonStyle)) UndoSavedPlaylistChange();
                GUI.enabled = previousEnabled;
            }
        }

        private void DrawSaveQueueShortcut(Rect buttonRect)
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady && _submittedSavedPlaylistTracks == null && _videoQueue.Count > 0;
            if (GUI.Button(buttonRect, "SAVE QUEUE", _seekButtonStyle)) BeginSaveQueuePlaylistPrompt();
            GUI.enabled = previousEnabled;
        }

        private void BeginSaveQueuePlaylistPrompt()
        {
            if (_videoQueue.Count == 0)
            {
                SetSavedPlaylistMessage("Add videos to the queue before saving it.");
                return;
            }
            _savedPlaylistQueueDraft = new List<SavedPlaylistEntry>();
            for (int i = 0; i < _videoQueue.Count && i < SavedPlaylistStore.MaxEntries; i++)
            {
                QueueEntry item = _videoQueue[i];
                _savedPlaylistQueueDraft.Add(new SavedPlaylistEntry(item.VideoId, GetCachedSavedTitle(item.VideoId, item.Title)));
            }
            _savedPlaylistNameDraft = "";
            _savedPlaylistNamePrompt = 2;
            ClearSavedPlaylistMessage();
            _activeTab = 2;
        }

        private void DrawSavedPlaylistNamePrompt(float width, float height)
        {
            bool saveQueue = _savedPlaylistNamePrompt == 2;
            GUI.DrawTexture(new Rect(0, 70, width, 246), _fieldTexture);
            GUI.DrawTexture(new Rect(0, 70, width, 2), _accentTexture);
            GUI.Label(new Rect(18, 84, width - 36, 30), saveQueue ? "Save your queue" : "Create a playlist", _titleStyle);
            GUI.Label(new Rect(18, 119, width - 36, 35), saveQueue
                ? (_savedPlaylistQueueDraft == null ? 0 : _savedPlaylistQueueDraft.Count) + " queued videos will be saved in order."
                : "Start a collection, then add the videos you want to keep.", _savedPlaylistMessageStyle);
            GUI.Label(new Rect(18, 159, width - 36, 18), "PLAYLIST NAME", _sectionStyle);
            _savedPlaylistNameDraft = GUI.TextField(new Rect(18, 184, width - 36, 36),
                _savedPlaylistNameDraft, SavedPlaylistStore.MaxNameLength, _inputStyle);
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && _savedPlaylistStoreReady;
            if (GUI.Button(new Rect(18, 239, (width - 44) / 2f, 35), saveQueue ? "SAVE PLAYLIST" : "CREATE", _primaryButtonStyle))
                CommitSavedPlaylistNamePrompt();
            GUI.enabled = previousEnabled;
            if (GUI.Button(new Rect((width + 8) / 2f, 239, (width - 44) / 2f, 35), "CANCEL", _controlButtonStyle))
            {
                _savedPlaylistNamePrompt = 0;
                _savedPlaylistQueueDraft = null;
                ClearSavedPlaylistMessage();
            }
            DrawSavedPlaylistFooter(width, height);
        }

        private void CommitSavedPlaylistNamePrompt()
        {
            string name = (_savedPlaylistNameDraft ?? "").Trim();
            if (name.Length == 0)
            {
                SetSavedPlaylistMessage("Give your playlist a name first.");
                return;
            }
            SavedPlaylist existing = FindSavedPlaylist(name);
            if (_savedPlaylistNamePrompt == 1)
            {
                if (existing != null)
                {
                    SetSavedPlaylistMessage("That name is already in your library. Choose a different name.");
                    return;
                }
                if (!SaveLocalPlaylist(name, new List<SavedPlaylistEntry>(), "Playlist created. Paste a video link to add your first track.")) return;
            }
            else
            {
                if (_savedPlaylistQueueDraft == null || _savedPlaylistQueueDraft.Count == 0)
                {
                    SetSavedPlaylistMessage("This queue has no videos to save.");
                    return;
                }
                if (existing != null)
                {
                    _savedPlaylistOverwriteName = name;
                    _savedPlaylistOverwriteEntries = _savedPlaylistQueueDraft;
                    _savedPlaylistNamePrompt = 0;
                    ClearSavedPlaylistMessage();
                    return;
                }
                if (!SaveLocalPlaylist(name, _savedPlaylistQueueDraft, "Queue saved as a playlist.")) return;
            }
            _savedPlaylistNamePrompt = 0;
            _savedPlaylistQueueDraft = null;
        }

        private void DrawSavedPlaylistDeletePrompt(float width, float height)
        {
            GUI.DrawTexture(new Rect(0, 78, width, 227), _fieldTexture);
            GUI.Label(new Rect(18, 94, width - 36, 32), "Delete this playlist?", _titleStyle);
            GUI.Label(new Rect(18, 139, width - 36, 60), "“" + _savedPlaylistDeletePrompt.Name + "” will be removed from your library. You can undo this after deleting.", _savedPlaylistMessageStyle);
            if (GUI.Button(new Rect(18, 232, (width - 44) / 2f, 35), "DELETE PLAYLIST", _primaryButtonStyle))
            {
                SavedPlaylist deleted = _savedPlaylistDeletePrompt;
                string error;
                if (_savedPlaylistStore.TryDelete(deleted.Name, out error))
                {
                    _savedPlaylistDeletePrompt = null;
                    _savedPlaylistUndo = deleted;
                    if (ReloadSavedPlaylists("")) SetSavedPlaylistMessage("Playlist deleted. Use Undo to restore it.");
                }
                else ReportSavedPlaylistError("The playlist could not be deleted. Your library is unchanged.", error);
            }
            if (GUI.Button(new Rect((width + 8) / 2f, 232, (width - 44) / 2f, 35), "CANCEL", _controlButtonStyle))
            {
                _savedPlaylistDeletePrompt = null;
                ClearSavedPlaylistMessage();
            }
            DrawSavedPlaylistFooter(width, height);
        }

        private void DrawSavedPlaylistOverwritePrompt(float width, float height)
        {
            GUI.DrawTexture(new Rect(0, 78, width, 227), _fieldTexture);
            GUI.Label(new Rect(18, 94, width - 36, 32), "Replace this playlist?", _titleStyle);
            GUI.Label(new Rect(18, 139, width - 36, 60), "“" + _savedPlaylistOverwriteName + "” already exists. Replace its videos with this saved queue?", _savedPlaylistMessageStyle);
            if (GUI.Button(new Rect(18, 232, (width - 44) / 2f, 35), "REPLACE", _primaryButtonStyle))
            {
                SavedPlaylist previous = FindSavedPlaylist(_savedPlaylistOverwriteName);
                if (SaveLocalPlaylist(_savedPlaylistOverwriteName, _savedPlaylistOverwriteEntries, "Playlist replaced. Use Undo to restore its previous videos."))
                {
                    _savedPlaylistUndo = previous;
                    _savedPlaylistOverwriteEntries = null;
                    _savedPlaylistOverwriteName = "";
                    _savedPlaylistQueueDraft = null;
                }
            }
            if (GUI.Button(new Rect((width + 8) / 2f, 232, (width - 44) / 2f, 35), "CANCEL", _controlButtonStyle))
            {
                _savedPlaylistOverwriteEntries = null;
                _savedPlaylistOverwriteName = "";
                _savedPlaylistQueueDraft = null;
                ClearSavedPlaylistMessage();
            }
            DrawSavedPlaylistFooter(width, height);
        }

        private void AddLinkToSavedPlaylist(SavedPlaylist playlist)
        {
            string id = ParseVideoId(_savedPlaylistLink);
            if (string.IsNullOrEmpty(id))
            {
                SetSavedPlaylistMessage("Paste a valid YouTube link or video ID.");
                return;
            }
            var entries = CopySavedPlaylistEntries(playlist);
            entries.Add(new SavedPlaylistEntry(id, GetCachedSavedTitle(id, "")));
            if (SaveLocalPlaylist(playlist.Name, entries, "Video saved to your playlist."))
            {
                _savedPlaylistLink = "";
                RequestQueueTitle(id);
            }
        }

        private void RemoveSavedPlaylistTrack(SavedPlaylist playlist, int index)
        {
            var entries = CopySavedPlaylistEntries(playlist);
            if (index < 0 || index >= entries.Count) return;
            entries.RemoveAt(index);
            if (SaveLocalPlaylist(playlist.Name, entries, "Video removed. Use Undo to restore it.")) _savedPlaylistUndo = playlist;
        }

        private bool SaveLocalPlaylist(string name, IEnumerable<SavedPlaylistEntry> entries, string successMessage)
        {
            if (_savedPlaylistStore == null) return false;
            string error;
            if (!_savedPlaylistStore.TrySave(name, entries, out error))
            {
                SetSavedPlaylistMessage(error);
                _savedPlaylistErrorDetail = error;
                return false;
            }
            _savedPlaylistUndo = null;
            if (!ReloadSavedPlaylists(name)) return false;
            SetSavedPlaylistMessage(successMessage);
            return true;
        }

        private void UndoSavedPlaylistChange()
        {
            SavedPlaylist undo = _savedPlaylistUndo;
            if (undo == null) return;
            if (SaveLocalPlaylist(undo.Name, undo.Entries, "Playlist restored.")) _savedPlaylistUndo = null;
        }

        private bool ReloadSavedPlaylists(string selectedName)
        {
            if (_savedPlaylistStore == null) return false;
            IReadOnlyList<SavedPlaylist> loaded;
            string error;
            if (!_savedPlaylistStore.TryLoad(out loaded, out error))
            {
                ReportSavedPlaylistError("Saved playlists could not be loaded. Your existing file is unchanged. See the BepInEx log.", error);
                return false;
            }
            _savedPlaylists.Clear();
            for (int i = 0; i < loaded.Count; i++) _savedPlaylists.Add(loaded[i]);
            _savedPlaylistStoreReady = true;
            _savedPlaylistErrorDetail = "";
            _selectedSavedPlaylistName = "";
            _selectedSavedTracks.Clear();
            if (FindSavedPlaylist(selectedName) != null) SelectSavedPlaylist(selectedName);
            else _savedTracksScroll = Vector2.zero;
            return true;
        }

        private SavedPlaylist FindSavedPlaylist(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _savedPlaylists.Count; i++)
                if (string.Equals(_savedPlaylists[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _savedPlaylists[i];
            return null;
        }

        private void SelectSavedPlaylist(string name)
        {
            SavedPlaylist playlist = FindSavedPlaylist(name);
            if (playlist == null) return;
            _selectedSavedPlaylistName = playlist.Name;
            _savedTracksScroll = Vector2.zero;
            _savedPlaylistLink = "";
            SelectAllSavedTracks(playlist);
            for (int i = 0; i < playlist.Entries.Count; i++) RequestQueueTitle(playlist.Entries[i].VideoId);
        }

        private void SelectAllSavedTracks(SavedPlaylist playlist)
        {
            _selectedSavedTracks.Clear();
            for (int i = 0; i < playlist.Entries.Count && i < SavedPlaylistStore.MaxEntries; i++) _selectedSavedTracks.Add(i);
        }

        private List<SavedPlaylistEntry> CopySavedPlaylistEntries(SavedPlaylist playlist)
        {
            var entries = new List<SavedPlaylistEntry>();
            for (int i = 0; i < playlist.Entries.Count; i++)
            {
                SavedPlaylistEntry entry = playlist.Entries[i];
                entries.Add(new SavedPlaylistEntry(entry.VideoId, GetCachedSavedTitle(entry.VideoId, entry.Title)));
            }
            return entries;
        }

        private string GetSavedVideoTitle(SavedPlaylistEntry entry)
        {
            string title = GetCachedSavedTitle(entry.VideoId, entry.Title);
            if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, entry.VideoId, StringComparison.Ordinal)) return title;
            RequestQueueTitle(entry.VideoId);
            return _pipeConnected ? "Loading video title…" : "YouTube video";
        }

        private void RefreshSavedPlaylistCachedTitles()
        {
            if (!_savedPlaylistStoreReady || _savedPlaylistStore == null || Time.unscaledTime < _nextSavedPlaylistTitleRefresh) return;
            _nextSavedPlaylistTitleRefresh = Time.unscaledTime + 5f;
            for (int i = 0; i < _savedPlaylists.Count; i++)
            {
                SavedPlaylist playlist = _savedPlaylists[i];
                bool changed = false;
                var entries = new List<SavedPlaylistEntry>();
                for (int j = 0; j < playlist.Entries.Count; j++)
                {
                    SavedPlaylistEntry entry = playlist.Entries[j];
                    string title = GetCachedSavedTitle(entry.VideoId, entry.Title);
                    changed |= !string.Equals(title, entry.Title, StringComparison.Ordinal);
                    entries.Add(new SavedPlaylistEntry(entry.VideoId, title));
                }
                if (!changed) continue;
                string error;
                if (!_savedPlaylistStore.TrySave(playlist.Name, entries, out error))
                {
                    ReportSavedPlaylistError("Saved video titles could not be updated. Your existing playlists were kept.", error);
                    return;
                }
                _savedPlaylists[i] = new SavedPlaylist(playlist.Name, entries);
            }
        }

        private string GetCachedSavedTitle(string videoId, string fallback)
        {
            string cached;
            string title = _queueTitleCache.TryGetValue(videoId, out cached) && !string.IsNullOrWhiteSpace(cached)
                && !string.Equals(cached, videoId, StringComparison.Ordinal) ? cached : fallback ?? "";
            if (title.Length <= SavedPlaylistStore.MaxTitleLength) return title;
            int length = SavedPlaylistStore.MaxTitleLength;
            if (char.IsHighSurrogate(title[length - 1])) length--;
            return title.Substring(0, length);
        }

        private void QueueSavedPlaylistIndexes(SavedPlaylist playlist, IEnumerable<int> indexes)
        {
            var ids = new List<string>();
            var submitted = new List<SavedPlaylistSubmittedTrack>();
            foreach (int index in indexes)
            {
                if (index < 0 || index >= playlist.Entries.Count || submitted.Count >= SavedPlaylistStore.MaxEntries) continue;
                string id = playlist.Entries[index].VideoId;
                ids.Add(id);
                submitted.Add(new SavedPlaylistSubmittedTrack { Index = index, VideoId = id });
            }
            // A local coordinator can acknowledge synchronously inside QueueSavedVideos.
            _submittedSavedPlaylistName = playlist.Name;
            _submittedSavedPlaylistTracks = submitted;
            if (!QueueSavedVideos(ids)) OnPlaylistQueueAdmission(0);
            _savedPlaylistShowQueueStatus = true;
            _savedPlaylistErrorDetail = "";
        }

        private void OnPlaylistQueueAdmission(int acceptedCount)
        {
            List<SavedPlaylistSubmittedTrack> submitted = _submittedSavedPlaylistTracks;
            string submittedName = _submittedSavedPlaylistName;
            _submittedSavedPlaylistTracks = null;
            _submittedSavedPlaylistName = "";
            if (submitted == null || acceptedCount <= 0 || !string.Equals(submittedName,
                _selectedSavedPlaylistName, StringComparison.OrdinalIgnoreCase)) return;
            SavedPlaylist playlist = FindSavedPlaylist(submittedName);
            if (playlist == null) return;
            for (int i = 0; i < acceptedCount && i < submitted.Count; i++)
            {
                SavedPlaylistSubmittedTrack track = submitted[i];
                if (track.Index < playlist.Entries.Count && string.Equals(playlist.Entries[track.Index].VideoId,
                    track.VideoId, StringComparison.Ordinal)) _selectedSavedTracks.Remove(track.Index);
            }
        }

        private void SetSavedPlaylistMessage(string message)
        {
            _savedPlaylistMessage = message ?? "";
            _savedPlaylistShowQueueStatus = false;
            _savedPlaylistErrorDetail = "";
        }

        private void ClearSavedPlaylistMessage() => SetSavedPlaylistMessage("");

        private void ReportSavedPlaylistError(string message, string detail)
        {
            _savedPlaylistStoreReady = false;
            SetSavedPlaylistMessage(message);
            _savedPlaylistErrorDetail = detail ?? "";
            Logger.LogWarning(detail ?? message);
        }
    }
}
