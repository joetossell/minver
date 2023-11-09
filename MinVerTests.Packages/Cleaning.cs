using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.FileSystemGlobbing;
using MinVerTests.Infra;
using Xunit;

namespace MinVerTests.Packages;

public static class Cleaning
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task PackagesAreCleaned(bool multiTarget)
    {
        // arrange
        var path = MethodBase.GetCurrentMethod().GetTestDirectory(multiTarget);
        await Sdk.CreateProject(path, multiTarget: multiTarget);

        await Git.Init(path);
        await Git.Commit(path);
        await Git.Tag(path, "2.3.4");

        _ = await Sdk.BuildProject(path);

        var packages = new Matcher().AddInclude("**/bin/Debug/*.nupkg");
        Assert.NotEmpty(packages.GetResultsInFullPath(path));

        // act
        // -maxCpuCount:1 is required to prevent massive execution times in GitHub Actions
        _ = await Sdk.DotNet("clean -maxCpuCount:1", path, new Dictionary<string, string> { { "GeneratePackageOnBuild", "true" }, });

        // assert
        Assert.Empty(packages.GetResultsInFullPath(path));
    }

    [Fact]
    public static async Task MinVerDoesNotRunWhenPackagesAreNotGeneratedOnBuild()
    {
        // arrange
        var path = MethodBase.GetCurrentMethod().GetTestDirectory();
        await Sdk.CreateProject(path);

        // act
        var (standardOutput, _) = await Sdk.DotNet(
            "clean",
            path,
            new Dictionary<string, string>
            {
                { "GeneratePackageOnBuild", "false" },
                { "MinVerVerbosity", "diagnostic" },
            });

        // assert
        Assert.DoesNotContain("minver:", standardOutput, StringComparison.OrdinalIgnoreCase);
    }
}
