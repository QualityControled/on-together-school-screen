using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OnTogetherSchoolScreen
{
    public sealed partial class SchoolScreenPlugin
    {
        private const int MaximumThumbnailEntries = 100;
        private const int MaximumThumbnailBytes = 256 * 1024;
        private const int MaximumThumbnailDownloads = 2;
        private const int MaximumDurationEntries = 300;
        private const int MaximumVideoDurationSeconds = 7 * 24 * 60 * 60;
        private readonly Dictionary<string, VideoThumbnailEntry> _videoThumbnails = new Dictionary<string, VideoThumbnailEntry>(StringComparer.Ordinal);
        private readonly Queue<string> _thumbnailRequests = new Queue<string>();
        private readonly HashSet<string> _thumbnailRequestsQueued = new HashSet<string>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<VideoThumbnailResult> _thumbnailResults = new ConcurrentQueue<VideoThumbnailResult>();
        private readonly Dictionary<string, int> _videoDurations = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Queue<string> _videoDurationOrder = new Queue<string>();
        private readonly CancellationTokenSource _videoMetadataLifetime = new CancellationTokenSource();
        private HttpClient _videoThumbnailHttpClient;
        private int _activeThumbnailDownloads;
        private long _thumbnailUseSequence;
        private bool _videoMetadataDisposed;

        private sealed class VideoThumbnailEntry
        {
            public string VideoId;
            public Texture2D Texture;
            public bool Fetching;
            public int Attempts;
            public float RetryAt;
            public float LastWantedAt;
            public long LastUse;
        }

        private sealed class VideoThumbnailResult
        {
            public string VideoId;
            public byte[] Bytes;
        }

        private bool HandleVideoMetadataMessage(string message)
        {
            if (message == null || !message.StartsWith("META|", StringComparison.Ordinal)) return false;
            if (_videoMetadataDisposed || message.Length > 256) return true;
            string[] fields = message.Split('|');
            int seconds;
            if (fields.Length != 4 || !IsVideoId(fields[1])
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                || seconds < 0 || seconds > MaximumVideoDurationSeconds) return true;

            string title = fields[2].Trim();
            if (title.Length == 0 || title.Length > 100) return true;
            for (int i = 0; i < title.Length; i++)
                if (char.IsControl(title[i])) return true;

            string videoId = fields[1];
            if (!_videoDurations.ContainsKey(videoId))
            {
                while (_videoDurations.Count >= MaximumDurationEntries && _videoDurationOrder.Count > 0)
                {
                    string evicted = _videoDurationOrder.Dequeue();
                    _videoDurations.Remove(evicted);
                    _queueTitleLookups.Remove(evicted);
                }
                _videoDurationOrder.Enqueue(videoId);
            }
            // Zero is deliberately retained for unknown durations and live videos.
            _videoDurations[videoId] = seconds;
            _queueTitleLookups.Remove(videoId);
            _queueTitleCache[videoId] = title;
            UpdateQueuedVideoTitle(videoId, title);
            if (string.Equals(_videoId, videoId, StringComparison.Ordinal)) _videoTitle = title;
            QueueVideoThumbnail(videoId);
            return true;
        }

        private string GetVideoDurationText(string videoId)
        {
            int seconds;
            if (!IsVideoId(videoId)) return "--";
            if (!_videoDurations.TryGetValue(videoId, out seconds))
            {
                RequestQueueTitle(videoId);
                return "--";
            }
            if (seconds <= 0) return "--";
            if (seconds >= 3600)
                return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + ":"
                    + ((seconds / 60) % 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                    + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":"
                + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private void QueueVideoThumbnail(string videoId)
        {
            if (_videoMetadataDisposed || !IsVideoId(videoId)) return;
            VideoThumbnailEntry existing;
            if (_videoThumbnails.TryGetValue(videoId, out existing))
            {
                existing.LastWantedAt = Time.unscaledTime;
                existing.LastUse = ++_thumbnailUseSequence;
                return;
            }
            if (_thumbnailRequestsQueued.Count < MaximumThumbnailEntries && _thumbnailRequestsQueued.Add(videoId))
                _thumbnailRequests.Enqueue(videoId);
        }

        private void DrawVideoThumbnail(Rect rect, string videoId)
        {
            // GUI only records demand. Network work and texture allocation happen in Update.
            QueueVideoThumbnail(videoId);
            VideoThumbnailEntry entry = null;
            bool loaded = IsVideoId(videoId) && _videoThumbnails.TryGetValue(videoId, out entry) && entry.Texture != null;
            if (loaded)
            {
                GUI.DrawTexture(rect, entry.Texture, ScaleMode.ScaleAndCrop);
            }
            else
            {
                Color oldColor = GUI.color;
                try
                {
                    GUI.color = new Color(.12f, .115f, .20f, 1f);
                    GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill);
                }
                finally { GUI.color = oldColor; }
            }
            // Preserve the passive IMGUI control count when an image arrives asynchronously.
            if (_hintStyle != null) GUI.Label(rect, loaded ? "" : "VIDEO", _hintStyle);
            else GUI.Label(rect, loaded ? "" : "VIDEO");
        }

        private void PumpVideoMetadata()
        {
            if (_videoMetadataDisposed) return;
            float now = Time.unscaledTime;
            VideoThumbnailResult result;
            while (_thumbnailResults.TryDequeue(out result))
            {
                _activeThumbnailDownloads = Math.Max(0, _activeThumbnailDownloads - 1);
                VideoThumbnailEntry entry;
                if (!_videoThumbnails.TryGetValue(result.VideoId, out entry)) continue;
                entry.Fetching = false;
                Texture2D texture = null;
                if (result.Bytes != null)
                {
                    try
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                        {
                            name = "SchoolScreenThumbnail_" + result.VideoId,
                            hideFlags = HideFlags.DontSave,
                            filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Clamp
                        };
                        if (!ImageConversion.LoadImage(texture, result.Bytes, true))
                        {
                            UnityEngine.Object.Destroy(texture);
                            texture = null;
                        }
                    }
                    catch
                    {
                        if (texture != null) UnityEngine.Object.Destroy(texture);
                        texture = null;
                    }
                }
                if (texture != null)
                {
                    if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
                    entry.Texture = texture;
                    entry.Attempts = 0;
                    entry.RetryAt = 0f;
                }
                else
                {
                    // Quiet negative caching prevents a missing image from producing frame-by-frame requests.
                    entry.RetryAt = now + (entry.Attempts >= 3 ? 300f : entry.Attempts == 2 ? 60f : 15f);
                }
            }

            for (int added = 0; added < 8 && _thumbnailRequests.Count > 0; added++)
            {
                string videoId = _thumbnailRequests.Dequeue();
                _thumbnailRequestsQueued.Remove(videoId);
                if (_videoThumbnails.ContainsKey(videoId)) continue;
                if (_videoThumbnails.Count >= MaximumThumbnailEntries && !EvictVideoThumbnail()) continue;
                _videoThumbnails.Add(videoId, new VideoThumbnailEntry
                {
                    VideoId = videoId,
                    LastWantedAt = now,
                    LastUse = ++_thumbnailUseSequence
                });
            }

            if (_activeThumbnailDownloads >= MaximumThumbnailDownloads) return;
            foreach (VideoThumbnailEntry entry in _videoThumbnails.Values)
            {
                if (_activeThumbnailDownloads >= MaximumThumbnailDownloads) break;
                if (entry.Texture != null || entry.Fetching || now < entry.RetryAt || now - entry.LastWantedAt > 3f) continue;
                if (_videoThumbnailHttpClient == null)
                {
                    try
                    {
                        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                        _videoThumbnailHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                    }
                    catch
                    {
                        entry.Attempts = 3;
                        entry.RetryAt = now + 300f;
                        continue;
                    }
                }
                entry.Fetching = true;
                entry.Attempts = Math.Min(3, entry.Attempts + 1);
                _activeThumbnailDownloads++;
                _ = DownloadVideoThumbnailAsync(entry.VideoId, _videoThumbnailHttpClient, _videoMetadataLifetime.Token);
            }
        }

        private bool EvictVideoThumbnail()
        {
            VideoThumbnailEntry oldest = null;
            foreach (VideoThumbnailEntry entry in _videoThumbnails.Values)
                if (!entry.Fetching && (oldest == null || entry.LastUse < oldest.LastUse)) oldest = entry;
            if (oldest == null) return false;
            if (oldest.Texture != null) UnityEngine.Object.Destroy(oldest.Texture);
            _videoThumbnails.Remove(oldest.VideoId);
            return true;
        }

        private async Task DownloadVideoThumbnailAsync(string videoId, HttpClient client, CancellationToken lifetimeToken)
        {
            byte[] bytes = null;
            try
            {
                // No caller-provided URLs, redirects, or video IDs outside the YouTube ID alphabet.
                if (!IsVideoId(videoId)) return;
                var uri = new Uri("https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg", UriKind.Absolute);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(12));
                    using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        long? length = response.Content.Headers.ContentLength;
                        if (length.HasValue && (length.Value <= 0 || length.Value > MaximumThumbnailBytes)) throw new InvalidDataException();
                        string mediaType = response.Content.Headers.ContentType == null ? null : response.Content.Headers.ContentType.MediaType;
                        if (mediaType != null && !string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(mediaType, "image/jpg", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var buffer = new MemoryStream())
                        {
                            var block = new byte[8192];
                            int count;
                            while ((count = await source.ReadAsync(block, 0, block.Length, deadline.Token).ConfigureAwait(false)) > 0)
                            {
                                if (buffer.Length + count > MaximumThumbnailBytes) throw new InvalidDataException();
                                buffer.Write(block, 0, count);
                            }
                            byte[] candidate = buffer.ToArray();
                            if (!HasBoundedThumbnailDimensions(candidate)) throw new InvalidDataException();
                            bytes = candidate;
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (!lifetimeToken.IsCancellationRequested)
                    _thumbnailResults.Enqueue(new VideoThumbnailResult { VideoId = videoId, Bytes = bytes });
            }
        }

        private static bool HasBoundedThumbnailDimensions(byte[] bytes)
        {
            // Validate the JPEG size before handing it to Unity's native decoder.
            if (bytes == null || bytes.Length < 11 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
            int offset = 2;
            while (offset + 3 < bytes.Length)
            {
                if (bytes[offset++] != 0xff) return false;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) return false;
                int marker = bytes[offset++];
                if (marker == 0xd9 || marker == 0xda) return false;
                if (marker == 0xd8 || marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
                if (offset + 1 >= bytes.Length) return false;
                int length = (bytes[offset] << 8) | bytes[offset + 1];
                if (length < 2 || offset + length > bytes.Length) return false;
                if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
                {
                    if (length < 7) return false;
                    int height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                    int width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                    return width > 0 && width <= 640 && height > 0 && height <= 360;
                }
                offset += length;
            }
            return false;
        }

        private void DisposeVideoMetadata()
        {
            if (_videoMetadataDisposed) return;
            _videoMetadataDisposed = true;
            _videoMetadataLifetime.Cancel();
            if (_videoThumbnailHttpClient != null) _videoThumbnailHttpClient.Dispose();
            _videoMetadataLifetime.Dispose();
            foreach (VideoThumbnailEntry entry in _videoThumbnails.Values)
                if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
            _videoThumbnails.Clear();
            _thumbnailRequests.Clear();
            _thumbnailRequestsQueued.Clear();
            _videoDurations.Clear();
            _videoDurationOrder.Clear();
            VideoThumbnailResult result;
            while (_thumbnailResults.TryDequeue(out result)) { }
        }
    }
}
