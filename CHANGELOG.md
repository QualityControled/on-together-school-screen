# Changelog

## 0.1.11

- Remove all player-discovery calls and Harmony hooks involving the game's report-player system, preventing synthetic kick dialogs on hosts without the mod.
- Discover modded players through whiteboard presence packets and use the RPC's original sender identity for player names and host queue permissions.
- Send the host's existing queue to newly discovered peers, retire old tokens from the same player, and remove expired members from the Access tab.
- Encode whiteboard packets as a small eraser dab without stroke interpolation so players without the mod do not interpret packed video data as an invalid paint color. This can clear a small corner of their whiteboard; all viewers should update for the changed encoding.
- Add a compiled-plugin regression check that detects the unsafe transport in 0.1.10 and rejects any recurrence.
- Document how hosts can dismiss an existing backlog without closing their lobby.

## 0.1.10

- Put the mod payload inside `BepInEx/plugins` so mod managers preserve the `www/index.html` path and keep the browser helper beside its dependencies.
- Package one x64 `WebView2Loader.dll` beside the browser executable, removing the duplicate native loader and unused WPF assembly.
- Report a missing player page or failed page navigation instead of silently rendering a blank board.
- Add a reproducible package builder and automated installer and browser startup checks.
- Keep the playback and queue protocol from 0.1.9.

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
