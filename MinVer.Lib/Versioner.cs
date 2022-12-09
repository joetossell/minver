using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MinVer.Lib;

public static class Versioner
{
    public static Version GetVersion(string workDir, string tagPrefix, MajorMinor minMajorMinor, string buildMeta, VersionPart autoIncrement, IEnumerable<string> defaultPreReleaseIdentifiers, bool ignoreHeight, ILogger log)
    {
        log = log ?? throw new ArgumentNullException(nameof(log));

        var defaultPreReleaseIdentifiersList = defaultPreReleaseIdentifiers.ToList();

        var (version, preReleaseMinMajorMinor, height) = GetVersion(workDir, tagPrefix, defaultPreReleaseIdentifiersList, log);

        var majorMinor = minMajorMinor.Major == preReleaseMinMajorMinor.Major
            ? new MajorMinor(preReleaseMinMajorMinor.Major,
                Math.Max(preReleaseMinMajorMinor.Minor, minMajorMinor.Minor))
            : minMajorMinor.Major > preReleaseMinMajorMinor.Major ? minMajorMinor : preReleaseMinMajorMinor;

        var satisfiedVersion = version.Satisfying(majorMinor, defaultPreReleaseIdentifiersList);

        _ = satisfiedVersion != version
            ? log.IsInfoEnabled && log.Info($"Bumping version to {satisfiedVersion} to satisfy minimum major minor {minMajorMinor}.")
            : log.IsDebugEnabled && log.Debug($"The calculated version {satisfiedVersion} satisfies the minimum major minor {minMajorMinor}.");

        _ = height.HasValue && ignoreHeight && log.IsDebugEnabled && log.Debug("Ignoring height.");
        satisfiedVersion = !height.HasValue || ignoreHeight ? satisfiedVersion : satisfiedVersion.WithHeight(height.Value, autoIncrement, defaultPreReleaseIdentifiersList);

        satisfiedVersion = satisfiedVersion.AddBuildMetadata(buildMeta);

        _ = log.IsInfoEnabled && log.Info($"Calculated version {satisfiedVersion}.");

        return satisfiedVersion;
    }

