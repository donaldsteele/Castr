using Castr.Core.Security;

namespace Castr.Core.Tests.Security;

public class PathSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("castr-path-safety-tests-").FullName;

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("subfolder/photo.jpg")]
    [InlineData("a/b/c/deep.jpg")]
    [InlineData("./photo.jpg")]
    [InlineData("weird..but..fine.jpg")] // literal dots that are not a ".." segment
    public void IsSafeRelativePath_AllowsBenignPaths(string path)
    {
        Assert.True(PathSafety.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("../escape.jpg")]
    [InlineData("../../etc/passwd")]
    [InlineData("subfolder/../../escape.jpg")]
    [InlineData("subfolder/../../../etc/passwd")]
    [InlineData("..\\windows\\escape.jpg")]
    [InlineData("a/b/../../../../../../escape.jpg")]
    [InlineData("..")]
    [InlineData("/etc/passwd")] // absolute POSIX path
    [InlineData("\\\\server\\share\\evil")] // UNC path
    [InlineData("C:\\Windows\\System32\\evil.dll")] // absolute Windows path
    [InlineData("C:evil.dll")] // drive-relative Windows path
    [InlineData("photo.jpg:hidden-stream")] // NTFS alternate-data-stream trick
    [InlineData("")]
    [InlineData("   ")]
    public void IsSafeRelativePath_RejectsTraversalAndAbsolutePaths(string maliciousPath)
    {
        Assert.False(PathSafety.IsSafeRelativePath(maliciousPath));
    }

    [Fact]
    public void ResolveDestination_BenignRelativePath_LandsInsideRoot()
    {
        var resolved = PathSafety.ResolveDestination(_root, "photos/beach.jpg");

        Assert.StartsWith(Path.GetFullPath(_root), resolved);
        Assert.EndsWith(Path.Combine("photos", "beach.jpg"), resolved);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("../escape.jpg")]
    [InlineData("subfolder/../../escape.jpg")]
    public void ResolveDestination_TraversalAttempt_ThrowsAndNeverEscapesRoot(string maliciousPath)
    {
        var ex = Assert.Throws<PathTraversalException>(() => PathSafety.ResolveDestination(_root, maliciousPath));
        Assert.Equal(maliciousPath, ex.AttemptedPath);
    }

    [Fact]
    public void ResolveDestination_ReceiverOverride_TakesPrecedenceOverSenderPath()
    {
        var overridePath = Path.Combine(_root, "operator-chosen", "final.jpg");

        // Even a maliciously-crafted sender path is irrelevant once an explicit receiver override is set.
        var resolved = PathSafety.ResolveDestination(_root, "../../../etc/passwd", overridePath);

        Assert.Equal(Path.GetFullPath(overridePath), resolved);
    }

    [Fact]
    public void ResolveDestination_ReceiverOverride_MustBeAbsolute()
    {
        Assert.Throws<ArgumentException>(() => PathSafety.ResolveDestination(_root, "photo.jpg", "relative/override.jpg"));
    }

    [Fact]
    public void ResolveDestination_ReceiverOverride_CanPointOutsideRoot()
    {
        // Explicit receiver overrides are, by design, not confined to the transfer root — the receiver
        // owns this decision entirely; only the sender's hint is constrained.
        using var elsewhere = new TempDir();
        var overridePath = Path.Combine(elsewhere.Path, "elsewhere.jpg");

        var resolved = PathSafety.ResolveDestination(_root, "photo.jpg", overridePath);

        Assert.Equal(Path.GetFullPath(overridePath), resolved);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("castr-path-safety-elsewhere-").FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
