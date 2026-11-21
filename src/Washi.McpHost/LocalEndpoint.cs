// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH (same authors, AGPL-3.0-or-later).
// Shared by the in-app MCP server (Washi.McpHost) and the stdio bridge (washi-mcp --app, linked source file).
// Keep it free of ASP.NET Core and the MCP SDK.
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Washi.McpHost;

/// <summary>
/// Where the local MCP endpoint lives and how both ends check who is on the other side.
/// <list type="bullet">
/// <item>macOS: <c>~/Library/Application Support/Washi/mcp.sock</c>.</item>
/// <item>Linux: <c>$XDG_RUNTIME_DIR/washi/mcp.sock</c>, or <c>/tmp/washi-{uid}/mcp.sock</c> when
/// XDG_RUNTIME_DIR is unset.</item>
/// <item>Windows: named pipe <c>\\.\pipe\washi-mcp-{user}</c> (PipeOptions.CurrentUserOnly on both ends).</item>
/// </list>
/// </summary>
internal static partial class LocalEndpoint
{
    public const string SocketFileName = "mcp.sock";
    public const string MacDirectoryName = "Washi";
    public const string LinuxDirectoryName = "washi";
    public const string WindowsPipePrefix = @"\\.\pipe\";

    /// <summary>sizeof(sockaddr_un.sun_path): 104 on macOS/BSD, 108 on Linux; one byte is the terminating NUL.</summary>
    public static int MaxSocketPathBytes => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 103 : 107;

    /// <summary>Default address for the current user and OS (a socket path, or <c>\\.\pipe\…</c> on Windows).</summary>
    public static string DefaultAddress()
    {
        if (OperatingSystem.IsWindows())
            return WindowsPipePrefix + "washi-mcp-" + Environment.UserName;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", MacDirectoryName, SocketFileName);
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && xdg.StartsWith('/'))
            return Path.Combine(xdg, LinuxDirectoryName, SocketFileName);
        return Path.Combine("/tmp", $"{LinuxDirectoryName}-{CurrentUid()}", SocketFileName);
    }

    /// <summary>Pipe name for NamedPipe(Server|Client)Stream: accepts "\\.\pipe\name" or a bare name.</summary>
    public static string PipeName(string address) =>
        address.StartsWith(WindowsPipePrefix, StringComparison.OrdinalIgnoreCase) ? address[WindowsPipePrefix.Length..] : address;

    /// <summary>Throws <see cref="PathTooLongException"/> with an actionable message when the socket path does not fit sun_path.</summary>
    public static void ValidateSocketPath(string path)
    {
        var bytes = Encoding.UTF8.GetByteCount(path);
        if (bytes > MaxSocketPathBytes)
            throw new PathTooLongException(
                $"MCP socket path is {bytes} bytes, but Unix domain sockets allow at most {MaxSocketPathBytes} on this OS: {path}. " +
                "Use a shorter home directory or override the socket path.");
    }

    // ------------------------------------------------------------------ peer credentials

    private static uint? s_uid;

    /// <summary>Effective uid of this process (Unix only).</summary>
    public static uint CurrentUid() => s_uid ??= ReadCurrentUid();

    private static uint ReadCurrentUid()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) return geteuid();
        // Linux: avoid P/Invoke into "libc" (glibc ships libc.so as a linker script, so the bare name may not dlopen).
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
            var fields = line["Uid:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return uint.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture); // real, effective, saved, fs
        }
        throw new PlatformNotSupportedException("cannot determine the current uid (/proc/self/status has no Uid line)");
    }

    /// <summary>uid of the process on the other end of a connected Unix domain socket; null if it cannot be determined.</summary>
    public static uint? PeerUid(Socket socket)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return getpeereid((int)socket.Handle, out var uid, out _) == 0 ? uid : null;
        if (OperatingSystem.IsLinux())
        {
            // getsockopt(SOL_SOCKET=1, SO_PEERCRED=17) -> struct ucred { pid_t pid; uid_t uid; gid_t gid; }
            Span<byte> ucred = stackalloc byte[12];
            try
            {
                var n = socket.GetRawSocketOption(1, 17, ucred);
                return n >= 8 ? BitConverter.ToUInt32(ucred[4..8]) : null;
            }
            catch (SocketException) { return null; }
        }
        return null;
    }

    /// <summary>True when the peer runs as the same user as this process. Fails closed when the uid is unknown.</summary>
    public static bool PeerIsCurrentUser(Socket socket, out uint? peerUid)
    {
        peerUid = PeerUid(socket);
        return peerUid is { } uid && uid == CurrentUid();
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getpeereid(int fd, out uint euid, out uint egid);

    [LibraryImport("libc")]
    private static partial uint geteuid();
}
