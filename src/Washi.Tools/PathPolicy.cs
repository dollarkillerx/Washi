// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Tools;

/// <summary>A tool call was refused or failed in a way the caller should see.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Which files the tools may touch. Every path an agent sends is resolved (symlinks
/// included) and must lie inside one of the allowed folders. Fonts may also be read from
/// the system font folders. Outputs are new files only: an existing file is never replaced.
/// </summary>
public sealed class PathPolicy
{
    private static readonly StringComparison Cmp =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public IReadOnlyList<string> Roots { get; }
    private readonly IReadOnlyList<string> _fontRoots;

    public PathPolicy(IEnumerable<string> roots)
    {
        Roots = roots.Select(r => Real(Path.GetFullPath(Environment.ExpandEnvironmentVariables(ExpandHome(r)))))
                     .Where(Directory.Exists).Distinct().ToList();
        if (Roots.Count == 0) throw new ArgumentException("no allowed folder exists");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _fontRoots = new[]
        {
            "/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library/Fonts"),
            "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".fonts"), Path.Combine(home, ".local/share/fonts"),
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft/Windows/Fonts"),
        }.Where(p => p.Length > 0 && Directory.Exists(p)).Select(p => Real(p)).ToList();
    }

    /// <summary>An existing PDF (or other file) inside the allowed folders.</summary>
    public string Input(string path)
    {
        var full = Resolve(path);
        if (!Inside(full, Roots)) throw new ToolException($"{path} is outside the folders Washi may use ({string.Join(", ", Roots)})");
        if (!File.Exists(full)) throw new ToolException($"file not found: {path}");
        return full;
    }

    /// <summary>A font file inside the allowed folders or a system font folder.</summary>
    public string Font(string path)
    {
        var full = Resolve(path);
        if (!Inside(full, Roots) && !Inside(full, _fontRoots))
            throw new ToolException($"{path} is outside the allowed folders and the system font folders");
        if (!File.Exists(full)) throw new ToolException($"font not found: {path}");
        return full;
    }

    /// <summary>
    /// Where to write. Defaults to "&lt;input name&gt;&lt;suffix&gt;.pdf" next to the input. Must be inside the
    /// allowed folders and must not exist yet.
    /// </summary>
    public string Output(string? path, string input, string suffix)
    {
        var target = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + suffix + ".pdf")
            : path;
        var full = Resolve(target);
        if (!Inside(full, Roots)) throw new ToolException($"{target} is outside the folders Washi may use ({string.Join(", ", Roots)})");
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new ToolException($"folder does not exist: {Path.GetDirectoryName(target)}");
        if (File.Exists(full) || Directory.Exists(full)) throw new ToolException($"{target} already exists; choose another output name (Washi never overwrites files)");
        if (!full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw new ToolException("output must be a .pdf file");
        return full;
    }

    /// <summary>A folder to write new files into: inside the allowed folders; created if missing.</summary>
    public string OutputFolder(string? path, string input, string suffix)
    {
        var target = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + suffix)
            : path;
        var full = Resolve(target);
        if (!Inside(full, Roots)) throw new ToolException($"{target} is outside the folders Washi may use ({string.Join(", ", Roots)})");
        if (File.Exists(full)) throw new ToolException($"{target} is a file, not a folder");
        return full;
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ToolException("path is empty");
        var p = ExpandHome(path.Trim());
        if (!Path.IsPathRooted(p)) p = Path.Combine(Roots[0], p);   // relative paths start in the first allowed folder
        return Real(Path.GetFullPath(p));
    }

    private static string ExpandHome(string p) =>
        p == "~" || p.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "") : p;

    private static bool Inside(string full, IEnumerable<string> roots) =>
        roots.Any(r => full.Equals(r, Cmp) || full.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, Cmp));

    /// <summary>Resolves symlinks in every existing component (so /tmp and /private/tmp compare equal).</summary>
    private static string Real(string full) => Real(full, 0);

    private static string Real(string full, int depth)
    {
        if (depth > 16) throw new ToolException("too many symbolic links");
        var root = Path.GetPathRoot(full)!;
        var cur = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(cur, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            for (int hops = 0; info.Exists && info.LinkTarget is { } target && hops < 32; hops++)
            {
                // a relative target is relative to the folder that holds the link
                next = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(next)!, target));
                info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            }
            // the target itself may run through other links (e.g. /etc -> private/etc on macOS)
            if (next != Path.Combine(cur, part)) next = Real(next, depth + 1);
            cur = next;
        }
        return cur;
    }
}
