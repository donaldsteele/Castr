namespace Castr.Core.Security;

public sealed class PathTraversalException(string attemptedPath)
    : Exception($"Rejected unsafe sender-suggested destination path: '{attemptedPath}'")
{
    public string AttemptedPath { get; } = attemptedPath;
}

/// <summary>
/// Enforces that a sender can only ever suggest where inside a receiver-owned root a file lands — never
/// dictate a destination outright. A sender-suggested relative path may go into subdirectories but must
/// never contain '..' (upward traversal) or be rooted/absolute; a receiver's own explicit override always
/// takes precedence over anything the sender suggested.
/// </summary>
public static class PathSafety
{
    public static string ResolveDestination(string rootDirectory, string senderSuggestedRelativePath, string? receiverExplicitOverride = null)
    {
        if (receiverExplicitOverride is not null)
        {
            if (!Path.IsPathRooted(receiverExplicitOverride))
                throw new ArgumentException("Receiver override destination must be an absolute path.", nameof(receiverExplicitOverride));
            return Path.GetFullPath(receiverExplicitOverride);
        }

        if (!IsSafeRelativePath(senderSuggestedRelativePath))
            throw new PathTraversalException(senderSuggestedRelativePath);

        var root = Path.GetFullPath(rootDirectory);

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(root, senderSuggestedRelativePath));
        }
        catch (ArgumentException)
        {
            // Invalid path characters etc. — treat identically to a rejected traversal attempt, not a crash.
            throw new PathTraversalException(senderSuggestedRelativePath);
        }

        // Defense in depth: re-confirm the fully resolved path still lives under root, in case any
        // platform-specific normalization behaved unexpectedly despite passing the segment-level check.
        if (!IsUnderRoot(root, combined))
            throw new PathTraversalException(senderSuggestedRelativePath);

        return combined;
    }

    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        if (Path.IsPathRooted(relativePath))
            return false; // no absolute/rooted sender paths, ever

        if (relativePath[0] is '/' or '\\')
            return false; // leading separator, incl. UNC (\\server\share\...) — Path.IsPathRooted only
                           // recognizes a leading '\' as rooted on Windows, not on Linux/macOS, so this
                           // check has to be explicit to reject it identically on every platform

        if (relativePath.Contains(':'))
            return false; // blocks Windows drive-relative paths ("C:foo") and NTFS alternate-data-stream tricks ("file.txt:evil")

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        return Array.TrueForAll(segments, segment => segment != "..");
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}
