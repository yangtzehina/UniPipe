using System.Text.Json;
using UniCli.Protocol;

namespace UniCli.Client.Tests;

/// <summary>
/// Routing rules for picking which editor a command reaches.
///
/// The rule these mostly defend is that ambiguity is refused rather than guessed: silently
/// choosing one of two editors named MyGame would send writes into a project the caller never
/// named, and nothing downstream would notice.
/// </summary>
public class ProjectResolverMatchTests
{
    private static InstanceEntry Entry(
        string name, string path, InstanceState state = InstanceState.Ready)
    {
        return new InstanceEntry(
            new InstanceRecord
            {
                projectName = name,
                projectPath = path,
                pipeName = "unicli-" + name.ToLowerInvariant(),
                unityVersion = "2022.3.62f3",
                pid = 1234
            },
            state);
    }

    [Fact]
    public void NoSelector_WithASingleEditor_PicksIt()
    {
        var result = ProjectResolver.Match(null, new[] { Entry("Game", "/work/Game") });

        Assert.True(result.IsSuccess);
        Assert.Equal("/work/Game", result.SuccessValue);
    }

    [Fact]
    public void NoSelector_WithTwoEditors_RefusesAndNamesBoth()
    {
        var result = ProjectResolver.Match(
            null, new[] { Entry("Game", "/work/Game"), Entry("Tools", "/work/Tools") });

        Assert.True(result.IsError);
        Assert.Contains("/work/Game", result.ErrorValue);
        Assert.Contains("/work/Tools", result.ErrorValue);
    }

    [Fact]
    public void NoSelector_WithNoEditors_SaysHowToNameOne()
    {
        var result = ProjectResolver.Match(null, System.Array.Empty<InstanceEntry>());

        Assert.True(result.IsError);
        Assert.Contains("UNICLI_PROJECT", result.ErrorValue);
    }

    [Fact]
    public void SelectorMatchesByName_IgnoringCase()
    {
        var result = ProjectResolver.Match(
            "game", new[] { Entry("Game", "/work/Game"), Entry("Tools", "/work/Tools") });

        Assert.True(result.IsSuccess);
        Assert.Equal("/work/Game", result.SuccessValue);
    }

    [Fact]
    public void SameNameInTwoPlaces_IsRefusedWithBothPaths()
    {
        var result = ProjectResolver.Match(
            "Game", new[] { Entry("Game", "/work/a/Game"), Entry("Game", "/work/b/Game") });

        Assert.True(result.IsError);
        Assert.Contains("/work/a/Game", result.ErrorValue);
        Assert.Contains("/work/b/Game", result.ErrorValue);
    }

    [Fact]
    public void AParentSegment_DisambiguatesSameNamedProjects()
    {
        var result = ProjectResolver.Match(
            "b/Game", new[] { Entry("Game", "/work/a/Game"), Entry("Game", "/work/b/Game") });

        Assert.True(result.IsSuccess);
        Assert.Equal("/work/b/Game", result.SuccessValue);
    }

    [Fact]
    public void APartialSegmentIsNotAMatch()
    {
        // "ame" must not match "Game": suffix matching is by path segment, or a caller could
        // land on a project whose name merely ends the same way.
        var result = ProjectResolver.Match("ame", new[] { Entry("Game", "/work/Game") });

        Assert.True(result.IsError);
    }

    [Fact]
    public void TheFullPathMatches()
    {
        var result = ProjectResolver.Match("/work/Game", new[] { Entry("Game", "/work/Game") });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void StaleRecordsAreNotCandidates()
    {
        var result = ProjectResolver.Match(
            null,
            new[]
            {
                Entry("Gone", "/work/Gone", InstanceState.Stale),
                Entry("Game", "/work/Game")
            });

        Assert.True(result.IsSuccess);
        Assert.Equal("/work/Game", result.SuccessValue);
    }

    [Fact]
    public void AReloadingEditorIsStillTheRightTarget()
    {
        // Reloading is temporary. Refusing to route to it would make every recompile look like
        // the editor had disappeared.
        var result = ProjectResolver.Match(
            null, new[] { Entry("Game", "/work/Game", InstanceState.Reloading) });

        Assert.True(result.IsSuccess);
        Assert.Equal("/work/Game", result.SuccessValue);
    }

    [Fact]
    public void AnUnknownName_ListsWhatIsRunning()
    {
        var result = ProjectResolver.Match("Nope", new[] { Entry("Game", "/work/Game") });

        Assert.True(result.IsError);
        Assert.Contains("Game", result.ErrorValue);
    }

    [Fact]
    public void ATrailingSlashDoesNotBreakMatching()
    {
        var result = ProjectResolver.Match("/work/Game/", new[] { Entry("Game", "/work/Game") });

        Assert.True(result.IsSuccess);
    }
}

/// <summary>
/// Reading the registry. Records outlive the editors that wrote them, and a record can be caught
/// mid-write, so the reader has to survive whatever it finds on disk.
/// </summary>
public class InstanceDirectoryReadTests : System.IDisposable
{
    private readonly string _home;
    private readonly string? _previousHome;

