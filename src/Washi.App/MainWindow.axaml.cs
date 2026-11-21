// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Data;
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
    }
}
