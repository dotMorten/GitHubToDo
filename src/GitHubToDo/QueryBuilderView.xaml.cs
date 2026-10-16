using GitHubToDo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GitHubToDo;

public sealed partial class QueryBuilderView : UserControl
{
    public QueryBuilderView(QueryBuilderViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AiIcon.Data = Octicons.Geometry(Octicons.Sparkle);
        GenerateIcon.Data = Octicons.Geometry(Octicons.Sparkle);
    }

    public QueryBuilderViewModel ViewModel { get; }

    public void FocusInitial()
    {
        Control target = ViewModel.IsAiAvailable && !ViewModel.IsEditing ? DescriptionBox : NameBox;
        target.Focus(FocusState.Programmatic);
    }

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility NotNull(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;
    public static string QualifierLabel(string name) => name + ":";
    public static string QualifierId(string name) => "Qualifier_" + name;
    public static string AttemptName(string header, string expression, string outcome) => $"{header}: {expression}. {outcome}";
    public static string ValidationGlyph(bool hasIssues) => hasIssues ? "\uE7BA" : "\uE73E";

    public static Brush ValidationBrush(bool hasIssues) => OutcomeBrush(!hasIssues, warning: true);

    public static Brush OutcomeBrush(bool succeeded) => OutcomeBrush(succeeded, warning: false);

    private static Brush OutcomeBrush(bool succeeded, bool warning) => (Brush)Application.Current.Resources[
        succeeded ? "SystemFillColorSuccessBrush" : warning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush"];

    private bool _isNarrow;

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        if (narrow == _isNarrow && e.PreviousSize.Width > 0) return;
        _isNarrow = narrow;
        ReferenceColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Root.ColumnSpacing = narrow ? 0 : 20;
        Grid.SetColumn(ReferencePanel, narrow ? 0 : 1);
        PaneSelector.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanes();
    }

    private void PaneSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => UpdatePanes();

    private void UpdatePanes()
    {
        var showCompose = !_isNarrow || PaneSelector.SelectedItem == ComposePaneItem;
        ComposePanel.Visibility = showCompose ? Visibility.Visible : Visibility.Collapsed;
        ReferencePanel.Visibility = !_isNarrow || !showCompose ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Example_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string example }) ViewModel.InsertExampleCommand.Execute(example);
    }

    private void QualifierList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ViewModel.AddQualifierCommand.Execute(null);

    private void QualifierChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Switching to a qualifier without choices empties the list; don't let that wipe the typed value.
        if (((ComboBox)sender).SelectedItem is string choice) ViewModel.QualifierValue = choice;
    }

    private void QualifierList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Filtering clears the list selection; only user picks should change the documented qualifier.
        if (((ListView)sender).SelectedItem is GitHubToDo.Core.QualifierDoc doc) ViewModel.SelectedQualifier = doc;
    }

    private void QualifierValue_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.AddQualifierCommand.Execute(null);
    }

    private void Description_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (ViewModel.GenerateCommand.CanExecute(null)) ViewModel.GenerateCommand.Execute(null);
    }
}
