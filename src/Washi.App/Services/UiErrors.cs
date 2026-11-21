// SPDX-License-Identifier: AGPL-3.0-or-later
using Washi.Ai.Chat;
using Washi.Core.Localization;

namespace Washi.App.Services;

/// <summary>Error text for the user, in the UI language wherever Washi wrote the message itself.</summary>
public static class UiErrors
{
    /// <summary>A provider failure keeps the provider's own words, after what kind of failure it is.</summary>
    public static string Text(Exception e) => e switch
    {
        AiProviderException p => Provider(p.Kind, p.Message),
        OperationCanceledException => L.Get("Ui_AiModelsTimeout"),
        _ => Errors.Text(e),
    };

    public static string Provider(AiErrorKind kind, string? detail)
    {
        var what = kind switch
        {
            AiErrorKind.Authentication => L.Get("Err_AiAuth"),
            AiErrorKind.RateLimited => L.Get("Err_AiRateLimited"),
            AiErrorKind.Overloaded => L.Get("Err_AiOverloaded"),
            AiErrorKind.InvalidRequest => L.Get("Err_AiInvalidRequest"),
            AiErrorKind.Network => L.Get("Err_AiNetwork"),
            AiErrorKind.MalformedResponse => L.Get("Err_AiMalformed"),
            _ => null,
        };
        detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        return what is null ? detail ?? "?" : detail is null ? what : $"{what} ({detail})";
    }
}
