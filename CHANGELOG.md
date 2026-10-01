# Changelog

## 0.1.9

- Automatically advance to the next video when playback finishes, using one playback coordinator.
- Add videos in queue order, including when starting an idle board. Anyone can use Play Next to resume an idle queue.
- Reserve selecting a specific queued video, stopping, and seeking for the game host. Viewers can add videos and vote to skip.
- Ignore relayed command echoes and stale browser end reports so they cannot advance the queue twice.
- Keep automatic playback and skip voting available when the game host does not have the mod.

## 0.1.8

- Let modded players start videos, add to the shared queue, and play queued videos without requiring the game host to have the mod.
- Share queue state and skip votes through whiteboard RPCs, without chat messages.
- Keep host skip, queue removal, and queue blocking controls when the host has the mod installed.
