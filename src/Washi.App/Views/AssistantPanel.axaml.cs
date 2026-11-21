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
            FollowLatest();
        };

        // A streaming answer grows inside one item (and its Markdown is laid out after it arrives), so follow the
        // content's height, not only added items.
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (_pinned) Scroller.ScrollToEnd();
            }
            else if (e.OffsetDelta.Y != 0) _pinned = IsAtBottom;
        };
    }

    /// <summary>Within this many pixels of the end counts as "at the bottom".</summary>
    internal const double BottomSlack = 24;

    /// <summary>
    /// Follow the newest content (after MySSH). True while the view is at the bottom; scrolling up to read clears it,
    /// scrolling back down (or sending, or a new chat) sets it again.
    /// </summary>
    private bool _pinned = true;

    public bool IsAtBottom => Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - BottomSlack;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // the user's own question (or a cleared chat) always brings the view down; other items only while following
        if (e.Action == NotifyCollectionChangedAction.Reset || e.NewItems?.OfType<UserChatItem>().Any() == true) FollowLatest();
        else if (_pinned) ScrollToEndSoon();
    }

    private void FollowLatest()
    {
        _pinned = true;
        ScrollToEndSoon();
    }

    private void ScrollToEndSoon() => Dispatcher.UIThread.Post(() => { if (_pinned) Scroller.ScrollToEnd(); }, DispatcherPriority.Background);
}
