using System.ComponentModel;
using System.Collections.Specialized;
using CommunityToolkit.WinUI.UI.Controls;
using GitHubToDo.Core;
using GitHubToDo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GitHubToDo;

public sealed partial class ItemDetailsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ShellViewModel), typeof(ItemDetailsView),
        new PropertyMetadata(null, (d, _) => ((ItemDetailsView)d).OnViewModelChanged()));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private ShellViewModel? _attachedViewModel;
    private bool _isDiscussionResetQueued;
    private bool _discussionPositionPending;
    private ScrollViewer? _discussionViewer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _commentReadTimer;
    private ItemReadSnapshot? _scrollReadSnapshot;
    private readonly HashSet<string> _reachedCommentIds = [];
    private double _previousDiscussionOffset;
    private bool _isDirectManipulation;
    private readonly HashSet<(string Item, string Activity)> _pendingCommentReads = [];

    public ItemDetailsView()
    {
        InitializeComponent();
        _commentReadTimer = DispatcherQueue.CreateTimer();
        _commentReadTimer.Interval = TimeSpan.FromMilliseconds(180);
        _commentReadTimer.IsRepeating = false;
        _commentReadTimer.Tick += async (_, _) => await CommitScrollReadAsync();
        DetailsScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(DetailsScroll_PointerWheelChanged), true);
        SummaryHeaderIcon.Data = Octicons.Geometry(Octicons.Sparkle);
        EmptyDetailsIcon.Data = Octicons.Geometry(Octicons.Inbox);
        Loaded += (_, _) => AttachViewModel();
        Unloaded += (_, _) =>
        {
            DetachViewModel();
            CancelScrollReading();
            DetachDiscussionViewer();
        };
    }

    public void FocusDiscussion() => DetailsScroll.Focus(FocusState.Keyboard);

    private void SummaryButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is AppBarButton { Icon: PathIcon icon })
            icon.Data = Octicons.Geometry(Octicons.Sparkle);
    }

    private void OnViewModelChanged()
    {
        CancelScrollReading();
        DetachViewModel();
        Bindings.Update();
        if (IsLoaded) AttachViewModel();
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        _attachedViewModel = ViewModel;
        if (_attachedViewModel is null) return;
        _attachedViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _attachedViewModel.PreviewActivities.CollectionChanged += PreviewActivities_CollectionChanged;
        _attachedViewModel.ExplicitReadStateChanging += CancelScrollReading;
        _attachedViewModel.UnreadRestored += RestoreUnreadPosition;
        RestoreUnreadPosition();
    }

    private void DetachViewModel()
    {
        if (_attachedViewModel is not { } viewModel) return;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.PreviewActivities.CollectionChanged -= PreviewActivities_CollectionChanged;
        viewModel.ExplicitReadStateChanging -= CancelScrollReading;
        viewModel.UnreadRestored -= RestoreUnreadPosition;
        _attachedViewModel = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.IsSummaryOpen))
        {
            // Pane reflow is not a reading gesture. Do not let a previously armed scroll
            // observation acknowledge comments just because summary generation changed the layout.
            CancelScrollReading();
            if (ViewModel.IsSummaryOpen)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.IsSummaryOpen) CloseSummaryButton.Focus(FocusState.Programmatic);
                });
        }
        if (args.PropertyName == nameof(ShellViewModel.SelectedItem)) RestoreUnreadPosition();
    }

    private async void Markdown_LinkClicked(object? sender, LinkClickedEventArgs e) =>
        await ViewModel.OpenMarkdownLinkAsync(e.Link);

    private void Markdown_ImageResolving(object? sender, ImageResolvingEventArgs e)
    {
        // Do not fetch tracking images, local files, SVGs, or authenticated content
        // simply because the user selected an item. Null suppresses the image.
        e.Handled = true;
        e.Image = null;
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SummaryLayout_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Preserve a useful discussion column; on smaller windows use the same right-hand pane
        // as an overlay rather than squeezing or clipping the thread.
        CancelScrollReading();
        // Keep the same pane alive across the breakpoint. SplitView's native light-dismiss
        // transition can asynchronously close a pane while switching Inline/Overlay modes.
        // A Grid placement change cannot dismiss it or cancel a running summary on resize.
        var isInline = args.NewSize.Width >= 800;
        Grid.SetColumn(SummaryPaneBorder, isInline ? 1 : 0);
        SummaryPaneBorder.HorizontalAlignment = isInline ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        SummaryPaneBorder.Width = Math.Max(0, Math.Min(360, args.NewSize.Width));
    }

    private void SummaryPane_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && ViewModel.IsSummaryOpen)
        {
            ViewModel.CloseSummaryCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void CancelScrollReading()
    {
        _commentReadTimer.Stop();
        _scrollReadSnapshot = null;
        _reachedCommentIds.Clear();
    }

    private void RestoreUnreadPosition()
    {
        CancelScrollReading();
        _discussionPositionPending = true;
        QueueDiscussionReset();
    }

    private void QueueDiscussionReset()
    {
        if (_isDiscussionResetQueued || !_discussionPositionPending) return;
        _isDiscussionResetQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isDiscussionResetQueued = false;
            if (ViewModel.SelectedItem is { Item.HasBaseline: false }) return;
            var target = ViewModel.PreviewActivities.FirstOrDefault(row => row.IsUnread) ??
                (ViewModel.SelectedQuery?.IsSavedQuery == false ? ViewModel.PreviewActivities.FirstOrDefault(row => row.IsFlagged) : null) ??
                ViewModel.PreviewActivities.FirstOrDefault();
            if (target is null)
            {
                DiscussionHeader.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false, VerticalAlignmentRatio = 0,
                    TargetRect = new Windows.Foundation.Rect(0, 0, 1, 1)
                });
                _discussionPositionPending = false;
                return;
            }
            DetailsScroll.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            DetailsScroll.UpdateLayout();
            if (DetailsScroll.ContainerFromItem(target) is FrameworkElement container)
                container.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false, VerticalAlignmentRatio = 0,
                    TargetRect = new Windows.Foundation.Rect(0, 0, 1, 1)
                });
            _discussionPositionPending = false;
        })) _isDiscussionResetQueued = false;
    }

    private void PreviewActivities_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueDiscussionReset();

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private void DetailsScroll_Loaded(object sender, RoutedEventArgs e)
    {
        DetachDiscussionViewer();
        _discussionViewer = VisualDescendants<ScrollViewer>(DetailsScroll).FirstOrDefault();
        if (_discussionViewer is null) return;
        _previousDiscussionOffset = _discussionViewer.VerticalOffset;
        _discussionViewer.ViewChanged += DiscussionViewer_ViewChanged;
        _discussionViewer.DirectManipulationStarted += DiscussionViewer_DirectManipulationStarted;
        _discussionViewer.DirectManipulationCompleted += DiscussionViewer_DirectManipulationCompleted;
        foreach (var bar in VisualDescendants<ScrollBar>(_discussionViewer))
            if (bar.Orientation == Orientation.Vertical) bar.Scroll += DiscussionScrollbar_Scroll;
    }

    private void DetachDiscussionViewer()
    {
        if (_discussionViewer is null) return;
        _discussionViewer.ViewChanged -= DiscussionViewer_ViewChanged;
        _discussionViewer.DirectManipulationStarted -= DiscussionViewer_DirectManipulationStarted;
        _discussionViewer.DirectManipulationCompleted -= DiscussionViewer_DirectManipulationCompleted;
        foreach (var bar in VisualDescendants<ScrollBar>(_discussionViewer))
            bar.Scroll -= DiscussionScrollbar_Scroll;
        _discussionViewer = null;
    }

    private void DiscussionViewer_DirectManipulationStarted(object? sender, object args) => _isDirectManipulation = true;
    private void DiscussionViewer_DirectManipulationCompleted(object? sender, object args) => _isDirectManipulation = false;

    private void DiscussionScrollbar_Scroll(object sender, ScrollEventArgs args)
    {
        if (args.NewValue > _previousDiscussionOffset) ArmCommentReading();
    }

    private void DetailsScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var properties = args.GetCurrentPoint(DetailsScroll).Properties;
        if (!properties.IsHorizontalMouseWheel && properties.MouseWheelDelta < 0) ArmCommentReading();
    }

    private void DetailsScroll_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Space)
            for (var source = args.OriginalSource as DependencyObject; source is not null; source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase) return;
        if (args.Key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.PageDown or Windows.System.VirtualKey.End ||
            (args.Key == Windows.System.VirtualKey.Space && !IsKeyDown(Windows.System.VirtualKey.Shift)))
            ArmCommentReading();
    }

    private void ArmCommentReading()
    {
        if (_discussionPositionPending || _isDiscussionResetQueued || ViewModel.IsSettingsOpen) return;
        _scrollReadSnapshot ??= ViewModel.PreviewReadThrough(ViewModel.PreviewActivities.Count - 1);
        if (_scrollReadSnapshot is null) return;
        ObserveCommentBottoms();
        _commentReadTimer.Stop();
        _commentReadTimer.Start();
    }

    private void DiscussionViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_discussionViewer is not { } viewer) return;
        if (_isDirectManipulation && viewer.VerticalOffset > _previousDiscussionOffset) ArmCommentReading();
        if (_scrollReadSnapshot is not null && !_discussionPositionPending && viewer.VerticalOffset >= _previousDiscussionOffset)
        {
            ObserveCommentBottoms();
            _commentReadTimer.Stop();
            _commentReadTimer.Start();
        }
        _previousDiscussionOffset = viewer.VerticalOffset;
    }

    private void ObserveCommentBottoms()
    {
        if (_discussionViewer is not { ViewportHeight: > 0 } viewer || DetailsScroll.ItemsPanelRoot is not { } panel) return;
        var reachedIndex = -1;
        foreach (var container in panel.Children.OfType<ListViewItem>())
        {
            if (container.Content is not ActivityRow row || container.ActualHeight <= 0) continue;
            var bottom = container.TransformToVisual(viewer).TransformPoint(new Windows.Foundation.Point(0, container.ActualHeight)).Y;
            if (bottom <= viewer.ViewportHeight + 0.5)
                reachedIndex = Math.Max(reachedIndex, ViewModel.PreviewActivities.IndexOf(row));
        }
        if (ViewModel.PreviewReadThrough(reachedIndex) is { } reached)
            _reachedCommentIds.UnionWith(reached.ActivityIds);
    }

    private async Task CommitScrollReadAsync()
    {
        var observed = _scrollReadSnapshot;
        var reached = _reachedCommentIds.ToHashSet();
        _scrollReadSnapshot = null;
        _reachedCommentIds.Clear();
        if (_discussionPositionPending || ViewModel.IsSettingsOpen || observed is null ||
            ViewModel.SelectedItem?.Id != observed.ItemId) return;
        var current = ViewModel.PreviewActivities.Where(row => row.IsUnread).Select(row => row.Id).ToHashSet();
        var ids = observed.ActivityIds.Where(id => reached.Contains(id) && current.Contains(id) &&
            _pendingCommentReads.Add((observed.ItemId, id))).ToArray();
        if (ids.Length == 0) return;
        try { await ViewModel.MarkCommentsReadAsync(new(observed.ItemId, ids)); }
        finally { foreach (var id in ids) _pendingCommentReads.Remove((observed.ItemId, id)); }
    }

    private async void CommentFollowUp_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { DataContext: ActivityRow row } button || ViewModel.SelectedItem is not { } item) return;
        await ViewModel.SetCommentFollowUpAsync(item.Id, row.Id, button.IsChecked == true);
        if (ReferenceEquals(button.DataContext, row)) button.IsChecked = row.IsFlagged;
    }

    private async void CommentReadState_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ActivityRow row } || ViewModel.SelectedItem is not { } item) return;
        await ViewModel.SetCommentReadStateAsync(item.Id, row.Id, row.IsUnread);
    }
}
