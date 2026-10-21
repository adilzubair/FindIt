namespace FindIt.Core;

/// <summary>Archive paths are relative to the drive and never trusted as shell input.</summary>
public static class PortablePaths
{
    public const string MetadataDirectory = "FindIt/archive";
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static string Relative(string root, string absolute)
    {
        var normalizedRoot = NormalizeRoot(root);
        var full = Path.GetFullPath(absolute);
        if (!IsWithin(normalizedRoot, full)) throw new IOException("The selected path is outside this archive.");
        return Path.GetRelativePath(normalizedRoot, full).Replace('\\', '/');
    }

    public static bool IsWithin(string root, string path) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Resolve(string root, string relative, bool allowMetadata = false, bool mustExist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (relative is "." or "./" or ".\\")
        {
            var resolvedRoot = NormalizeRoot(root); RejectLinks(resolvedRoot, resolvedRoot);
            if (mustExist && !Directory.Exists(resolvedRoot)) throw new DirectoryNotFoundException("The archive is not connected.");
            return resolvedRoot;
        }
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0')) throw new IOException("Archive paths must be relative.");
        var components = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(p => p is ".." || p.EndsWith(' ') || p.EndsWith('.'))) throw new IOException("Unsafe archive path.");
        if (!allowMetadata && components.Length > 0 && components[0].Equals("FindIt", StringComparison.OrdinalIgnoreCase)) throw new IOException("Application metadata is not an original file.");
        var normalizedRoot = NormalizeRoot(root);
        var full = Path.GetFullPath(Path.Combine(normalizedRoot, Path.Combine(components)));
        if (!IsWithin(normalizedRoot, full)) throw new IOException("The path leaves the archive.");
        RejectLinks(normalizedRoot, full);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("This archive item is unavailable.", full);
        return full;
    }

    public static void RejectLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var normalizedRoot = NormalizeRoot(root);
        if (!IsWithin(normalizedRoot, current)) throw new IOException("Path is outside archive.");
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and junctions are skipped to keep archive access on this drive.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new IOException("Invalid archive path.");
        }
    }
}