    public InstanceDirectoryReadTests()
    {
        _previousHome = System.Environment.GetEnvironmentVariable(InstanceRegistry.HomeVariable);
        _home = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "unicli-tests-" + System.Guid.NewGuid().ToString("n"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(_home, "instances"));
        System.Environment.SetEnvironmentVariable(InstanceRegistry.HomeVariable, _home);
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(InstanceRegistry.HomeVariable, _previousHome);
        try { System.IO.Directory.Delete(_home, recursive: true); }
        catch (System.IO.IOException) { }
    }

    private void WriteRecord(string pipeName, string name, string path)
    {
        var record = new InstanceRecord
        {
            pipeName = pipeName,
            projectName = name,
            projectPath = path,
            unityVersion = "2022.3.62f3",
            pid = 4321,
            startedAt = 1700000000000,
            serverVersion = "0.11.1"
        };

        System.IO.File.WriteAllText(
            InstanceRegistry.GetRecordPath(pipeName),
            JsonSerializer.Serialize(record, ProtocolJsonContext.Default.InstanceRecord));
    }

    [Fact]
    public void HomeVariableRedirectsTheRegistry()
    {
        Assert.StartsWith(_home, InstanceRegistry.GetDirectory());
    }

    [Fact]
    public void RecordsAreReadBack()
    {
        WriteRecord("unicli-aaaaaaaa", "Game", "/work/Game");

        var records = InstanceDirectory.ReadAll();

        Assert.Single(records);
        Assert.Equal("Game", records[0].projectName);
        Assert.Equal("/work/Game", records[0].projectPath);
    }

    [Fact]
    public void MalformedRecordsAreSkipped_NotFatal()
    {
        // A record caught mid-write must not take the whole listing down with it.
        WriteRecord("unicli-aaaaaaaa", "Game", "/work/Game");
        System.IO.File.WriteAllText(
            InstanceRegistry.GetRecordPath("unicli-bbbbbbbb"), "{ this is not json");

        var records = InstanceDirectory.ReadAll();

        Assert.Single(records);
        Assert.Equal("Game", records[0].projectName);
    }

    [Fact]
    public void RecordsMissingTheirIdentityAreSkipped()
    {
        System.IO.File.WriteAllText(
            InstanceRegistry.GetRecordPath("unicli-cccccccc"), "{\"projectName\":\"Half\"}");

        Assert.Empty(InstanceDirectory.ReadAll());
    }

    [Fact]
    public void ListingIsOrderedByName()
    {
        WriteRecord("unicli-aaaaaaaa", "Zebra", "/work/Zebra");
        WriteRecord("unicli-bbbbbbbb", "Apple", "/work/Apple");

        var records = InstanceDirectory.ReadAll();

        Assert.Equal(new[] { "Apple", "Zebra" }, records.Select(r => r.projectName).ToArray());
    }

    [Fact]
    public void AnAbsentRegistryIsEmpty_NotAnError()
    {
        System.IO.Directory.Delete(System.IO.Path.Combine(_home, "instances"), recursive: true);

        Assert.Empty(InstanceDirectory.ReadAll());
    }

    [Fact]
    public void PruningRemovesOnlyStaleRecords()
    {
        WriteRecord("unicli-aaaaaaaa", "Gone", "/work/Gone");
        WriteRecord("unicli-bbbbbbbb", "Live", "/work/Live");

        var records = InstanceDirectory.ReadAll();
        var entries = records.Select(r => new InstanceEntry(
            r,
            r.projectName == "Gone" ? InstanceState.Stale : InstanceState.Reloading)).ToList();

        var pruned = InstanceDirectory.PruneStale(entries);

        Assert.Equal(1, pruned);
        Assert.Single(InstanceDirectory.ReadAll());
        Assert.Equal("Live", InstanceDirectory.ReadAll()[0].projectName);
    }
}
