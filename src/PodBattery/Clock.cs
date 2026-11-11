namespace PodBattery;

/// <summary>
/// The current time for timing decisions. Advances with the system tick count (which keeps counting through sleep),
/// so changes to the wall clock can't make old readings look fresh or stall a scanner restart.
/// </summary>
internal static class Clock
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
    private static readonly long StartedTicks = Environment.TickCount64;

    public static DateTimeOffset Now => StartedAt.AddMilliseconds(Environment.TickCount64 - StartedTicks);
}
