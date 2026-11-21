// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Washi.App.ViewModels;

namespace Washi.App.Views;

public partial class AssistantPanel : UserControl
{
    private AssistantViewModel? _vm;

    public AssistantPanel()
    {
        InitializeComponent();
        // Enter sends, Shift+Enter is a new line
        InputBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || _vm is null) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var caret = InputBox.CaretIndex;
                InputBox.Text = (InputBox.Text ?? "").Insert(caret, "\n");
                InputBox.CaretIndex = caret + 1;
            }
            else if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm = DataContext as AssistantViewModel;
            if (_vm is not null) _vm.Items.CollectionChanged += OnItemsChanged;
        };
    }

    /// <summary>Follow the newest message (also while it streams: items grow after they were added).</summary>
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems.OfType<AssistantChatItem>())
            item.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
    }
}
