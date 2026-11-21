// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Washi.Core.Localization;

/// <summary>
/// An error whose text exists in the UI languages. <see cref="Exception.Message"/> stays English (logs, the CLI's
/// agents, MCP tool results); the desktop app shows <see cref="Errors.Text"/> instead.
/// </summary>
public interface ILocalizedError
{
    /// <summary>The string key ("Err_…"), or null when there is only the English message.</summary>
    string? Key { get; }
    object[] Args { get; }
}

public static class Errors
{
    /// <summary>The error in the UI language when it has one, else its English message.</summary>
    public static string Text(Exception e) =>
        e is ILocalizedError { Key: { } key } le ? (le.Args.Length > 0 ? L.F(key, le.Args) : L.Get(key)) : e.Message;
}
