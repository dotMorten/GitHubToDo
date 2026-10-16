using System.ComponentModel;
using System.Collections.Specialized;
using CommunityToolkit.WinUI.UI.Controls;
using GitHubToDo.Core;
using GitHubToDo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls.Tabular;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GitHubToDo;

public sealed partial class MainPage : Page
{
    private bool _isUpdatingNavigation;
    private bool _isNavigationTransition;
    private bool _isNavigationRestoreQueued;
    private bool? _isNarrowNavigation;
    private bool _isDialogOpen;
    private readonly Dictionary<string, QueryNavigationEntry> _queryNavigation = [];
    private readonly NavigationViewItemHeader _savedSearchesHeader = new() { Content = "Saved searches" };
    private readonly NavigationViewItemSeparator _followUpSeparator = new();
    private static readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> s_avatars = new(StringComparer.OrdinalIgnoreCase);

    private sealed record QueryNavigationEntry(NavigationViewItem Item, TextBlock Name, Border Pill, TextBlock Badge)
    {
        public string? IconExpression { get; set; }
    }
    private bool _isUpdatingItemSelection;
    private bool _isItemSelectionQueued;
    private bool _isDiscussionResetQueued;
    private bool _discussionPositionPending;
    private ScrollViewer? _discussionViewer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _commentReadTimer;
    private ItemReadSnapshot? _scrollReadSnapshot;
    private readonly HashSet<string> _reachedCommentIds = [];
    private double _previousDiscussionOffset;
    private bool _isDirectManipulation;
    private readonly HashSet<(string Item, string Activity)> _pendingCommentReads = [];
    private readonly Dictionary<TableViewRow, (FrameworkElement Root, long Token)> _rowAutomation = [];
    private ItemRow? _selectionAnchor;
    private bool _selectionChangedSincePress;
    public ShellViewModel ViewModel { get; } = new(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

    public MainPage()
    {
        InitializeComponent();
        _commentReadTimer = DispatcherQueue.CreateTimer();
        _commentReadTimer.Interval = TimeSpan.FromMilliseconds(180);
        _commentReadTimer.IsRepeating = false;
        _commentReadTimer.Tick += async (_, _) => await CommitScrollReadAsync();
        DetailsScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(DetailsScroll_PointerWheelChanged), true);
        SummarizeIcon.Data = Octicons.Geometry(Octicons.Sparkle);
        SummaryHeaderIcon.Data = Octicons.Geometry(Octicons.Sparkle);
        EmptyListIcon.Data = Octicons.Geometry(Octicons.CheckCircle);
        EmptyDetailsIcon.Data = Octicons.Geometry(Octicons.Inbox);
        ThemePicker.SelectedIndex = Services.ThemeService.Load() switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
        InitializeTableLayout();
        AutomationProperties.SetAutomationId(_savedSearchesHeader, "SavedSearchesHeader");
        AutomationProperties.SetName(_savedSearchesHeader, "Saved searches");
        ViewModel.QueriesChanged += UpdateNavigation;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        ViewModel.ItemProjectionStarting += QueueItemSelection;
        ViewModel.PreviewActivities.CollectionChanged += PreviewActivities_CollectionChanged;
        ViewModel.ExplicitReadStateChanging += CancelScrollReading;
        ViewModel.UnreadRestored += RestoreUnreadPosition;
        Unloaded += (_, _) =>
        {
            ViewModel.QueriesChanged -= UpdateNavigation;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Items.CollectionChanged -= Items_CollectionChanged;
            ViewModel.ItemProjectionStarting -= QueueItemSelection;
            ViewModel.PreviewActivities.CollectionChanged -= PreviewActivities_CollectionChanged;
            ViewModel.ExplicitReadStateChanging -= CancelScrollReading;
            ViewModel.UnreadRestored -= RestoreUnreadPosition;
            _commentReadTimer.Stop();
            DetachDiscussionViewer();
            DetachRowAutomation();
            ViewModel.Dispose();
        };
    }

