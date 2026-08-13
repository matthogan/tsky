using Tsky.Agent.V1;

namespace tsky;

internal static class TapProtocolVersioning
{
    public const uint Major = 1;
    public const uint CurrentMinor = 1;
    public const uint LegacyMinor = 0;
    public const string CurrentText = "1.1";
    public const string GrpcProfile = "tap-grpc";
    public const string GrpcBindingVersion = "1";

    public static ProtocolVersion Current() => new() { Major = Major, Minor = CurrentMinor };

    public static IReadOnlyList<ProtocolVersion> SupportedVersions() =>
    [
        Current(),
        new ProtocolVersion { Major = Major, Minor = LegacyMinor }
    ];

    public static ProtocolVersion? SelectHighestCommon(IEnumerable<ProtocolVersion> proposed)
    {
        foreach (var supported in SupportedVersions())
        {
            if (proposed.Any(candidate => candidate.Major == supported.Major && candidate.Minor == supported.Minor))
            {
                return supported;
            }
        }

        return null;
    }

    public static bool IsCompatibleBinding(ProtocolBinding binding) =>
        string.Equals(binding.Profile, GrpcProfile, StringComparison.OrdinalIgnoreCase)
        && string.Equals(binding.Version, GrpcBindingVersion, StringComparison.Ordinal);

    public static bool Matches(ProtocolVersion? left, ProtocolVersion? right) =>
        left is not null && right is not null && left.Major == right.Major && left.Minor == right.Minor;

    public static bool IsFrameHeaderValid(FrameHeader? header, ProtocolVersion selectedVersion) =>
        header is not null
        && string.Equals(header.Protocol, "tap", StringComparison.Ordinal)
        && Matches(header.Version, selectedVersion);

    public static bool IsTap11OrLater(ProtocolVersion? version) =>
        version is { Major: Major, Minor: >= CurrentMinor };

    public static bool SupportsScheduleV1(ProtocolVersion? version) => IsTap11OrLater(version);

    public static bool SupportsScheduleV1(string? version) =>
        Version.TryParse(version, out var parsed)
        && (uint)parsed.Major == Major
        && (uint)parsed.Minor >= CurrentMinor;

    public static string Format(ProtocolVersion version) => $"{version.Major}.{version.Minor}";
}
