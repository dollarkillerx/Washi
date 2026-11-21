// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Markup.Xaml;
using Washi.Core.Localization;

namespace Washi.App.Localization;

/// <summary>{l:Tr Ui_Open} — a UI string in the current language (see strings.py).</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.Get(Key);
}
