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

    /// <summary>Text size in points; 0 = fitted to the field (the default).</summary>
    [ObservableProperty] private double _fontSize;
    /// <summary>"#RRGGBB"; empty = black.</summary>
    [ObservableProperty] private string _color = "";
    /// <summary>"" (the form language's usual face), "serif" or "sans".</summary>
    [ObservableProperty] private string _font = "";

    /// <summary>Radio buttons: buttons with the same group form one choice.</summary>
    [ObservableProperty] private string _group = "";
    /// <summary>Radio buttons: the option this button stands for.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display))] private string _export = "";
    /// <summary>Drop-down lists: the items, one per line.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OptionList))] private string _options = "";

    public IReadOnlyList<string> OptionList =>
        [.. Options.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct()];

    /// <summary>What the user typed in Preview ("true" for a ticked checkbox or the chosen radio button). Written into the saved PDF.</summary>
    [ObservableProperty] private string _value = "";

    // ------------------------------------------------------------------ a field the PDF already has (filling a form)
    /// <summary>The form protects this field from changes.</summary>
    [ObservableProperty] private bool _readOnly;
    /// <summary>The value the PDF holds (as <see cref="Value"/> holds it): what changed is what is saved.</summary>
    public string Original { get; set; } = "";
    public bool IsChanged => Value != Original;

    // ------------------------------------------------------------------ rules (typed fields): format, range, calculation
    /// <summary>"" (none), "number", "percent", "date" or "mask".</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNumberFormat), nameof(IsNumberFormat), nameof(HasPattern), nameof(HasRules))] private string _format = "";
    [ObservableProperty] private int _decimals;
    /// <summary>Number: a currency mark shown before the number ("¥", "$"); empty = none.</summary>
    [ObservableProperty] private string _currency = "";
    /// <summary>Date: yyyy/mm/dd and the like; mask: 9 a digit, A a letter ("999-9999").</summary>
    [ObservableProperty] private string _pattern = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRules))] private string _rangeMin = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRules))] private string _rangeMax = "";
    /// <summary>"" (typed by hand), "sum", "product", "average", "min" or "max" of <see cref="CalcSources"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCalculated), nameof(HasRules))] private string _calcOp = "";
    /// <summary>The names of the fields the calculation uses, separated by commas.</summary>
    [ObservableProperty] private string _calcSources = "";
    /// <summary>Why the value does not fit the field's rules (empty when it does).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";

    public bool HasNumberFormat => Format is "number" or "percent";
    public bool IsNumberFormat => Format == "number";
    public bool HasPattern => Format is "date" or "mask";
    public bool IsCalculated => CalcOp.Length > 0;
    public bool HasError => Error.Length > 0;
    public bool HasRules => Logic is not null;

    partial void OnFormatChanged(string value)
    {
        if (value == "date" && !Pattern.Contains('y')) Pattern = "yyyy/mm/dd";
        if (value == "mask" && Pattern.Contains('y')) Pattern = "999-9999";
        if (value == "mask" && Pattern.Length == 0) Pattern = "999-9999";
        Check();
    }
    partial void OnDecimalsChanged(int value) => Check();
    partial void OnCurrencyChanged(string value) => Check();
    partial void OnPatternChanged(string value) => Check();
    partial void OnRangeMinChanged(string value) => Check();
    partial void OnRangeMaxChanged(string value) => Check();

    /// <summary>The rules as the PDF will hold them (null when there are none, or the kind takes none).</summary>
    public Washi.Core.Forms.FieldLogic? Logic
    {
        get
        {
            if (Kind is not (FieldKind.Text or FieldKind.Amount or FieldKind.Comb)) return null;
            var format = Format switch
            {
                "number" => new Washi.Core.Forms.FieldFormat(Washi.Core.Forms.FieldFormatKind.Number, Math.Clamp(Decimals, 0, 10), 0, Currency.Trim()),
                "percent" => new Washi.Core.Forms.FieldFormat(Washi.Core.Forms.FieldFormatKind.Percent, Math.Clamp(Decimals, 0, 10)),
                "date" => Washi.Core.Forms.FieldFormat.Date(Pattern.Trim().Length > 0 ? Pattern.Trim() : "yyyy/mm/dd"),
                "mask" when Pattern.Trim().Length > 0 => Washi.Core.Forms.FieldFormat.Mask(Pattern.Trim()),
                _ => null,
            };
            double? min = Washi.Core.Forms.FieldLogic.ParseNumber(RangeMin), max = Washi.Core.Forms.FieldLogic.ParseNumber(RangeMax);
            var range = min is null && max is null ? null : new Washi.Core.Forms.FieldRange(min, max);
            var sources = CalcSources.Split([',', '、', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Washi.Core.Forms.CalcOp? op = CalcOp switch
            {
                "sum" => Washi.Core.Forms.CalcOp.Sum, "product" => Washi.Core.Forms.CalcOp.Product, "average" => Washi.Core.Forms.CalcOp.Average,
                "min" => Washi.Core.Forms.CalcOp.Min, "max" => Washi.Core.Forms.CalcOp.Max, _ => null,
            };
            var calc = op is { } o && sources.Length > 0 ? new Washi.Core.Forms.FieldCalc(o, sources) : null;
            var logic = new Washi.Core.Forms.FieldLogic(format, range, calc);
            return logic.IsEmpty ? null : logic;
        }
    }

    /// <summary>What the field shows: the value as its format writes it ("¥1,200" for "1200"), or as typed when it does not fit.</summary>
    public string Shown => Logic is { } l && l.Accept(Value) is { Error: null, Value: { } stored } ? l.Display(stored) : Value;

    /// <summary>Checks the value against the rules (the message is in the UI language).</summary>
    public void Check()
    {
        Error = Logic is { } l && Value.Length > 0 && l.Accept(Value) is { Error: not null } r
            ? L.F(r.Key!, Value, l.Format?.Pattern ?? "", l.Range is { } rg ? Washi.Core.Forms.FieldLogic.RangeText(rg) : "")
            : "";
        OnPropertyChanged(nameof(Shown));
        OnPropertyChanged(nameof(HasRules));
    }

    public bool IsChecked => Value == "true";

    partial void OnValueChanged(string value)
    {
        Check();
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ShowName));
        OnPropertyChanged(nameof(IsChanged));
    }

    /// <summary>Filled in: something other than blanks (forms often hold a space in an empty drop-down).</summary>
    public bool HasValue => Value.Trim().Length > 0;

    /// <summary>
    /// What the field list shows: what is in the field (its value), else what the form calls it (the printed label),
    /// else its name. An auto-made name such as p1_text_006 says nothing to the person filling the form.
    /// </summary>
    public string Display => Value.Length > 0
        ? Kind switch { FieldKind.Checkbox => "✓", FieldKind.Radio => "● " + (Export.Length > 0 ? Export : Label), _ => Value }
        : Label.Length > 0 ? Label : Name;

    /// <summary>The name under the row, when the row itself shows something else.</summary>
    public bool ShowName => Display != Name;

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(Display)); OnPropertyChanged(nameof(ShowName)); }
    partial void OnLabelChanged(string value) { OnPropertyChanged(nameof(Display)); OnPropertyChanged(nameof(ShowName)); }

    /// <summary>For the tick box in the fill panel.</summary>
    public bool Ticked { get => IsChecked; set { Value = value ? "true" : ""; OnPropertyChanged(); } }

    public bool IsCheckbox => Kind == FieldKind.Checkbox;
    public bool IsRadio => Kind == FieldKind.Radio;
    public bool IsChoice => Kind == FieldKind.Choice;
    public bool IsSignature => Kind == FieldKind.Signature;
    /// <summary>Typed into: text, amount, comb and drop-down values have a size, colour and face.</summary>
    public bool HasTextStyle => Kind is FieldKind.Text or FieldKind.Amount or FieldKind.Comb or FieldKind.Choice;
    /// <summary>Filled in Preview by typing (or choosing an item); checkboxes, radio buttons and signatures are not.</summary>
    public bool IsTyped => Kind is FieldKind.Text or FieldKind.Amount or FieldKind.Comb;

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

    public bool HasPrefilled => Prefilled.Length > 0;

    partial void OnPrefilledChanged(string value)
    {
        OnPropertyChanged(nameof(PrefilledText));
        OnPropertyChanged(nameof(HasPrefilled));
    }

    public string PositionText => L.F("Ui_Position", Math.Round(X0, 1), Math.Round(Y0, 1), Math.Round(X1 - X0, 1), Math.Round(Y1 - Y0, 1));

    partial void OnKindChanged(FieldKind value)
    {
        Value = "";   // a value of one type means nothing for another
        if (value == FieldKind.Comb && MaxLen <= 0) MaxLen = 10;
        if (value == FieldKind.Radio)
        {
            if (Group.Length == 0) Group = Name;
            if (Export.Length == 0) Export = Label.Length > 0 ? Label : Name;
        }
        Align = value switch { FieldKind.Amount => 2, FieldKind.Comb => 1, _ => Align == 2 ? 0 : Align };
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(IsCheckbox));
        OnPropertyChanged(nameof(IsRadio));
        OnPropertyChanged(nameof(IsChoice));
        OnPropertyChanged(nameof(IsSignature));
        OnPropertyChanged(nameof(HasTextStyle));
        OnPropertyChanged(nameof(IsTyped));
        OnPropertyChanged(nameof(Display));
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
        FontSize = f.FontSize, Color = f.Color, Font = f.Font,
        Group = f.Group, Export = f.Export, Options = string.Join("\n", f.Options),
        Format = f.Format?.Kind switch
        {
            Washi.Core.Forms.FieldFormatKind.Number => "number", Washi.Core.Forms.FieldFormatKind.Percent => "percent",
            Washi.Core.Forms.FieldFormatKind.Date => "date", Washi.Core.Forms.FieldFormatKind.Mask => "mask", _ => "",
        },
        Decimals = f.Format?.Decimals ?? 0, Currency = f.Format?.Currency ?? "", Pattern = f.Format?.Pattern ?? "",
        RangeMin = f.Range?.Min is { } mn ? Washi.Core.Forms.FieldLogic.RangeText(new(mn, null))[2..] : "",
        RangeMax = f.Range?.Max is { } mx ? Washi.Core.Forms.FieldLogic.RangeText(new(null, mx))[2..] : "",
        CalcOp = f.Calc?.Op.ToString().ToLowerInvariant() ?? "", CalcSources = f.Calc is { } c ? string.Join(", ", c.Sources) : "",
    };

    /// <summary>A widget of a field the PDF already has, as the editor fills it.</summary>
    public static FieldViewModel From(Washi.Core.Forms.WidgetInfo w)
    {
        var kind = w.Type switch
        {
            "checkbox" => FieldKind.Checkbox, "radio" => FieldKind.Radio, "choice" => FieldKind.Choice,
            _ => w.Comb ? FieldKind.Comb : FieldKind.Text,
        };
        var vm = From(new DetectedField
        {
            Page = w.Page, Kind = kind, Rect = w.Rect, Name = w.Name, MaxLen = w.Comb ? w.MaxLen : 0, Align = w.Align,
            Label = w.Tooltip, FontSize = w.FontSize, Group = kind == FieldKind.Radio ? w.Name : "", Export = w.OnState,
            Options = w.Options, Format = w.Logic?.Format, Range = w.Logic?.Range, Calc = w.Logic?.Calc,
        });
        if (kind is FieldKind.Checkbox or FieldKind.Radio)
        {
            vm.Export = w.OnState;   // the state it turns on (From cleared it for check boxes)
            if (kind == FieldKind.Radio && w.OnLabel.Length > 0) vm.Label = w.OnLabel;
        }
        vm.Value = kind is FieldKind.Checkbox or FieldKind.Radio
            ? (w.Value != "Off" && w.Value.Length > 0 && (w.OnState.Length == 0 || w.Value == w.OnState) ? "true" : "")
            : w.Value;
        vm.Original = vm.Value;
        vm.ReadOnly = w.ReadOnly;
        return vm;
    }

    public DetectedField ToDetected() => new()
    {
        Page = Page, Kind = Kind, Rect = Rect, Name = Name, MaxLen = Kind == FieldKind.Comb ? Math.Max(1, MaxLen) : 0,
        Align = Align, Label = Label, Prefilled = Prefilled, FontSize = FontSize, Color = Color, Font = Font,
        Group = Kind == FieldKind.Radio ? Group : "", Export = Kind == FieldKind.Radio ? Export : "",
        Options = Kind == FieldKind.Choice ? OptionList : [],
        Format = Logic?.Format, Range = Logic?.Range, Calc = Logic?.Calc,
    };
}