    public static bool Not(bool value) => !value;
    public static Windows.UI.Text.FontWeight UnreadWeight(bool unread) =>
        unread ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public static bool CanConfigure(bool signedIn, bool deviceFlow) => !signedIn && !deviceFlow;
    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility CollapsedWhen(bool value) => Visible(!value);
    public static Symbol ReadActionIcon(bool unread) => unread ? Symbol.Accept : Symbol.Mail;
    public static Visibility TextVisible(string? value) => Visible(HasText(value));
    public static Visibility WorkspaceVisible(bool settings, bool signedIn) => Visible(!settings && signedIn);
    public static string CountLabel(int count, string noun) => $"{count:N0} {noun}";
    public static Visibility SummaryVisible(bool summarizing, string? status, string? summary) =>
        Visible(summarizing || HasText(status) || HasText(summary));
    public static Brush TimeBrush(bool unread) => (Brush)Application.Current.Resources[
        unread ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
    public static string SummaryTitle(bool failed) => failed ? "Couldn't summarize" : "Catch-up summary";
    public static Brush SummaryAccentBrush(bool failed) => (Brush)Application.Current.Resources[
        failed ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush"];
    public static Brush SummaryStatusBrush(bool failed) => (Brush)Application.Current.Resources[
        failed ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];

    public static ImageSource? Avatar(string? login)
    {
#if DEBUG
        if (App.IsDemo) return null;
#endif
        if (string.IsNullOrWhiteSpace(login)) return null;
        var name = login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? login[..^5] : login;
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        if (!s_avatars.TryGetValue(name, out var image))
        {
            image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"https://github.com/{name}.png?size=64"))
            {
                DecodePixelWidth = 64, DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical
            };
            s_avatars[name] = image;
        }
        return image;
    }

