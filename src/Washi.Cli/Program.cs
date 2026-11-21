// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;

var version = Assembly.GetExecutingAssembly().GetName().Version;

if (args is ["--version"])
{
    Console.WriteLine($"washi {version}");
    return 0;
}

Console.Error.WriteLine($"""
    washi {version} - open-source PDF toolkit (pre-alpha)

    usage:
      washi --version

    Commands (detect, fill, ocr, redact, merge, ...) arrive as tools are ported
    from the reference implementation in prototype/.
    """);
return args.Length == 0 ? 0 : 2;