    private static (Version Version, MajorMinor preRelease, int? Height) GetVersion(string workDir, string tagPrefix, List<string> defaultPreReleaseIdentifiers, ILogger log)
    {
        if (!Git.IsWorkingDirectory(workDir, log))
        {
            var version = new Version(defaultPreReleaseIdentifiers);

            _ = log.IsWarnEnabled && log.Warn(1001, $"'{workDir}' is not a valid Git working directory. Using default version {version}.");

            return (version, MajorMinor.Default, default);
        }

        if (!Git.TryGetHead(workDir, out var head, log))
        {
            var version = new Version(defaultPreReleaseIdentifiers);

            _ = log.IsInfoEnabled && log.Info($"No commits found. Using default version {version}.");

            return (version, MajorMinor.Default, default);
        }

        var tags = Git.GetTags(workDir, log);

        var orderedCandidates = GetCandidates(head, tags, tagPrefix, defaultPreReleaseIdentifiers, log)
            .OrderBy(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Index).ToList();

        var tagWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Tag.Length) : 0;
        var versionWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Version.ToString().Length) : 0;
        var heightWidth = log.IsDebugEnabled ? orderedCandidates.Max(candidate => candidate.Height).ToString(CultureInfo.CurrentCulture).Length : 0;

        if (log.IsDebugEnabled)
        {
            foreach (var candidate in orderedCandidates.Take(orderedCandidates.Count - 1))
            {
                _ = log.Debug($"Ignoring {candidate.ToString(tagWidth, versionWidth, heightWidth)}.");
            }
        }

        var selectedCandidate = orderedCandidates.Last(candidate => !candidate.Version.IsPrerelease);
        var preReleaseVersion = orderedCandidates.Last(candidate => candidate.Version.IsPrerelease).Version;
        var preReleaseMajorMinor = new MajorMinor(preReleaseVersion.Major, preReleaseVersion.Minor);

        _ = string.IsNullOrEmpty(selectedCandidate.Tag) && log.IsInfoEnabled && log.Info($"No commit found with a valid SemVer 2.0 version{(string.IsNullOrEmpty(tagPrefix) ? "" : $" prefixed with '{tagPrefix}'")}. Using default version {selectedCandidate.Version}.");
        _ = log.IsInfoEnabled && log.Info($"Using{(log.IsDebugEnabled && orderedCandidates.Count > 1 ? "    " : " ")}{selectedCandidate.ToString(tagWidth, versionWidth, heightWidth)}.");

        return (selectedCandidate.Version, preReleaseMajorMinor, selectedCandidate.Height);
    }

    private static List<Candidate> GetCandidates(Commit head, IEnumerable<(string Name, string Sha)> tags, string tagPrefix, List<string> defaultPreReleaseIdentifiers, ILogger log)
    {
        var tagsAndVersions = new List<(string Name, string Sha, Version Version)>();

        foreach (var (name, sha) in tags)
        {
            if (Version.TryParse(name, out var version, tagPrefix))
            {
                tagsAndVersions.Add((name, sha, version));
            }
            else
            {
                _ = log.IsDebugEnabled && log.Debug($"Ignoring non-version tag {{ Name: {name}, Sha: {sha} }}.");
            }
        }

        tagsAndVersions = tagsAndVersions
            .OrderBy(tagAndVersion => tagAndVersion.Version)
            .ThenBy(tagsAndVersion => tagsAndVersion.Name)
            .ToList();

        var itemsToCheck = new Stack<(Commit Commit, int Height, Commit? Child)>();
        itemsToCheck.Push((head, 0, null));

        var checkedShas = new HashSet<string>();
        var candidates = new List<Candidate>();

        while (itemsToCheck.TryPop(out var item))
        {
            _ = item.Child != null && log.IsTraceEnabled && log.Trace($"Checking parents of commit {item.Child}...");
            _ = log.IsTraceEnabled && log.Trace($"Checking commit {item.Commit} (height {item.Height})...");

            if (!checkedShas.Add(item.Commit.Sha))
            {
                _ = log.IsTraceEnabled && log.Trace($"Commit {item.Commit} already checked. Abandoning path.");
                continue;
            }

            var commitTagsAndVersions = tagsAndVersions.Where(tagAndVersion => tagAndVersion.Sha == item.Commit.Sha).ToList();

            if (commitTagsAndVersions.Any())
            {
                var hasRtm = false;
                foreach (var (name, _, version) in commitTagsAndVersions)
                {
                    var candidate = new Candidate(item.Commit, item.Height, name, version, candidates.Count);
                    _ = log.IsTraceEnabled && log.Trace($"Found version tag {candidate}.");
                    hasRtm = !version.IsPrerelease;
                    candidates.Add(candidate);
                }

                if (hasRtm)
                {
                    continue;
                }
            }

            _ = log.IsTraceEnabled && log.Trace($"Found no version tags on commit {item.Commit}.");

            if (!item.Commit.Parents.Any())
            {
                candidates.Add(new Candidate(item.Commit, item.Height, "", new Version(defaultPreReleaseIdentifiers), candidates.Count));
                _ = log.IsTraceEnabled && log.Trace($"Found root commit {candidates.Last()}.");
                continue;
            }

            if (log.IsTraceEnabled)
            {
                _ = log.Trace($"Commit {item.Commit} has {item.Commit.Parents.Count} parent(s):");
                foreach (var parent in item.Commit.Parents)
                {
                    _ = log.Trace($"- {parent}");
                }
            }

            foreach (var parent in ((IEnumerable<Commit>)item.Commit.Parents).Reverse())
            {
                itemsToCheck.Push((parent, item.Height + 1, item.Commit));
            }
        }

        _ = log.IsDebugEnabled && log.Debug($"{checkedShas.Count:N0} commits checked.");
        return candidates;
    }

    private sealed class Candidate
    {
        public Candidate(Commit commit, int height, string tag, Version version, int index)
        {
            this.Commit = commit;
            this.Height = height;
            this.Tag = tag;
            this.Version = version;
            this.Index = index;
        }

        public Commit Commit { get; }

        public int Height { get; }

        public string Tag { get; }

        public Version Version { get; }

        public int Index { get; }

        public override string ToString() => this.ToString(0, 0, 0);

        public string ToString(int tagWidth, int versionWidth, int heightWidth) =>
            $"{{ {nameof(this.Commit)}: {this.Commit.ShortSha}, {nameof(this.Tag)}: {$"'{this.Tag}',".PadRight(tagWidth + 3)} {nameof(this.Version)}: {$"{this.Version},".PadRight(versionWidth + 1)} {nameof(this.Height)}: {this.Height.ToString(CultureInfo.CurrentCulture).PadLeft(heightWidth)} }}";
    }
}
