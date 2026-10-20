namespace RoomSplit.Api.Common;

/// <summary>
/// Business "today" in the app's time zone (<c>App:TimeZone</c>), independent of the server's local zone.
/// </summary>
public interface IAppClock
{
    DateOnly Today { get; }
}

public class AppClock(TimeProvider timeProvider, TimeZoneInfo timeZone) : IAppClock
{
    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone).DateTime);
}
