# Version 0.1.11 validation

Checked on Windows on 2026-10-01.

The 0.1.11 package was refreshed on the same date to include the approved DVD-style idle splash. The version number is unchanged; earlier 0.1.11 downloads must be replaced to obtain it.

## Passed

- Plugin and browser Release builds: zero warnings and errors.
- Compiled-plugin reporting transport scan: 0.1.11 had no reporting or kick references, patch targets, or old protocol marker. The actual 0.1.10 DLL was rejected with nine findings as a positive control.
- Linked-source packet envelope check: all 16,777,216 possible 24-bit first payload groups round-tripped exactly, remained negative on the wire to avoid vanilla stroke interpolation, and retained legacy positive decoding. Small-erasure flags and invalid encode inputs were checked.
- Reviewed the shipped game's whiteboard RPC signature and PurrNet sender forwarding. The prefix receives the existing `RPCInfo rpcInfo` argument and binds protocol tokens to the actual original sender. The shipped Steam transport dispatches RPCs from the Unity update/tick path, allowing the player-name lookup there.
- Reviewed the shipped host report-dialog lifecycle: each **No** closes the current dialog and opens the next pending report. Old clients must stop sending requests before the host can drain the backlog. There is no remote clear operation for an updated joining client.
- Upstream r2modman 3.2.20 installer code, commit `a1897e3d6f7c1946dc51312252db1cc25f1dace0`, using its bundled On-Together `OnTogetherVirtualCoWorking` routing rules and real files in a fresh directory.
- Reproduced both 0.1.9 defects: `www/index.html` installed as `index.html`, and the two native DLL paths collided at one installed filename.
- Uninstalled the old package and installed 0.1.11: `www/index.html` retained its path; the helper and dependencies stayed together; the installed payload matched ZIP file hashes; one native loader was packaged; no copy destinations collided.
- Disabled and enabled the installed package with the same upstream installer.
- Launched the installed helper using the single x64 native loader and a separate WebView2 data folder. Its local page loaded, named-pipe communication worked, and it captured a valid JPEG.
- Loaded video `pqsgpG_OeM8` while muted and received `STATE|0.0|1|This is not a Wii remote...|pqsgpG_OeM8|1` from the installed helper.
- Repeated the installer and installed-helper checks for the refreshed DVD package. Visually inspected its captured idle JPEG with the DVD-style logo behind the readable F9 prompt, then confirmed muted YouTube playback still reported a playing state.
- Opened the animation preview in the in-app browser before implementation; the user approved its appearance.

## Still untested

- Local ZIP import through Thunderstore Mod Manager or r2modman's actual UI. The desktop-control tool failed to initialize with `failed to write kernel assets: The system cannot find the path specified. (os error 3)`, including retries after reset.
- Launching On-Together through the manager and checking Unity integration, distance-based audio, automatic queue progression, skip votes, and multiplayer synchronization in a live lobby.
- In-game recovery of the existing host popup backlog, absence of new dialogs with an unmodded host, late-join queue state, player names, and host queue blocking. Their expected behavior was reviewed against code, not exercised in a live lobby.
- Idle animation on the in-game whiteboard, reduced-motion behavior, and open/clear transitions in a live game session. The installed helper capture and code review cover the new page; these are not a full Unity check.

## Known transport effect

Players without the mod still handle the whiteboard packet as a small eraser dab, clearing up to a 2-by-2 corner of the board. The new envelope disables stroke interpolation and ignores the packed color index in vanilla drawing code. This is not an invisible or entirely side-effect-free transport. All modded viewers should update to 0.1.11 for the changed encoding and removal of the faulty reporting heartbeat.

The automated installer check exercises production installer functions with filesystem/provider adapters. It is not a claim that the manager UI or the full game was tested. See [instructions](README.md) to reproduce the checks.
