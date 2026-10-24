namespace RoomSplit.Api.Common;

/// <summary>
/// The <c>Cache</c> configuration section. The cache store itself is chosen by <c>ConnectionStrings:Redis</c>.
/// </summary>
public class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>Turns caching off entirely (every read goes to the database).</summary>
    public bool Enabled { get; set; } = true;

    public int MembersTtlSeconds { get; set; } = 60;
}
