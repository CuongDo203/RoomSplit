using System.Reflection;

namespace RoomSplit.Api.Common;

/// <summary>
/// Build metadata baked into the assembly. CI passes the commit as <c>-p:SourceRevisionId</c>
/// (Dockerfile <c>GIT_SHA</c>) and the SDK appends it to the informational version (docs/deploy-ecr.md).
/// </summary>
public static class BuildInfo
{
    private static readonly string InformationalVersion =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>"1.0.0+3f2c1a9..." → "1.0.0".</summary>
    public static string Version { get; } = InformationalVersion.Split('+')[0];

    /// <summary>"1.0.0+3f2c1a9..." → "3f2c1a9..."; "unknown" when built without a commit.</summary>
    public static string Commit { get; } =
        InformationalVersion.Split('+', 2) is [_, var sha] && sha.Length > 0 ? sha : "unknown";
}
