// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from MySSH's myssh-mcp bridge (same authors, AGPL-3.0-or-later).
//
// washi-mcp --app [--socket PATH] [--no-launch]: stdio <-> the Washi app's local MCP socket, for Claude Desktop and
// other stdio-only MCP clients. A pure byte pump: the Washi window owns the MCP server, folder policy and approvals.
// Exit: 0 stdin EOF / server closed, 3 Washi is not running (or its MCP server is off), 1 any other error.
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using Washi.McpHost;

internal static class Bridge
{
    private const int ExitOk = 0, ExitError = 1, ExitNotRunning = 3;

    public static async Task<int> RunAsync(string? address, bool launch)
    {
        try
        {
            address ??= Environment.GetEnvironmentVariable("WASHI_MCP_SOCKET");
            var custom = !string.IsNullOrEmpty(address);
            address = custom ? address! : LocalEndpoint.DefaultAddress();
            var (server, socket, notRunning) = await ConnectAsync(address);
            if (server is null && launch && !custom && LaunchApp() is { } app)
            {
                Console.Error.WriteLine($"washi-mcp: Washi is not running; started {app}, waiting for its MCP server...");
                var until = DateTime.UtcNow.AddSeconds(45);
                while (server is null && DateTime.UtcNow < until)
                {
                    await Task.Delay(300);
                    (server, socket, notRunning) = await ConnectAsync(address);
                }
            }
            if (server is null)
            {
                Console.Error.WriteLine($"washi-mcp: Washi is not running ({notRunning ?? address}).");
                Console.Error.WriteLine("washi-mcp: start Washi and turn on the MCP server in Settings → AI & MCP.");
                return ExitNotRunning;
            }
            await using (server)
            {
                var stdin = Console.OpenStandardInput();
                var stdout = Console.OpenStandardOutput();
                var up = PumpAsync(stdin, server);    // client -> Washi
                var down = PumpAsync(server, stdout); // Washi -> client
                if (await Task.WhenAny(up, down) == up)
                {
                    // stdin EOF: the client is done. Half-close so the server sees EOF (it cancels the session), then let
                    // already-written responses drain for a moment.
                    try { socket?.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                    await Task.WhenAny(down, Task.Delay(2000));
                }
            }
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"washi-mcp: {ex.Message}");
            return ExitError;
        }
    }

    /// <summary>Inside Washi.app (Contents/MacOS/mcp/washi-mcp) opens the app in the background. Returns what it started, or null.</summary>
    private static string? LaunchApp()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var dir = AppContext.BaseDirectory.TrimEnd('/');
        const string inBundle = "/Contents/MacOS/mcp";
        if (!dir.EndsWith(inBundle, StringComparison.Ordinal)) return null;
        var bundle = dir[..^inBundle.Length];
        try
        {
            // `open` detaches the app from this process: the MCP client may kill the bridge at any time.
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(bundle);
            using var p = Process.Start(psi);
            p?.WaitForExit(10_000);
            return p is { ExitCode: 0 } ? bundle : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"washi-mcp: could not start {bundle}: {ex.Message}");
            return null;
        }
    }

    private static async Task<(Stream? Stream, Socket? Socket, string? NotRunning)> ConnectAsync(string address)
    {
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly also verifies the pipe server runs as the same user (anti-squatting).
            var pipe = new NamedPipeClientStream(".", LocalEndpoint.PipeName(address), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(2000);
                return (pipe, null, null);
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync();
                return (null, null, $"nobody is listening on {address}");
            }
        }

        LocalEndpoint.ValidateSocketPath(address);
        if (!File.Exists(address))
            return (null, null, $"socket not found: {address}");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(address));
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            socket.Dispose();
            return (null, null, $"stale socket, nobody is listening: {address}");
        }
        if (!LocalEndpoint.PeerIsCurrentUser(socket, out var peer))
        {
            socket.Dispose();
            throw new IOException($"refusing to talk to {address}: it is owned by uid {peer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}, not by the current user");
        }
        return (new NetworkStream(socket, ownsSocket: true), socket, null);
    }

    private static async Task PumpAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                await to.FlushAsync(); // newline-delimited JSON-RPC: never sit on a partial buffer
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
    }
}
