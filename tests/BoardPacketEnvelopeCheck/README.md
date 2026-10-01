# Vanilla board envelope check

Run this dependency-free .NET 8 check from the repository root:

```powershell
dotnet run --project tests/BoardPacketEnvelopeCheck --configuration Release
```

It links the production `BoardPacketEnvelope.cs` source and exhaustively checks every unsigned 24-bit payload group. New envelopes keep `prevUV.x` negative to disable vanilla brush interpolation. Small erase mode ensures vanilla recipients ignore the payload's packed color index, avoiding palette indexing errors. Legacy positive payload groups still decode.

Vanilla boards clamp off-board UVs into their grid. These packets therefore still erase up to a 2-by-2 corner of the board on recipients without the mod. This is a bounded drawing side effect, not an invisible transport. The check verifies arithmetic and flags; live Unity/network behavior remains a separate test.
