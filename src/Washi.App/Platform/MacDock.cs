// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Washi.App.Platform;

/// <summary>
/// The Dock icon on macOS. Inside Washi.app the bundle's washi.icns is used; when the editor
/// runs straight from <c>dotnet</c> (development), macOS shows the generic "exec" icon, so
/// the Washi icon is set on NSApplication at startup.
/// </summary>
internal static class MacDock
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    public static void SetIcon(byte[] png)
    {
        if (!OperatingSystem.IsMacOS() || IsBundled) return;
        try
        {
            var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), handle.AddrOfPinnedObject(), (nuint)png.Length);
                var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
                var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (image != IntPtr.Zero) Send(app, sel_registerName("setApplicationIconImage:"), image);
            }
            finally { handle.Free(); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // not fatal: the Dock keeps the default icon
        }
    }

    /// <summary>True inside Washi.app, where Info.plist already names the icon.</summary>
    private static bool IsBundled => AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal);
}
