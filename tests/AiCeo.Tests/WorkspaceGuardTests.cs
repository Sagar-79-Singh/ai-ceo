using AiCeo.Tools;
using Xunit;

namespace AiCeo.Tests;

/// <summary>
/// The security guardrail: nothing may escape the selected workspace folder.
/// </summary>
public class WorkspaceGuardTests
{
    [Fact]
    public void Relative_paths_resolve_inside_the_workspace()
    {
        using var workspace = new TempWorkspace();

        var resolved = WorkspaceGuard.Resolve(workspace.Root, "src/Program.cs");

        // Compare through the guard: Windows short (8.3) temp paths differ textually from the
        // long path the guard returns, so a raw StartsWith would be flaky.
        Assert.True(WorkspaceGuard.IsInside(workspace.Root, resolved));
        Assert.Equal("src/Program.cs", WorkspaceGuard.ToRelative(workspace.Root, resolved));
    }

    [Fact]
    public void The_root_itself_is_allowed()
    {
        using var workspace = new TempWorkspace();

        Assert.True(WorkspaceGuard.IsInside(workspace.Root, workspace.Root));
        Assert.Equal(".", WorkspaceGuard.ToRelative(workspace.Root, workspace.Root));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("src/../../escape.txt")]
    [InlineData("./sub/../../escape.txt")]
    public void Parent_traversal_is_rejected(string candidate)
    {
        using var workspace = new TempWorkspace();

        var exception = Assert.Throws<WorkspaceViolationException>(
            () => WorkspaceGuard.Resolve(workspace.Root, candidate));

        Assert.Contains("outside the workspace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_style_traversal_is_rejected()
    {
        // On Unix a backslash is an ordinary filename character, so the traversal only exists on
        // Windows — but the guard must refuse it there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace();

        var exception = Assert.Throws<WorkspaceViolationException>(
            () => WorkspaceGuard.Resolve(workspace.Root, "..\\escape.txt"));

        Assert.Contains("outside the workspace", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absolute_paths_outside_the_workspace_are_rejected()
    {
        using var workspace = new TempWorkspace();

        var outside = Path.Combine(Path.GetTempPath(), "definitely-not-the-workspace", "secret.txt");

        Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(workspace.Root, outside));
    }

    [Fact]
    public void A_sibling_folder_sharing_a_name_prefix_is_rejected()
    {
        using var workspace = new TempWorkspace();

        // "C:\...\abc" must not grant access to "C:\...\abc-evil".
        var sibling = workspace.Root + "-evil";
        Directory.CreateDirectory(sibling);

        try
        {
            Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(workspace.Root, sibling));
            Assert.False(WorkspaceGuard.IsInside(workspace.Root, Path.Combine(sibling, "x.txt")));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Mixed_separators_cannot_smuggle_a_traversal()
    {
        using var workspace = new TempWorkspace();

        var hostile = OperatingSystem.IsWindows()
            ? "sub\\..\\..\\Windows\\System32\\drivers\\etc\\hosts"
            : "sub/../../etc/passwd";

        Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(workspace.Root, hostile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_workspace_means_no_access(string? root)
    {
        var exception = Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(root, "file.txt"));
        Assert.Contains("No workspace folder", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symlink_inside_the_workspace_cannot_point_outside_it()
    {
        using var workspace = new TempWorkspace();
        using var outside = new TempWorkspace();

        var secret = outside.Write("secret.txt", "top secret");
        var linkPath = Path.Combine(workspace.Root, "link.txt");

        try
        {
            File.CreateSymbolicLink(linkPath, secret);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating symlinks can require privileges (Windows without developer mode); skip.
            return;
        }

        Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(workspace.Root, "link.txt"));
    }

    [Fact]
    public void TryResolve_reports_instead_of_throwing()
    {
        using var workspace = new TempWorkspace();

        Assert.True(WorkspaceGuard.TryResolve(workspace.Root, "ok.txt", out var full, out var error));
        Assert.Null(error);
        Assert.True(WorkspaceGuard.IsInside(workspace.Root, full));

        Assert.False(WorkspaceGuard.TryResolve(workspace.Root, "../nope.txt", out _, out var denied));
        Assert.NotNull(denied);
    }

    [Fact]
    public void Null_bytes_are_rejected()
    {
        using var workspace = new TempWorkspace();

        Assert.Throws<WorkspaceViolationException>(() => WorkspaceGuard.Resolve(workspace.Root, "ok\0.txt"));
    }

    [Fact]
    public void Relative_helper_produces_forward_slashes()
    {
        using var workspace = new TempWorkspace();

        Assert.Equal("src/app/Program.cs", WorkspaceGuard.ToRelative(workspace.Root, Path.Combine(workspace.Root, "src", "app", "Program.cs")));
    }
}
