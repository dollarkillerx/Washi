// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Washi.Core;
using Washi.Core.Localization;
using Washi.Masume;

namespace Washi.App.ViewModels;

/// <summary>One editable field. Coordinates are points on the upright page, top-left origin.</summary>
public sealed partial class FieldViewModel : ObservableObject
{
    [ObservableProperty] private int _page;
    [ObservableProperty] private FieldKind _kind;
    [ObservableProperty] private double _x0;
    [ObservableProperty] private double _y0;
    [ObservableProperty] private double _x1;
    [ObservableProperty] private double _y1;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _maxLen;
    [ObservableProperty] private int _align;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _prefilled = "";

    public PdfRect Rect => new(X0, Y0, X1, Y1);

    public string KindText => L.Get("Kind_" + Kind.ToString().ToLowerInvariant());

    public bool IsComb => Kind == FieldKind.Comb;

    /// <summary>What is already printed in the cell, with the detector's markers in the UI language.</summary>
    public string PrefilledText => Prefilled switch
    {
        MasumeDetector.DrawnMarker => L.Get("Ui_PrefilledDrawn"),
        MasumeDetector.UnreadableMarker => L.Get("Ui_PrefilledUnreadable"),
        var p => p,
    };

    partial void OnPrefilledChanged(string value) => OnPropertyChanged(nameof(PrefilledText));

    public string PositionText => L.F("Ui_Position", Math.Round(X0, 1), Math.Round(Y0, 1), Math.Round(X1 - X0, 1), Math.Round(Y1 - Y0, 1));

    partial void OnKindChanged(FieldKind value)
    {
        if (value == FieldKind.Comb && MaxLen <= 0) MaxLen = 10;
        Align = value switch { FieldKind.Amount => 2, FieldKind.Comb => 1, _ => Align == 2 ? 0 : Align };
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(IsComb));
    }

    partial void OnX0Changed(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnY0Changed(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnX1Changed(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnY1Changed(double value) => OnPropertyChanged(nameof(PositionText));

    /// <summary>Moves the whole rectangle (no resize).</summary>
    public void Offset(double dx, double dy)
    {
        X0 += dx; X1 += dx;
        Y0 += dy; Y1 += dy;
    }

    public void SetRect(PdfRect r)
    {
        X0 = Math.Min(r.X0, r.X1); X1 = Math.Max(r.X0, r.X1);
        Y0 = Math.Min(r.Y0, r.Y1); Y1 = Math.Max(r.Y0, r.Y1);
    }

    public static FieldViewModel From(DetectedField f) => new()
    {
        Page = f.Page, Kind = f.Kind, X0 = f.Rect.X0, Y0 = f.Rect.Y0, X1 = f.Rect.X1, Y1 = f.Rect.Y1,
        Name = f.Name, MaxLen = f.MaxLen, Align = f.Align, Label = f.Label, Prefilled = f.Prefilled,
    };

    public DetectedField ToDetected() => new()
    {
        Page = Page, Kind = Kind, Rect = Rect, Name = Name, MaxLen = Kind == FieldKind.Comb ? Math.Max(1, MaxLen) : 0,
        Align = Align, Label = Label, Prefilled = Prefilled,
    };
}
