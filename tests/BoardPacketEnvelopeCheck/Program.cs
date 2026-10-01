using System.Diagnostics;
using OnTogetherSchoolScreen;

try
{
    var elapsed = Stopwatch.StartNew();
    Assert(BoardPacketEnvelope.IsErase, "Packets must use erase mode to ignore their packed color index.");
    Assert(!BoardPacketEnvelope.IsBigErase, "Packets must use the small eraser.");

    for (int value = 0; value <= BoardPacketEnvelope.MaximumFirstGroup; value++)
    {
        float encoded = BoardPacketEnvelope.EncodeFirstGroup(value);
        if (!(encoded < 0f))
            throw new InvalidOperationException($"Payload {value} was not negative.");
        if (encoded > 0f)
            throw new InvalidOperationException($"Payload {value} would activate vanilla brush interpolation.");
        if (BoardPacketEnvelope.DecodeFirstGroup(encoded) != value)
            throw new InvalidOperationException($"Payload {value} did not round-trip exactly.");
        if (BoardPacketEnvelope.DecodeFirstGroup(value) != value)
            throw new InvalidOperationException($"Legacy payload {value} changed during decoding.");
    }

    float[] invalid = [-1f, 0.5f, 16777216f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
    foreach (float value in invalid)
    {
        try
        {
            BoardPacketEnvelope.EncodeFirstGroup(value);
            throw new InvalidOperationException($"Invalid payload {value} was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    Console.WriteLine($"PASS: all {BoardPacketEnvelope.MaximumFirstGroup + 1:N0} payload groups round-trip exactly; legacy decoding, no interpolation, small erase flags, and invalid inputs checked ({elapsed.Elapsed.TotalSeconds:F2}s).");
    Console.WriteLine("LIMITATION: vanilla recipients still erase up to a 2-by-2 corner; this check does not claim an invisible transport.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