    public UIElement AppTitleBarElement => AppTitleBar;

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ItemFilterBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateNavigationLayout(ActualWidth);
        App.Window.Closed += (_, _) => ViewModel.Dispose();
        await ViewModel.InitializeAsync();
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        _isUpdatingNavigation = true;
        try
        {
            var ids = ViewModel.Queries.Select(row => row.Id).ToHashSet();
            foreach (var id in _queryNavigation.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                Navigation.MenuItems.Remove(_queryNavigation[id].Item);
                _queryNavigation.Remove(id);
            }
            var desired = new List<object> { _savedSearchesHeader };
            var separatorAdded = false;
            foreach (var row in ViewModel.Queries)
            {
                if (!row.IsSavedQuery && !separatorAdded)
                {
                    desired.Add(_followUpSeparator);
                    separatorAdded = true;
                }
                var entry = GetQueryNavigationEntry(row);
                UpdateQueryNavigationEntry(entry, row);
                desired.Add(entry.Item);
            }
            for (var index = 0; index < desired.Count; index++)
            {
                var oldIndex = Navigation.MenuItems.IndexOf(desired[index]);
                if (oldIndex == index) continue;
                if (oldIndex >= 0) Navigation.MenuItems.RemoveAt(oldIndex);
                Navigation.MenuItems.Insert(index, desired[index]);
            }
            while (Navigation.MenuItems.Count > desired.Count) Navigation.MenuItems.RemoveAt(Navigation.MenuItems.Count - 1);
            if (Navigation.SettingsItem is DependencyObject settings)
                AutomationProperties.SetAutomationId(settings, "NavigationSettings");
            RestoreLogicalNavigationSelection();
        }
        finally { _isUpdatingNavigation = false; }
    }

    private QueryNavigationEntry GetQueryNavigationEntry(QueryRow row)
    {
        if (_queryNavigation.TryGetValue(row.Id, out var entry)) return entry;
        var panel = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var badge = new TextBlock();
        var pill = new Border { Child = badge, Margin = new Thickness(0, 0, 4, 0) };
        Grid.SetColumn(pill, 1);
        AutomationProperties.SetAutomationId(name, $"QueryName_{row.Id}");
        AutomationProperties.SetAutomationId(badge, $"QueryBadge_{row.Id}");
        panel.Children.Add(name);
        panel.Children.Add(pill);
        var item = new NavigationViewItem
        {
            Content = panel, Tag = row.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 40,
            Icon = row.IsSavedQuery ? null : new FontIcon { Glyph = "\uE7C1", Foreground = (Brush)Application.Current.Resources["FollowUpFlagBrush"] }
        };
        if (row.IsSavedQuery)
        {
            var edit = new MenuFlyoutItem { Text = "Edit query", Icon = new SymbolIcon(Symbol.Edit), Tag = row.Id };
            var remove = new MenuFlyoutItem { Text = "Remove query", Icon = new SymbolIcon(Symbol.Delete), Tag = row.Id };
            AutomationProperties.SetAutomationId(edit, "QueryContextEdit");
            AutomationProperties.SetAutomationId(remove, "QueryContextRemove");
            edit.Click += EditQuery_Click;
            remove.Click += DeleteQuery_Click;
            var menu = new MenuFlyout();
            menu.Items.Add(edit);
            menu.Items.Add(remove);
            item.ContextFlyout = menu;
        }
        AutomationProperties.SetAutomationId(item, $"Query_{row.Id}");
        entry = new QueryNavigationEntry(item, name, pill, badge);
        _queryNavigation.Add(row.Id, entry);
        return entry;
    }

    private void UpdateQueryNavigationEntry(QueryNavigationEntry entry, QueryRow row)
    {
        if (row.IsSavedQuery && entry.IconExpression != row.Query.Expression)
        {
            entry.Item.Icon = Octicons.Icon(Octicons.ForQuery(row.Query.Expression));
            entry.IconExpression = row.Query.Expression;
        }
        entry.Name.Text = row.Name;
        entry.Name.FontWeight = UnreadWeight(row.IsUnread);
        entry.Badge.Text = row.PendingCount > 0 ? $"{row.UnreadCount} · …" : row.UnreadCount.ToString();
        var accent = row.IsUnread;
        entry.Pill.Style = (Style)Resources[accent ? "QueryPillUnreadStyle" : "QueryPillPendingStyle"];
        entry.Badge.Style = (Style)Resources[accent ? "QueryPillTextUnreadStyle" : "QueryPillTextPendingStyle"];
        // Keep zero-count badges in the automation tree, but visually quiet.
        entry.Pill.Opacity = row.IsUnread || row.PendingCount > 0 ? 1 : 0;
        ToolTipService.SetToolTip(entry.Item, row.IsSavedQuery
            ? $"{row.Name}\n{row.Query.Expression}\n{row.Badge}"
            : $"{row.Name}\n{row.Badge}");
        AutomationProperties.SetName(entry.Item, $"{row.Name}, {row.UnreadCount} unread, {row.PendingCount} pending");
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Template/display-mode changes can emit temporary settings/null selections.
        // Only stable selection changes (including UIA SelectionItem.Select) navigate.
        if (_isUpdatingNavigation || _isNavigationTransition) return;
        if (args.IsSettingsSelected) { ViewModel.IsSettingsOpen = true; return; }
        if (args.SelectedItem is NavigationViewItem { Tag: string id })
        {
            ViewModel.SelectedQuery = ViewModel.Queries.FirstOrDefault(q => q.Id == id);
            ViewModel.IsSettingsOpen = false;
            if (Navigation.DisplayMode != NavigationViewDisplayMode.Expanded) Navigation.IsPaneOpen = false;
        }
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateNavigationLayout(args.NewSize.Width);

    private void UpdateNavigationLayout(double width)
    {
        if (width <= 0 || Navigation is null) return;
        var isNarrow = width < 1008;
        if (_isNarrowNavigation == isNarrow) return;
        _isNarrowNavigation = isNarrow;
        _isNavigationTransition = true;
        // Text-only query entries have no useful compact/icon representation.
        // A closed overlay pane keeps all query labels at their full pane width.
        Navigation.PaneDisplayMode = isNarrow
            ? NavigationViewPaneDisplayMode.LeftMinimal
            : NavigationViewPaneDisplayMode.Left;
        Navigation.IsPaneOpen = !isNarrow;
        RestoreLogicalNavigationSelection();
        QueueNavigationRestore();
    }

    private void Navigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        _isNavigationTransition = true;
        QueueNavigationRestore();
    }

    private void QueueNavigationRestore()
    {
        if (_isNavigationRestoreQueued) return;
        _isNavigationRestoreQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isNavigationRestoreQueued = false;
            RestoreLogicalNavigationSelection();
            _isNavigationTransition = false;
        }))
        {
            _isNavigationRestoreQueued = false;
            _isNavigationTransition = false;
        }
    }

    private void RestoreLogicalNavigationSelection()
    {
        var wasUpdating = _isUpdatingNavigation;
        _isUpdatingNavigation = true;
        try
        {
            Navigation.SelectedItem = ViewModel.IsSettingsOpen
                ? Navigation.SettingsItem
                : Navigation.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(item => item.Tag as string == ViewModel.SelectedQuery?.Id);
        }
        finally { _isUpdatingNavigation = wasUpdating; }
    }

    private async void AddQuery_Click(object sender, RoutedEventArgs e) => await EditQueryAsync(null);
    private QueryRow? QueryActionTarget(object sender) => sender is MenuFlyoutItem { Tag: string id }
        ? ViewModel.Queries.FirstOrDefault(query => query.Id == id)
        : ViewModel.SelectedQuery;

    private async void EditQuery_Click(object sender, RoutedEventArgs e)
    {
        if (QueryActionTarget(sender) is { IsSavedQuery: true } row) await EditQueryAsync(row.Query);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<ElementTheme>(tag, out var theme)) return;
        Services.ThemeService.Save(theme);
        if (App.Window is { } window) Services.ThemeService.Apply(window, theme);
    }

    private async Task EditQueryAsync(SavedQuery? query)
    {
        if (_isDialogOpen || !ViewModel.IsSignedIn) return;
        _isDialogOpen = true;
        using var builder = new QueryBuilderViewModel(ViewModel, query);
        try
        {
            var view = new QueryBuilderView(builder)
            {
                Width = Math.Clamp(XamlRoot.Size.Width - 120, 300, 1000),
                Height = Math.Clamp(XamlRoot.Size.Height - 230, 300, 660)
            };
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, Margin = new Thickness(0, 12, 0, 0) };
            AutomationProperties.SetAutomationId(error, "QueryEditorError");
            var panel = new StackPanel();
            panel.Children.Add(view);
            panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = query is null ? "New saved search" : $"Edit “{query.Name}”",
                Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, RequestedTheme = Services.ThemeService.Load()
            };
            dialog.Resources["ContentDialogMaxWidth"] = 1100.0;
            dialog.Resources["ContentDialogMaxHeight"] = 1000.0;
            AutomationProperties.SetAutomationId(dialog, "QueryEditor");
            dialog.Opened += (_, _) =>
            {
                view.FocusInitial();
                builder.Start();
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (builder.GetSaveError() is { } problem) throw new ArgumentException(problem);
                    await ViewModel.SaveQueryAsync(query?.Id, builder.Name.Trim(), builder.Expression);
                    ViewModel.UpdateProjection();
                    ViewModel.IsSettingsOpen = false;
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    // Make room so the dialog keeps its size instead of growing past the window.
                    if (!error.IsOpen) view.Height = Math.Max(240, view.Height - 64);
                    error.Message = ex.Message;
                    error.IsOpen = true;
                }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        finally { _isDialogOpen = false; }
    }

    private async void DeleteQuery_Click(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen || QueryActionTarget(sender) is not { IsSavedQuery: true } query) return;
        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = $"Delete “{query.Name}”?",
                Content = "Only this saved query and its membership will be removed. Item read history is retained.",
                PrimaryButtonText = "Delete query", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                RequestedTheme = Services.ThemeService.Load()
            };
            AutomationProperties.SetAutomationId(dialog, "ConfirmDeleteQuery");
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.RunAsync(() => ViewModel.DeleteQueryAsync(query.Id));
        }
        finally { _isDialogOpen = false; }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveQueryAsync(-1);
    private async void MoveDown_Click(object sender, RoutedEventArgs e) => await ViewModel.MoveQueryAsync(1);
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSettingsOpen = true;
        RestoreLogicalNavigationSelection();
    }
    private void BackToQueries_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedQuery ??= ViewModel.Queries.FirstOrDefault();
        ViewModel.IsSettingsOpen = false;
        RestoreLogicalNavigationSelection();
        if (_isNarrowNavigation == true) Navigation.IsPaneOpen = false;
    }
    private void FocusDetails_Click(object sender, RoutedEventArgs e) => DetailsScroll.Focus(FocusState.Keyboard);
    private void FocusList_Click(object sender, RoutedEventArgs e) => ItemList.Focus(FocusState.Keyboard);
    private async void Markdown_LinkClicked(object? sender, LinkClickedEventArgs e) =>
        await ViewModel.OpenMarkdownLinkAsync(e.Link);

    private void Markdown_ImageResolving(object? sender, ImageResolvingEventArgs e)
    {
        // Do not fetch tracking images, local files, SVGs, or authenticated content
        // simply because the user selected an item. Null suppresses the image.
        e.Handled = true;
        e.Image = null;
    }

    private void ItemList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RowFromSource(e.OriginalSource) is not { } row) return;
        // Right-clicking inside a multi-selection keeps it, like Outlook and Explorer.
        if (!row.IsSelected)
        {
            _selectionAnchor = row;
            ViewModel.SetItemSelection([row], row);
        }
        SynchronizeItemSelection();
    }

    private static ItemRow? RowFromSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: ItemRow row }) return row;
        return null;
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ItemCell_Loaded(object sender, RoutedEventArgs e) => AttachRowAutomation((FrameworkElement)sender);
    private void ItemCell_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => AttachRowAutomation(sender);

    private void AttachRowAutomation(FrameworkElement cellRoot)
    {
        // TableView rows have no item-container hook, so the first cell names its
        // recycled row for UI Automation and paints app-managed multi-selection.
        var parent = VisualTreeHelper.GetParent(cellRoot);
        while (parent is not null && parent is not TableViewRow) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not TableViewRow tableRow) return;
        if (!_rowAutomation.TryGetValue(tableRow, out var existing) || !ReferenceEquals(existing.Root, cellRoot))
        {
            existing.Root?.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, existing.Token);
            var token = cellRoot.RegisterPropertyChangedCallback(AutomationProperties.NameProperty,
                (element, _) => AutomationProperties.SetName(tableRow, AutomationProperties.GetName(element)));
            _rowAutomation[tableRow] = (cellRoot, token);
        }
        var row = cellRoot.DataContext as ItemRow;
        AutomationProperties.SetAutomationId(tableRow, row?.AutomationId ?? "");
        AutomationProperties.SetName(tableRow, row?.AccessibleName ?? "");
        UpdateRowHighlight(tableRow, row);
    }

    private void DetachRowAutomation()
    {
        foreach (var (_, entry) in _rowAutomation)
            entry.Root.UnregisterPropertyChangedCallback(AutomationProperties.NameProperty, entry.Token);
        _rowAutomation.Clear();
    }

    private void UpdateRowHighlight(TableViewRow tableRow, ItemRow? row)
    {
        var extra = row is { IsSelected: true } && !ReferenceEquals(ItemList.SelectedItem, row);
        if (extra) tableRow.Style = (Style)Resources["MultiSelectedTableRowStyle"];
        else if (tableRow.Style is not null) tableRow.ClearValue(StyleProperty);
    }

    private void UpdateRowHighlights()
    {
        foreach (var (tableRow, entry) in _rowAutomation)
        {
            var row = entry.Root.DataContext as ItemRow;
            UpdateRowHighlight(tableRow, row);
            if (row is not null) AutomationProperties.SetName(tableRow, row.AccessibleName);
        }
    }

    private void ItemList_PointerPressed(object sender, PointerRoutedEventArgs e) => _selectionChangedSincePress = false;

    private void ItemList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicking the natively selected row raises no SelectionChanged: Ctrl toggles it,
        // a plain click collapses a multi-selection to that row.
        if (_selectionChangedSincePress || IsKeyDown(Windows.System.VirtualKey.Shift)) return;
        if (RowFromSource(e.OriginalSource) is { } row && ReferenceEquals(row, ItemList.SelectedItem) &&
            (IsKeyDown(Windows.System.VirtualKey.Control) || ViewModel.SelectedItems.Count > 1)) ApplyPointerSelection(row);
    }

    private void ItemList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.A && IsKeyDown(Windows.System.VirtualKey.Control) && ViewModel.Items.Count > 0)
        {
            e.Handled = true;
            ViewModel.SetItemSelection(ViewModel.Items.ToList(), ViewModel.SelectedItem ?? ViewModel.Items[0]);
            SynchronizeItemSelection();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.SelectedItems.Count > 1 && ViewModel.SelectedItem is { } primary)
        {
            e.Handled = true;
            _selectionAnchor = primary;
            ViewModel.SetItemSelection([primary], primary);
            SynchronizeItemSelection();
        }
    }

    private void ItemList_SelectionChanged(TableView sender, SelectionChangedEventArgs args)
    {
        if (_isUpdatingItemSelection) return;
        _selectionChangedSincePress = true;
        if (!ViewModel.IsUpdatingItems && !_isItemSelectionQueued && ItemList.SelectedItem is null &&
            IsKeyDown(Windows.System.VirtualKey.Control) && args.RemovedItems.OfType<ItemRow>().FirstOrDefault() is { } removed)
        {
            // Ctrl-clicking the native row deselects it natively; treat it as a toggle.
            ApplyPointerSelection(removed);
            return;
        }
        if (ViewModel.IsUpdatingItems || _isItemSelectionQueued || ItemList.SelectedItem is not ItemRow row || !ViewModel.Items.Contains(row))
        {
            // Native notifications during projection updates or deselection are repaired
            // from the view model, which owns the (multi-)selection.
            QueueItemSelection();
            return;
        }
        ApplyPointerSelection(row);
    }

    private void ApplyPointerSelection(ItemRow row)
    {
        var ctrl = IsKeyDown(Windows.System.VirtualKey.Control);
        var items = ViewModel.Items;
        if (IsKeyDown(Windows.System.VirtualKey.Shift) && _selectionAnchor is { } anchor && items.IndexOf(anchor) is var from and >= 0)
        {
            var to = items.IndexOf(row);
            var range = items.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1);
            ViewModel.SetItemSelection(ctrl ? ViewModel.SelectedItems.Union(range).ToList() : range.ToList(), row);
        }
        else if (ctrl)
        {
            _selectionAnchor = row;
            var selected = ViewModel.SelectedItems.ToList();
            var wasSelected = row.IsSelected && !(selected.Count == 1 && ReferenceEquals(selected[0], row));
            if (wasSelected) selected.Remove(row);
            else if (!selected.Contains(row)) selected.Add(row);
            ViewModel.SetItemSelection(selected, wasSelected ? selected.LastOrDefault() : row);
        }
        else
        {
            _selectionAnchor = row;
            ViewModel.SetItemSelection([row], row);
        }
        SynchronizeItemSelection();
    }

    private void ItemList_Sorting(TableView sender, TableViewSortingEventArgs args)
    {
        // The view model owns ordering (refresh merges, range selection); the native sort
        // only mirrors it through a comparer, so the header shows a real sort indicator.
        if (args.Column?.SortMemberPath is not { Length: > 0 } key || args.Direction == SortDirection.None)
            ViewModel.SortItems(WorkspaceRows.DefaultSortKey, true);
        else
            ViewModel.SortItems(key, args.Direction == SortDirection.Descending);
        QueueItemSelection();
        if (_isTableLayoutReady) SaveTableLayout();
    }

    private void ApplySort(string key, bool descending)
    {
        // Programmatic SortByColumn does not raise Sorting, so order the view model first.
        ViewModel.SortItems(key, descending);
        var column = ItemList.Columns.FirstOrDefault(column => column.SortMemberPath == key);
        if (column is not null) ItemList.SortByColumn(column, descending ? SortDirection.Descending : SortDirection.Ascending);
        QueueItemSelection();
    }

    private sealed partial class ViewOrderComparer(MainPage page) : ITableViewSortComparer
    {
        public int Compare(object left, object right)
        {
            var items = page.ViewModel.Items;
            var order = items.IndexOf((ItemRow)left).CompareTo(items.IndexOf((ItemRow)right));
            // Items are already in the requested direction, but the control reverses results for descending columns.
            return page.ViewModel.ItemSortDescending ? -order : order;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ShellViewModel.SelectedItem)) return;
        QueueItemSelection();
        RestoreUnreadPosition();
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

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueItemSelection();

    private void QueueItemSelection()
    {
        if (_isItemSelectionQueued) return;
        _isItemSelectionQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _isItemSelectionQueued = false;
            SynchronizeItemSelection();
        })) _isItemSelectionQueued = false;
    }

    private void SynchronizeItemSelection()
    {
        _isUpdatingItemSelection = true;
        try
        {
            // The native TableView selects one row: the primary (previewed) item.
            var index = ViewModel.SelectedItem is { } primary ? ViewModel.Items.IndexOf(primary) : -1;
            if (index < 0) { if (ItemList.SelectedIndex >= 0) ItemList.DeselectAll(); }
            else if (ItemList.SelectedIndex != index) ItemList.Select(index);
            UpdateRowHighlights();
        }
        finally { _isUpdatingItemSelection = false; }
    }

    private const string TableLayoutKey = "ItemTableLayout";
    private bool _isTableLayoutReady;
    private readonly Dictionary<TableViewColumn, GridLength> _defaultColumnWidths = [];

    private IEnumerable<(string Key, TableViewColumn Column)> NamedColumns() =>
        ItemList.Columns.Select(column => (column.SortMemberPath, column));

    private void InitializeTableLayout()
    {
        foreach (var column in ItemList.Columns)
        {
            _defaultColumnWidths[column] = column.Width;
            column.CustomSortComparer = new ViewOrderComparer(this);
        }
        ItemList.AddHandler(PointerPressedEvent, new PointerEventHandler(ItemList_PointerPressed), true);
        ItemList.AddHandler(TappedEvent, new TappedEventHandler(ItemList_Tapped), true);
        ItemList.RegisterPropertyChangedCallback(TableView.DensityProperty, (_, _) => TableAppearance.Current.Density = ItemList.Density);
        // Shortcuts act on every selected row, like the table's context menu.
        AddSelectionShortcut(Windows.System.VirtualKey.Q, ViewModel.MarkReadCommand);
        AddSelectionShortcut(Windows.System.VirtualKey.U, ViewModel.MarkUnreadCommand);
        var sortKey = WorkspaceRows.DefaultSortKey;
        var descending = true;
        if (Windows.Storage.ApplicationData.Current.LocalSettings.Values[TableLayoutKey] is string json)
        {
            try
            {
                var layout = System.Text.Json.JsonSerializer.Deserialize(json, TableLayoutJson.Default.TableLayout);
                if (layout is not null)
                {
                    if (Enum.TryParse<TableViewDensity>(layout.Density, out var density)) ItemList.Density = density;
                    foreach (var (key, column) in NamedColumns())
                    {
                        if (layout.Widths?.TryGetValue(key, out var width) == true && width > 0)
                            column.Width = new GridLength(width);
                        if (layout.Hidden?.Contains(key) == true && key != "Title") column.Visibility = Visibility.Collapsed;
                    }
                    if (!string.IsNullOrEmpty(layout.SortKey)) { sortKey = layout.SortKey; descending = layout.SortDescending; }
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        ViewModel.SortItems(sortKey, descending);
        ItemList.Loaded += (_, _) => ApplySort(ViewModel.ItemSortKey, ViewModel.ItemSortDescending);
        UpdateViewMenu();
        _isTableLayoutReady = true;
        if (App.Window is { } window) window.Closed += (_, _) => SaveTableLayout();
    }

    private void AddSelectionShortcut(Windows.System.VirtualKey key, System.Windows.Input.ICommand command)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            if (!_isDialogOpen && command.CanExecute(null)) command.Execute(null);
        };
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        KeyboardAccelerators.Add(accelerator);
    }

    private void SaveTableLayout()
    {
        var layout = new TableLayout
        {
            Density = ItemList.Density.ToString(),
            SortKey = ViewModel.ItemSortKey,
            SortDescending = ViewModel.ItemSortDescending,
            // Only explicitly resized (pixel) widths persist; the star Title column keeps filling.
            Widths = NamedColumns().Where(entry => entry.Column.Width.IsAbsolute && entry.Column.Width != _defaultColumnWidths.GetValueOrDefault(entry.Column))
                .ToDictionary(entry => entry.Key, entry => entry.Column.Width.Value),
            Hidden = NamedColumns().Where(entry => entry.Column.Visibility == Visibility.Collapsed).Select(entry => entry.Key).ToList(),
        };
        Windows.Storage.ApplicationData.Current.LocalSettings.Values[TableLayoutKey] =
            System.Text.Json.JsonSerializer.Serialize(layout, TableLayoutJson.Default.TableLayout);
    }

    private void UpdateViewMenu()
    {
        DensityCompact.IsChecked = ItemList.Density == TableViewDensity.Compact;
        DensityStandard.IsChecked = ItemList.Density == TableViewDensity.Standard;
        DensityComfortable.IsChecked = ItemList.Density == TableViewDensity.Comfortable;
        foreach (var toggle in ColumnsMenu.Items.OfType<ToggleMenuFlyoutItem>())
            toggle.IsChecked = NamedColumns().FirstOrDefault(entry => entry.Key == (string)toggle.Tag).Column?.Visibility != Visibility.Collapsed;
    }

    private void Density_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<TableViewDensity>(tag, out var density)) ItemList.Density = density;
        UpdateViewMenu();
        SaveTableLayout();
    }

    private void ColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string key } toggle &&
            NamedColumns().FirstOrDefault(entry => entry.Key == key).Column is { } column)
            column.Visibility = toggle.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        SaveTableLayout();
    }

    private void ResetTableLayout_Click(object sender, RoutedEventArgs e)
    {
        ItemList.Density = TableViewDensity.Standard;
        foreach (var column in ItemList.Columns)
        {
            column.Width = _defaultColumnWidths.GetValueOrDefault(column, GridLength.Auto);
            column.Visibility = Visibility.Visible;
        }
        ApplySort(WorkspaceRows.DefaultSortKey, true);
        UpdateViewMenu();
        Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(TableLayoutKey);
    }
}

internal sealed class TableLayout
{
    public string? Density { get; set; }
    public string? SortKey { get; set; }
    public bool SortDescending { get; set; } = true;
    public Dictionary<string, double>? Widths { get; set; }
    public List<string>? Hidden { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(TableLayout))]
internal sealed partial class TableLayoutJson : System.Text.Json.Serialization.JsonSerializerContext;


/// <summary>Density-dependent cell layout shared by every row template (x:Bind to a static instance).</summary>
public sealed partial class TableAppearance : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public static TableAppearance Current { get; } = new();
    private TableViewDensity _density = TableViewDensity.Standard;

    public TableViewDensity Density
    {
        get => _density;
        set
        {
            if (!SetProperty(ref _density, value)) return;
            OnPropertyChanged(nameof(SecondLineVisibility));
            OnPropertyChanged(nameof(CellPadding));
        }
    }

    public Visibility SecondLineVisibility => _density == TableViewDensity.Compact ? Visibility.Collapsed : Visibility.Visible;
    public Thickness CellPadding => _density switch
    {
        TableViewDensity.Compact => new Thickness(8, 2, 8, 2),
        TableViewDensity.Comfortable => new Thickness(8, 12, 8, 12),
        _ => new Thickness(8, 6, 8, 6),
    };
}
