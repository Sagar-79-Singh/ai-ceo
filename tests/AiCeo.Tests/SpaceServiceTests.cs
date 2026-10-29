using AiCeo.Models;
using AiCeo.Services;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// v2.0.0 persistent workspaces: a space is a named folder plus the conversation held inside it,
/// exactly one space is active, and reopening the app restores both.
/// </summary>
public class SpaceServiceTests
{
    [Fact]
    public async Task Creating_a_space_makes_it_active_and_lists_it()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);

        var created = await spaces.CreateAsync("Python Game", workspace.Root);

        Assert.Equal("Python Game", created.Name);
        Assert.True(created.IsActive);
        Assert.Equal(Path.GetFullPath(workspace.Root), created.FolderPath);

        var list = await spaces.ListAsync();
        Assert.Single(list);
        Assert.Equal(created.Id, (await spaces.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task A_second_space_takes_over_and_switching_back_works()
    {
        using var database = TestDatabase.Create();
        using var first = new TempWorkspace();
        using var second = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);

        var one = await spaces.CreateAsync("One", first.Root);
        var two = await spaces.CreateAsync("Two", second.Root);

        var list = await spaces.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.True(list.Single(space => space.Id == two.Id).IsActive);
        Assert.False(list.Single(space => space.Id == one.Id).IsActive);

        var activated = await spaces.ActivateAsync(one.Id);

        Assert.NotNull(activated);
        Assert.True(activated!.IsActive);
        Assert.Equal(one.Id, (await spaces.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task A_space_needs_a_unique_name_and_an_absolute_existing_folder()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);

        await spaces.CreateAsync("React App", workspace.Root);

        // Same name, different case: still a clash.
        await Assert.ThrowsAsync<ArgumentException>(() => spaces.CreateAsync("react app", workspace.Root));

        // Relative and non-existent folders are refused before anything is stored.
        await Assert.ThrowsAsync<ArgumentException>(() => spaces.CreateAsync("Relative", "some/relative/path"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => spaces.CreateAsync("Missing", Path.Combine(workspace.Root, "nope")));

        Assert.Single(await spaces.ListAsync());
    }

    [Fact]
    public async Task Deleting_the_active_space_promotes_the_most_recent_survivor()
    {
        using var database = TestDatabase.Create();
        using var first = new TempWorkspace();
        using var second = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);

        var one = await spaces.CreateAsync("One", first.Root);
        var two = await spaces.CreateAsync("Two", second.Root);

        Assert.True(await spaces.DeleteAsync(two.Id));

        var active = await spaces.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(one.Id, active!.Id);
        Assert.Single(await spaces.ListAsync());
    }

    [Fact]
    public async Task The_conversation_of_a_space_survives_a_reload()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);
        var space = await spaces.CreateAsync("Notes", workspace.Root);

        await spaces.SaveConversationAsync(
            space.Id,
            lastPrompt: "write the plan",
            finalOutput: "the plan is ready",
            feed: new[]
            {
                new SpaceFeedItem("plan", "Atlas", "[]", null, false, false, "CEO plan",
                    new[] { new SpaceStep(1, "Writer", "Draft it") }),
                new SpaceFeedItem("tool_result", "Forge", "done", "write_file", false, false, "result",
                    Array.Empty<SpaceStep>())
            });

        var snapshot = await spaces.LoadConversationAsync(space.Id);

        Assert.NotNull(snapshot);
        Assert.Equal("write the plan", snapshot!.LastPrompt);
        Assert.Equal("the plan is ready", snapshot.FinalOutput);
        Assert.Equal(2, snapshot.Feed.Count);
        Assert.Equal("plan", snapshot.Feed[0].Kind);
        Assert.Single(snapshot.Feed[0].Steps);
        Assert.Equal("Writer", snapshot.Feed[0].Steps[0].Role);
        Assert.Equal("write_file", snapshot.Feed[1].ToolName);
        Assert.Equal(2, snapshot.Space.FeedCount);

        // Saving again replaces the conversation instead of appending to it.
        await spaces.SaveConversationAsync(space.Id, "second prompt", "second answer",
            new[] { new SpaceFeedItem("note", "Atlas", "only one line", null, false, false, string.Empty,
                Array.Empty<SpaceStep>()) });

        var replaced = await spaces.LoadConversationAsync(space.Id);

        Assert.NotNull(replaced);
        Assert.Single(replaced!.Feed);
        Assert.Equal("second prompt", replaced.LastPrompt);
    }

    [Fact]
    public async Task Deleting_a_space_removes_its_conversation_rows()
    {
        using var database = TestDatabase.Create();
        using var workspace = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);
        var space = await spaces.CreateAsync("Doomed", workspace.Root);

        await spaces.SaveConversationAsync(
            space.Id,
            "prompt",
            "answer",
            new[] { new SpaceFeedItem("note", "Atlas", "hi", null, false, false, string.Empty, Array.Empty<SpaceStep>()) });

        await using (var db = database.NewContext())
        {
            Assert.Equal(1, db.SpaceFeedEntries.Count(entry => entry.SpaceId == space.Id));
        }

        Assert.True(await spaces.DeleteAsync(space.Id));

        await using (var db = database.NewContext())
        {
            Assert.Equal(0, db.SpaceFeedEntries.Count(entry => entry.SpaceId == space.Id));
        }

        Assert.Null(await spaces.LoadConversationAsync(space.Id));
    }

    [Fact]
    public async Task Pointing_a_space_at_a_new_folder_keeps_its_conversation()
    {
        using var database = TestDatabase.Create();
        using var first = new TempWorkspace();
        using var second = new TempWorkspace();

        var spaces = new SpaceService(database.Factory);
        var space = await spaces.CreateAsync("Moving", first.Root);

        await spaces.SaveConversationAsync(space.Id, "prompt", "answer",
            new[] { new SpaceFeedItem("note", "Atlas", "kept", null, false, false, string.Empty, Array.Empty<SpaceStep>()) });

        var moved = await spaces.SetFolderAsync(space.Id, second.Root);

        Assert.NotNull(moved);
        Assert.Equal(Path.GetFullPath(second.Root), moved!.FolderPath);

        var snapshot = await spaces.LoadConversationAsync(space.Id);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Feed);
    }
}
