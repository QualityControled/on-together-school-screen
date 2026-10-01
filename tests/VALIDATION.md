# Version 0.1.11 validation

Checked on Windows on 2026-10-01.

The 0.1.11 package was refreshed on the same date with the approved DVD-style idle splash and a further mixed-lobby compatibility review. The version number is unchanged; all modded viewers must replace earlier 0.1.11 downloads with the refreshed package. Mod managers will not detect a new version number for this replacement.

## Passed

- Plugin and browser Release builds: zero warnings and errors.
- Compiled-plugin reporting transport scan: 0.1.11 had no reporting or kick references, patch targets, or old protocol marker. The actual 0.1.10 DLL was rejected with nine findings as a positive control.
- Linked-source packet envelope check: all 16,777,216 possible 24-bit first payload groups round-tripped exactly, remained negative on the wire to avoid vanilla stroke interpolation, and retained legacy positive decoding. Small-erasure flags and invalid encode inputs were checked.
- The earlier 0.1.11 source failed the new outgoing-RPC positive-control check: it contained three direct whiteboard calls, with only one using the safe wrapper. The refreshed source routes all sends through that wrapper.
- Mixed-lobby simulation extracted 55 complete, unchanged plugin methods from the current source and linked the actual envelope/playback-session helpers. All 1,048,575 peer tokens and 1,005 boundary/random video IDs round-tripped. The simulation passed nonhost queueing with a vanilla host, late joins with partial votes, personal vote convergence, idle lower-token joins, automatic advance, coordinator handoff before followers prune, departure-driven vote thresholds, host authority regardless of token order, host snapshots before heartbeats, sender binding, queue blocking, immediate host skip, duplicate/gapped queue entries, and same-code disconnect reset.
- Every simulated outgoing packet used negative previous X and small-erasure flags, with both operation UV coordinates clamping to the top-right corner. Invalid packet fields were discarded before identity binding, and ordinary drawing/other boards passed through the prefix.
- The built DLL had exactly one outgoing RPC call site, inside `SendBoardPayload`, with literal small-erasure arguments. No other outgoing RPC calls were found. Harmony prefix parameter names/types matched the installed game's `QuadPainterGPU.FillTheBlanksRPC_Original_2`, including `RPCInfo rpcInfo`.
- Reviewed the shipped game's whiteboard RPC signature and PurrNet sender forwarding. The prefix receives the existing `RPCInfo rpcInfo` argument and binds protocol tokens to the actual original sender. The shipped Steam transport dispatches RPCs from the Unity update/tick path, allowing the player-name lookup there.
- Inspected the installed Unity assets: `level2` NetworkManager path ID 46893 references `sharedassets2.assets` path ID 4583, named `Unsafe`, whose script is `PurrNet.NetworkRules`. Its serialized RPC rules enable `ignoreRequireServerAttribute`, `ignoreRequireOwnerAttribute`, and `targetRpcsCanTargetServer`. This confirms the installed game's configuration allows guest whiteboard RPCs to relay through a vanilla host. It does not execute a connection or confirm observer membership in a live lobby.
- Reviewed the shipped host report-dialog lifecycle: each **No** closes the current dialog and opens the next pending report. Old clients must stop sending requests before the host can drain the backlog. There is no remote clear operation for an updated joining client.
- Upstream r2modman 3.2.20 installer code, commit `a1897e3d6f7c1946dc51312252db1cc25f1dace0`, using its bundled On-Together `OnTogetherVirtualCoWorking` routing rules and real files in a fresh directory.
- Reproduced both 0.1.9 defects: `www/index.html` installed as `index.html`, and the two native DLL paths collided at one installed filename.
- Uninstalled the old package and installed 0.1.11: `www/index.html` retained its path; the helper and dependencies stayed together; the installed payload matched ZIP file hashes; one native loader was packaged; no copy destinations collided.
- Disabled and enabled the installed package with the same upstream installer.
- Launched the installed helper using the single x64 native loader and a separate WebView2 data folder. Its local page loaded, named-pipe communication worked, and it captured a valid JPEG.
- Loaded video `pqsgpG_OeM8` while muted and received `STATE|0.0|1|This is not a Wii remote...|pqsgpG_OeM8|1` from the installed helper.
- Repeated the installer and installed-helper checks for the refreshed DVD package. Visually inspected its captured idle JPEG with the DVD-style logo behind the readable F9 prompt, then confirmed muted YouTube playback still reported a playing state.
- Opened the animation preview in the in-app browser before implementation; the user approved its appearance.
- Repeated installation, disable/enable, payload hashes, and installed-helper muted playback for the final mixed-lobby refresh. Final ZIP: 324,868 bytes; SHA-256 `312b7f59d9717f0027e5bc1006d919eb1dd072758bc497ea734ee1efbbf25616`. The final source hash matches the source used by the passing 55-method simulation.

## Still untested

- Local ZIP import through Thunderstore Mod Manager or r2modman's actual UI. The desktop-control tool failed to initialize with `failed to write kernel assets: The system cannot find the path specified. (os error 3)`, including retries after reset.
- Launching On-Together through the manager and checking Unity integration, distance-based audio, automatic queue progression, skip votes, and multiplayer synchronization in a live lobby.
- In-game recovery of the existing host popup backlog, absence of new dialogs with an unmodded host, late-join queue state, player names, and host queue blocking. Code, compiled metadata, installed network configuration, and selected method simulations provide evidence; they are not a live-lobby check.
- Idle animation on the in-game whiteboard, reduced-motion behavior, and open/clear transitions in a live game session. The installed helper capture and code review cover the new page; these are not a full Unity check.

## Known transport effect

Players without the mod still handle each whiteboard packet as a small eraser dab. The refreshed outgoing marker and operation fields clamp to the top-right edge, where the native small eraser clears one corner drawing cell. The envelope disables stroke interpolation and ignores the packed color index in vanilla drawing code. Placement and branch behavior were reviewed in the shipped code and checked on emitted packets; Unity's native drawing was not executed. This is not an invisible or entirely side-effect-free transport. All modded viewers should replace their installation with the refreshed 0.1.11 package for the changed encoding and removal of the faulty reporting heartbeat.

The automated installer check exercises production installer functions with filesystem/provider adapters. It is not a claim that the manager UI or the full game was tested. See [instructions](README.md) to reproduce the checks.
