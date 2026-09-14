using System;

namespace ImmichReverseGeo.Web.LifecycleTelemetry;

internal static class LifecycleElapsed
{
    internal static long? Timestamp(TimeProvider time)
    {
        try
        {
            return time.GetTimestamp();
        }
        catch
        {
            return null;
        }
    }

    internal static long Milliseconds(TimeProvider time, long? start, long? end)
    {
        if (start is null || end is null)
        {
            return 0;
        }

        try
        {
            long frequency = time.TimestampFrequency;
            if (frequency <= 0)
            {
                return 0;
            }

            // Decimal subtraction avoids overflow even at the signed timestamp
            // extremes. Use the provider's monotonic frequency, never UTC.
            decimal milliseconds = ((decimal)end.Value - start.Value) * 1000 / frequency;
            return milliseconds <= 0 ? 0
                : milliseconds >= long.MaxValue ? long.MaxValue
                : (long)milliseconds;
        }
        catch
        {
            return 0;
        }
    }
}
