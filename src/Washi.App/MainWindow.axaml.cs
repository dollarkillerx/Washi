// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Washi.App.Services;
using Washi.App.ViewModels;
using Washi.Masume;

namespace Washi.App;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow() : this(null) { }

    public MainWindow(IDialogs? dialogs)
    {
        InitializeComponent();
        // Choice<T>.Value is the value the field stores; set in code because the generic type
        // is awkward to name in XAML.
        KindBox.SelectedValueBinding = new Binding(nameof(Choice<int>.Value));
        AlignBox.SelectedValueBinding = new Binding(nameof(Choice<int>.Value));
        SizeBox.SelectedValueBinding = new Binding(nameof(Choice<double>.Value));
        ColorBox.SelectedValueBinding = new Binding(nameof(Choice<string>.Value));
        FaceBox.SelectedValueBinding = new Binding(nameof(Choice<string>.Value));
        FormatBox.SelectedValueBinding = new Binding(nameof(Choice<string>.Value));
        CalcBox.SelectedValueBinding = new Binding(nameof(Choice<string>.Value));
        ViewModel = new MainViewModel(dialogs ?? new AvaloniaDialogs(this));
        DataContext = ViewModel;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        foreach (var rail in new[] { FormToolButton, OcrToolButton, CombineToolButton, SplitToolButton, OrganizeToolButton, CompressToolButton, CommentToolButton, SignToolButton, ProtectToolButton, RedactToolButton, EditTextToolButton, StampToolButton, CompareToolButton, EraseToolButton, AskToolButton })
            rail.AddHandler(PointerPressedEvent, (_, _) =>
            {
                // a second click on the active tool folds its panel away; any tool click opens it
                ViewModel.ToolPanelOpen = rail.IsChecked != true || !ViewModel.ToolPanelOpen;
            }, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PageScroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        // pinch: two fingers on a touch screen, and the trackpad's magnify gesture on macOS
        PageScroller.GestureRecognizers.Add(new PinchGestureRecognizer());
        PageScroller.AddHandler(InputElement.PinchEvent, OnPinch);
        PageScroller.AddHandler(InputElement.PinchEndedEvent, (_, _) => _pinchStartZoom = null);
        PageScroller.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnMagnify);
        DocumentArea.SizeChanged += (_, e) =>
        {
            ViewModel.ViewportWidth = e.NewSize.Width;
            ViewModel.ViewportHeight = e.NewSize.Height;
        };
        // the Settings page gets its view model the first time it is shown
        ViewModel.PropertyChanged += (_, e) =>
        {
            // (until then it inherits the window's MainViewModel)
            if (e.PropertyName == nameof(MainViewModel.ActiveTool) && ViewModel.IsSettingsTool && SettingsPage.DataContext is not SettingsViewModel)
                SettingsPage.DataContext = ViewModel.Settings;
        };
        ViewModel.EditRequested += BeginEdit;
        // edit text: a click on a line puts the cursor in its box, the whole text selected
        ViewModel.EditText.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditTextViewModel.Selected) && ViewModel.EditText.Selected is not null)
                Dispatcher.UIThread.Post(() => { EditTextBox.Focus(); EditTextBox.SelectAll(); }, DispatcherPriority.Input);
        };
        // find bar: the cursor goes to the box; the current hit is scrolled into view (after its page is drawn)
        ViewModel.Find.Opened += () => Dispatcher.UIThread.Post(() => { FindBox.Focus(); FindBox.SelectAll(); }, DispatcherPriority.Input);
        ViewModel.Find.Changed += () => Dispatcher.UIThread.Post(ShowFindHit, DispatcherPriority.Background);
        FindBox.KeyDown += OnFindKeyDown;
        // bookmarks: a click opens the bookmark's place (once its page is drawn)
        BookmarkList.Tapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is BookmarkRow row) ViewModel.Bookmarks.Go(row);
        };
        ViewModel.Bookmarks.Navigate += (_, top) => Dispatcher.UIThread.Post(() =>
        {
            double z = ViewModel.Zoom, y = (top ?? 0) * z;
            Canvas.BringIntoView(new Rect(0, Math.Max(0, y - 12), 1, Math.Max(1, PageScroller.Viewport.Height - 24)));
        }, DispatcherPriority.Background);
        SignPad.InkCompleted += ink => ViewModel.Sign.SetInk(ink);
        FieldEditor.KeyDown += OnEditorKeyDown;
        FieldEditor.LostFocus += (_, _) => EndEdit(commit: true);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Zoom) or nameof(MainViewModel.PageIndex) or nameof(MainViewModel.PreviewMode)
                                or nameof(MainViewModel.ActiveTool) or nameof(MainViewModel.PdfPath))
                EndEdit(commit: true);
        };
        // a click in the list selects the field; a field that is not in the list (an empty one while filling) stays
        // selected on the page, so the list never writes "nothing" back
        FieldList.SelectionChanged += (_, _) =>
        {
            if (FieldList.SelectedItem is FieldViewModel picked && !ReferenceEquals(picked, ViewModel.SelectedField))
                ViewModel.SelectedField = picked;
        };
        // the field list follows the selection, so Tab through fields keeps the row in view
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedField) && ViewModel.SelectedField is { } f)
                FieldList.ScrollIntoView(f);
        };
    }

    // ------------------------------------------------------------------ Preview: typing into a field
    private FieldViewModel? _editing;

    /// <summary>Opens the inline editor over the field, sized and spaced like the field itself.</summary>
    private void BeginEdit(FieldViewModel f)
    {
        EndEdit(commit: true);
        _editing = f;
        double z = ViewModel.Zoom;
        double w = (f.X1 - f.X0) * z, h = (f.Y1 - f.Y0) * z;
        var ed = FieldEditor;
        ed.Margin = new Thickness(f.X0 * z, f.Y0 * z, 0, 0);
        ed.Width = Math.Max(24, w);
        ed.Height = Math.Max(14, h);
        double auto = f.Kind == FieldKind.Comb && f.MaxLen > 0 ? Math.Min(h * 0.62, w / f.MaxLen * 0.9) : Math.Min(h * 0.62, 12 * z);
        ed.FontSize = Math.Max(8, f.FontSize > 0 ? Math.Min(f.FontSize * z, Math.Max(8, h - 4)) : auto);
        ed.Foreground = Controls.FieldCanvas.TextBrush(f);
        ed.FontFamily = Controls.FieldCanvas.TextFamily(f);
        ed.MaxLength = f.Kind == FieldKind.Comb ? Math.Max(1, f.MaxLen) : 0;
        ed.TextAlignment = f.Align switch { 1 => Avalonia.Media.TextAlignment.Center, 2 => Avalonia.Media.TextAlignment.Right, _ => Avalonia.Media.TextAlignment.Left };
        if (f.Kind == FieldKind.Comb && f.MaxLen > 0)
        {
            // one character per printed box: spread the letters to the box pitch
            double pitch = w / f.MaxLen;
            double glyph = new Avalonia.Media.FormattedText("0", System.Globalization.CultureInfo.InvariantCulture, Avalonia.Media.FlowDirection.LeftToRight,
                                                            new Avalonia.Media.Typeface(ed.FontFamily), ed.FontSize, null).Width;
            ed.LetterSpacing = Math.Max(0, pitch - glyph);
            ed.Padding = new Thickness(Math.Max(0, (pitch - glyph) / 2) - 1.5, 0, 0, 0);
            ed.TextAlignment = Avalonia.Media.TextAlignment.Left;
        }
        else
        {
            ed.LetterSpacing = 0;
            ed.Padding = new Thickness(2 * z, 0);
        }
        ed.Text = f.Value;
        ed.IsVisible = true;
        ed.Focus();
        ed.SelectAll();
    }

    private void EndEdit(bool commit)
    {
        if (_editing is not { } f) return;
        _editing = null;
        if (commit) f.Value = FieldEditor.Text ?? "";
        FieldEditor.IsVisible = false;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                EndEdit(commit: true);
                Canvas.Focus();
                break;
            case Key.Escape:
                EndEdit(commit: false);
                Canvas.Focus();
                break;
            case Key.Tab:
                // Tab fills the next field, as in a PDF viewer
                EndEdit(commit: true);
                ViewModel.SelectNextField(backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                if (ViewModel.SelectedField is { Kind: not FieldKind.Checkbox } next) BeginEdit(next);
                else Canvas.Focus();
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ find bar
    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift): ViewModel.Find.PreviousCommand.Execute(null); break;
            case Key.Enter: ViewModel.Find.NextCommand.Execute(null); break;
            case Key.Escape:
                ViewModel.Find.CloseCommand.Execute(null);
                Canvas.Focus();
                break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Scrolls the page so the current hit is in view, with some room around it.</summary>
    private void ShowFindHit()
    {
        if (ViewModel.Find is not { IsOpen: true, Current: { } hit } || hit.Page != ViewModel.PageIndex || hit.Boxes.Count == 0) return;
        var b = hit.Boxes[0];
        double z = ViewModel.Zoom;
        Canvas.BringIntoView(new Rect(b.X0 * z, b.Y0 * z, Math.Max(1, b.Width * z), Math.Max(1, b.Height * z)).Inflate(new Thickness(80, 60)));
    }

    // ------------------------------------------------------------------ keyboard
    private static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        bool typing = FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox;

        // ⌘/Ctrl shortcuts. On macOS the native menu owns them (it shows them too).
        if (e.KeyModifiers == CommandKey && !OperatingSystem.IsMacOS())
        {
            switch (e.Key)
            {
                case Key.O: vm.OpenCommand.Execute(null); break;
                case Key.F: vm.Find.OpenCommand.Execute(null); break;
                case Key.P: vm.Print.OpenCommand.Execute(null); break;
                case Key.G: vm.Find.NextCommand.Execute(null); break;
                case Key.S when vm.IsFormTool: vm.MakeFillableCommand.Execute(null); break;
                case Key.W: vm.CloseDocumentCommand.Execute(null); break;
                case Key.OemPlus or Key.Add: vm.ZoomInCommand.Execute(null); break;
                case Key.OemMinus or Key.Subtract: vm.ZoomOutCommand.Execute(null); break;
                case Key.D0 or Key.NumPad0: vm.FitWidthCommand.Execute(null); break;
                case Key.D9 or Key.NumPad9: vm.FitPageCommand.Execute(null); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && vm.Print.IsOpen)
        {
            vm.Print.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == (CommandKey | KeyModifiers.Shift) && e.Key == Key.G && !OperatingSystem.IsMacOS())
        {
            vm.Find.PreviousCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F3 && vm.Find.IsOpen)
        {
            if (e.KeyModifiers == KeyModifiers.Shift) vm.Find.PreviousCommand.Execute(null); else vm.Find.NextCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (typing || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) || !vm.HasDocument) return;

        switch (e.Key)
        {
            case Key.V when vm.IsFormTool: vm.SelectModeCommand.Execute(null); break;
            case Key.F when vm.IsFormTool: vm.DrawFieldModeCommand.Execute(null); break;
            case Key.P when vm.IsFormTool: vm.TogglePreviewCommand.Execute(null); break;
            case Key.Tab when vm.IsFormTool:
                vm.SelectNextField(backwards: e.KeyModifiers == KeyModifiers.Shift);
                if (vm.PreviewMode && vm.SelectedField is { Kind: not FieldKind.Checkbox } f) BeginEdit(f);
                else Canvas.Focus();
                break;
            case Key.Enter when vm.IsFormTool && vm.SelectedField is not null && !vm.PreviewMode:
                FieldNameBox.Focus();
                FieldNameBox.SelectAll();
                break;
            case Key.PageDown: vm.NextPageCommand.Execute(null); break;
            case Key.PageUp: vm.PreviousPageCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>⌘/Ctrl + wheel (and trackpad pinch on macOS) zooms; plain wheel scrolls.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(CommandKey) && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Delta.Y != 0) ZoomAround(ViewModel.Zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1), e.GetPosition(PageScroller));
        e.Handled = true;
    }

    private double? _pinchStartZoom;

    private void OnPinch(object? sender, PinchEventArgs e)
    {
        _pinchStartZoom ??= ViewModel.Zoom;
        var origin = PageScroller.Bounds.Width > 0
            ? new Point(e.ScaleOrigin.X * PageScroller.Bounds.Width, e.ScaleOrigin.Y * PageScroller.Bounds.Height)
            : default;
        ZoomAround(_pinchStartZoom.Value * e.Scale, origin);
        e.Handled = true;
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAround(ViewModel.Zoom * (1 + e.Delta.X), e.GetPosition(PageScroller));
        e.Handled = true;
    }

    /// <summary>Zooms so the page point under <paramref name="at"/> (viewport coordinates) stays put.</summary>
    public void ZoomAround(double zoom, Point at)
    {
        double before = ViewModel.Zoom;
        ViewModel.SetZoom(zoom);
        double k = ViewModel.Zoom / before;
        if (k == 1) return;
        var target = new Vector((PageScroller.Offset.X + at.X) * k - at.X, (PageScroller.Offset.Y + at.Y) * k - at.Y);
        // after the page has its new size
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            PageScroller.Offset = new Vector(Math.Max(0, target.X), Math.Max(0, target.Y)), Avalonia.Threading.DispatcherPriority.Render);
    }

    // ------------------------------------------------------------------ drag and drop
    private static IEnumerable<string> DroppedPdfs(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedPdfs(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = DroppedPdfs(e).ToList();
        e.Handled = true;
        if (files.Count > 0) await ViewModel.DropAsync(files);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettings("general");

    /// <summary>Opens the Settings page (in this window) at a section: "general", "ai", "mcp" or "about".</summary>
    public void ShowSettings(string section)
    {
        ViewModel.ActiveTool = ViewModels.Tool.Settings;
        if (SettingsPage.DataContext is not SettingsViewModel) SettingsPage.DataContext = ViewModel.Settings;
        SettingsPage.ShowSection(section);
    }

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PageThumb t }) ViewModel.GoToPageCommand.Execute(t);
    }
}
