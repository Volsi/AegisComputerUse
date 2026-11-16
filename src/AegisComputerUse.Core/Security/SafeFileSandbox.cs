using System.IO;

namespace AegisComputerUse.Core.Security;

public sealed class SafeFileSandbox
{
    public const long DefaultMaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    public const int DefaultMaxFilesPerSession = 50;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".com", ".pif",
        ".msi", ".msix", ".msp", ".mst", ".lnk", ".url", ".scf", ".hta", ".reg", ".inf",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".wsf", ".wsh", ".jse",
        ".jar", ".docm", ".xlsm", ".pptm", ".iso", ".img", ".vhd", ".vhdx"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".log", ".html", ".htm", ".css", ".scss",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".cs", ".csproj", ".sln", ".py", ".java",
        ".kt", ".c", ".h", ".cpp", ".hpp", ".rs", ".go", ".rb", ".php", ".sql", ".graphql",
        ".svg", ".rtf", ".tex", ".sh", ".docx", ".xlsx", ".pdf", ".png", ".bmp"
    };

    private int _filesWrittenThisSession;

    public string SandboxRoot { get; }
    public long MaxFileSizeBytes { get; }
    public int MaxFilesPerSession { get; }

    public SafeFileSandbox(
        string sandboxRoot,
        long maxFileSizeBytes = DefaultMaxFileSizeBytes,
        int maxFilesPerSession = DefaultMaxFilesPerSession)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
            throw new ArgumentException("Sandbox root cannot be empty.", nameof(sandboxRoot));

        string fullRoot = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? rootDrive = Path.GetPathRoot(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullRoot, rootDrive, StringComparison.OrdinalIgnoreCase))
            throw new SandboxSecurityException("Sandbox root cannot be a drive root.");

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (IsSameOrSubPath(fullRoot, windowsDir) ||
            (!string.IsNullOrEmpty(programFiles) && IsSameOrSubPath(fullRoot, programFiles)) ||
            (!string.IsNullOrEmpty(programFilesX86) && IsSameOrSubPath(fullRoot, programFilesX86)))
        {
            throw new SandboxSecurityException("Sandbox root cannot point inside protected OS directories.");
        }

        Directory.CreateDirectory(fullRoot);
        EnsureNoReparsePoints(fullRoot);

        SandboxRoot = fullRoot;
        MaxFileSizeBytes = maxFileSizeBytes;
        MaxFilesPerSession = maxFilesPerSession;
    }

    public static IReadOnlyCollection<string> SupportedExtensions => AllowedExtensions;

    public string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new SandboxSecurityException("File path cannot be empty.");

        if (relativePath.EndsWith(' ') || relativePath.EndsWith('.'))
            throw new SandboxSecurityException("Trailing spaces or dots in filenames are forbidden.");

        string cleaned = relativePath.Trim();
        if (cleaned.Length > 240)
            throw new SandboxSecurityException("Relative file path exceeds maximum length.");

        if (Path.IsPathRooted(cleaned) || cleaned.StartsWith("\\\\", StringComparison.Ordinal) || cleaned.StartsWith('/'))
            throw new SandboxSecurityException("Absolute or UNC paths are forbidden in sandbox.");

        if (cleaned.Contains(':') || cleaned.Contains('\0'))
            throw new SandboxSecurityException("Drive letters, NTFS ADS streams (:), or null bytes are forbidden.");

        if (cleaned.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            throw new SandboxSecurityException("Invalid filename characters detected.");

        string[] segments = cleaned.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new SandboxSecurityException("No target filename provided.");

        foreach (string seg in segments)
        {
            if (seg is "." or "..")
                throw new SandboxSecurityException("Path traversal segments ('.' or '..') are forbidden.");

            if (seg.EndsWith('.') || seg.EndsWith(' '))
                throw new SandboxSecurityException("Trailing dots or spaces in filenames are forbidden.");

            string baseName = Path.GetFileNameWithoutExtension(seg).TrimEnd('.', ' ');
            if (ReservedDeviceNames.Contains(baseName) || ReservedDeviceNames.Contains(seg))
                throw new SandboxSecurityException($"Reserved Windows device name '{seg}' is forbidden.");
        }

        string ext = Path.GetExtension(segments[^1]);
        if (string.IsNullOrEmpty(ext))
            throw new SandboxSecurityException("File must have an explicit allowed extension.");

        if (BlockedExtensions.Contains(ext))
            throw new SandboxSecurityException($"Executable or dangerous script extension '{ext}' is strictly blocked.");

        if (!AllowedExtensions.Contains(ext))
            throw new SandboxSecurityException($"Extension '{ext}' is not in the sandbox allowlist.");

        string combined = Path.GetFullPath(Path.Combine(SandboxRoot, Path.Combine(segments)));
        string expectedPrefix = SandboxRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new SandboxSecurityException("Resolved path escapes sandbox root.");

        EnsureNoReparsePointsInHierarchy(SandboxRoot, segments[..^1]);

        if (File.Exists(combined))
        {
            var attrs = File.GetAttributes(combined);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new SandboxSecurityException("Target file is a symbolic link or reparse point.");
        }

        return combined;
    }

    public string WriteBytesSafe(string relativePath, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.LongLength > MaxFileSizeBytes)
            throw new SandboxSecurityException($"File size ({data.LongLength} bytes) exceeds limit ({MaxFileSizeBytes} bytes).");

        int count = Interlocked.Increment(ref _filesWrittenThisSession);
        if (count > MaxFilesPerSession)
        {
            Interlocked.Decrement(ref _filesWrittenThisSession);
            throw new SandboxSecurityException($"Session file creation budget ({MaxFilesPerSession}) exceeded.");
        }

        string targetPath = ResolveSafePath(relativePath);
        string? parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
            EnsureNoReparsePointsInHierarchy(SandboxRoot, GetRelativeSegments(SandboxRoot, parent));
        }

        using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough);
        fs.Write(data);
        fs.Flush(true);
        return targetPath;
    }

    private static string[] GetRelativeSegments(string root, string targetDir)
    {
        string rel = Path.GetRelativePath(root, targetDir);
        if (rel == ".") return [];
        return rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static void EnsureNoReparsePointsInHierarchy(string root, string[] dirSegments)
    {
        EnsureNoReparsePoints(root);
        string current = root;
        foreach (string seg in dirSegments)
        {
            current = Path.Combine(current, seg);
            if (Directory.Exists(current))
            {
                EnsureNoReparsePoints(current);
            }
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SandboxSecurityException($"Reparse point / symlink forbidden in sandbox path: {path}");
    }

    private static bool IsSameOrSubPath(string candidate, string baseDir)
    {
        string normBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normCand = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normCand.StartsWith(normBase, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SandboxSecurityException : Exception
{
    public SandboxSecurityException(string message) : base(message) { }
}
