// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Washi.App.Services;
using Washi.App.ViewModels;

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
        ViewModel = new MainViewModel(dialogs ?? new AvaloniaDialogs(this));
        DataContext = ViewModel;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        foreach (var rail in new[] { FormToolButton, OcrToolButton, CombineToolButton, SplitToolButton, AskToolButton })
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
        QuestionBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && ViewModel.Ask.RunCommand.CanExecute(null))
            {
                ViewModel.Ask.RunCommand.Execute(null);
                e.Handled = true;
            }
        };
        // the field list follows the selection, so Tab through fields keeps the row in view
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedField) && ViewModel.SelectedField is { } f)
                FieldList.ScrollIntoView(f);
        };
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
        if (typing || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift) || !vm.HasDocument) return;

        switch (e.Key)
        {
            case Key.V when vm.IsFormTool: vm.SelectModeCommand.Execute(null); break;
            case Key.F when vm.IsFormTool: vm.DrawFieldModeCommand.Execute(null); break;
            case Key.Tab when vm.IsFormTool:
                vm.SelectNextField(backwards: e.KeyModifiers == KeyModifiers.Shift);
                Canvas.Focus();
                break;
            case Key.Enter when vm.IsFormTool && vm.SelectedField is not null:
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

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => (Application.Current as App)?.ShowSettings(about: false);

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PageThumb t }) ViewModel.GoToPageCommand.Execute(t);
    }
}
