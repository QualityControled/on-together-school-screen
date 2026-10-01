using System;

namespace OnTogetherSchoolScreen
{
    internal static class BoardPacketEnvelope
    {
        // Vanilla boards clamp out-of-range UVs to a corner. Erasing prevents the packed
        // payload's color index from indexing the palette. SendBoardPayload places the
        // small eraser at the top-right edge, clearing one corner cell on vanilla boards.
        // This transport is not side-effect free.
        internal const bool IsErase = true;
        internal const bool IsBigErase = false;
        internal const int MaximumFirstGroup = 0xFFFFFF;

        internal static float EncodeFirstGroup(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f
                || value > MaximumFirstGroup || value != Math.Truncate(value))
                throw new ArgumentOutOfRangeException(nameof(value), "A board payload group must be an unsigned 24-bit integer.");

            // Vanilla only interpolates a brush stroke when prevUV.x > 0. Keeping this
            // component negative avoids drawing/erasing along the packed payload values.
            return -value - 1f;
        }

        internal static float DecodeFirstGroup(float value)
        {
            // Positive values were sent by previous releases.
            return value < 0f ? -value - 1f : value;
        }
    }
}
