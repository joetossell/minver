#if NET
using System.Collections.Generic;
using System.Linq;

namespace MinVerTests.Infra
{
    public record Package(string Version, AssemblyVersion AssemblyVersion, FileVersion FileVersion, string InformationalVersion)
    {
        public static Package WithVersion(int major, int minor, int patch, IEnumerable<string>? preReleaseIdentifiers = null, int height = 0, string buildMetadata = "", string? informationalVersionAdditionalBuildMetadata = "")
        {
            var preReleaseToken = preReleaseIdentifiers == null ? "" : GetPreReleaseToken(preReleaseIdentifiers.ToList());
            var heightToken = height == 0 ? "" : $".{height}";
            var buildMetadataToken = string.IsNullOrEmpty(buildMetadata) ? "" : $"+{buildMetadata}";

            var version = $"{major}.{minor}.{patch}{preReleaseToken}{heightToken}";
            var informationalVersion = $"{version}{buildMetadataToken}{informationalVersionAdditionalBuildMetadata}";

            return new Package(version, new AssemblyVersion(major, 0, 0, 0), new FileVersion(major, minor, patch, 0, informationalVersion), informationalVersion);
        }

        private static string GetPreReleaseToken(IReadOnlyList<string> preReleaseIdentifiers) => preReleaseIdentifiers.Any() ? $"-{string.Join(".", preReleaseIdentifiers)}" : "";
    }
}
#endif
