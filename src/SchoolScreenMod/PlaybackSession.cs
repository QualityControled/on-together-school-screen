using System;

namespace OnTogetherSchoolScreen
{
    // Local browser loads have their own identity, including consecutive copies of the same video.
    internal sealed class PlaybackSession
    {
        public int LoadId { get; private set; }
        public string VideoId { get; private set; } = "";
        public bool HasEnded { get; private set; }
        private bool _hasPlayed;

        public int Begin(string videoId)
        {
            LoadId = LoadId == int.MaxValue ? 1 : LoadId + 1;
            VideoId = videoId ?? "";
            _hasPlayed = false;
            HasEnded = false;
            return LoadId;
        }

        public void Clear() => Begin("");

        public void Resume()
        {
            if (!HasEnded) return;
            _hasPlayed = false;
            HasEnded = false;
        }

        public bool Observe(string videoId, int loadId, int state)
        {
            if (string.IsNullOrEmpty(VideoId) || loadId != LoadId
                || !string.Equals(videoId, VideoId, StringComparison.Ordinal)) return false;
            if (state == 1)
            {
                _hasPlayed = true;
                HasEnded = false;
            }
            if (state == 0 && _hasPlayed) HasEnded = true;
            return true;
        }

        public bool ShouldAdvance(bool controlsPlayback, bool startRequested, bool hasQueuedVideos)
        {
            if (!controlsPlayback) return false;
            return string.IsNullOrEmpty(VideoId) ? startRequested && hasQueuedVideos : HasEnded;
        }
    }
}
