# Mixed lobby source simulation

This check extracts complete selected methods from the **current product source** without editing their bodies, then compiles them with the actual `BoardPacketEnvelope` and `PlaybackSession` files. Test doubles represent the Unity clock/vector/math APIs, the PurrNet sender and relay boundary, roster lookup, browser commands, and board RPC sends. Generated method hashes and the full input hash are saved alongside the extracted source.

Run from PowerShell with Node.js and .NET 8 or newer:

```powershell
& ./tests/MixedLobbyCheck/Run.ps1
```

Optional arguments: `-PluginSource <source-file>` for an older source positive control; `-OutputDir <work-directory>`, `-NodePath <node.exe>`, and `-DotnetPath <dotnet.exe>`. Supply both `-PluginDll <built-plugin.dll>` and `-GameDll <actual-Assembly-CSharp.dll>` to check the compiled transport as well.

Checks include:

- The single safe outgoing board RPC wrapper, every nonzero 20-bit peer token, and 1,005 sample/boundary video IDs.
- Nonhost queueing with an unmodded server, FIFO start/advance, late join playback/queue discovery, and idle coordination when a newcomer has the lowest token.
- Modded-only electorate and 30% rounding, existing partial votes reaching a late joiner, local vote removal converging after an older queued snapshot, and an already-met lower vote threshold after a departure.
- Coordinator departure followed by immediate handoff packets before followers run their own periodic prune.
- Actual sender identity binding, queue blocks, token-rebinding rejection, host authority independent of token order, host skip, and rejected viewer queue-clear/arbitrary playback packets.
- Immediate playback/queue sync from a real modded host before a separate host heartbeat, duplicate/gapped queue entries, and disconnection reset when the textual lobby code remains unchanged.
- Malformed packet rejection before membership/commands mutate, with normal drawing and other boards passing through.

Every emitted packet must use small erase mode, a negative previous X, and UVs beyond the upper/right bounds, avoiding vanilla interpolation and palette lookup while limiting the eraser dab to one corner cell. Delivery failures are recorded independently so the product's ordinary exception handler cannot swallow a failed test assertion.

The previous 0.1.11 source is expected to fail the wrapper check: its queue and presence helpers sent unsafe direct packets outside `SendBoardPayload`.

The optional compiled check reads managed IL/metadata without executing either DLL. It verifies the actual plugin has exactly one outgoing RPC call site, inside `SendBoardPayload`, with literal `true,false` small erase flags and no other outgoing RPC calls. It also compares Harmony prefix argument names and types against `QuadPainterGPU.FillTheBlanksRPC_Original_2` in the supplied game assembly, including `rpcInfo` sender context.

**Limits:** this simulation does not execute Unity native code, PurrNet serialization/relay/observer membership, a real server connection, native drawing, audio, browser rendering, or other mods. Vanilla recipient safety checks use the observed branch predicates from the game's drawing methods. Unmodded recipients still receive a one-cell corner eraser dab. Passing is useful regression evidence and cannot replace a live mixed lobby check.
