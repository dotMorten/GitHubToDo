using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace GitHubToDo;

/// <summary>GitHub-style issue/pull request state glyph, or a filled state pill when <see cref="ShowLabel"/> is set.</summary>
public sealed partial class ItemStateIcon : UserControl
{
    public static readonly DependencyProperty StateKeyProperty = DependencyProperty.Register(
        nameof(StateKey), typeof(string), typeof(ItemStateIcon), new PropertyMetadata("", (d, _) => ((ItemStateIcon)d).Apply()));

    public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
        nameof(ShowLabel), typeof(bool), typeof(ItemStateIcon), new PropertyMetadata(false, (d, _) => ((ItemStateIcon)d).Apply()));

    public ItemStateIcon()
    {
        InitializeComponent();
        Apply();
    }

    public string StateKey
    {
        get => (string)GetValue(StateKeyProperty);
        set => SetValue(StateKeyProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    private void Apply()
    {
        if (Glyph is null) return;
        var (data, state, label) = StateKey switch
        {
            "pr-open" => (Octicons.PullRequest, "Open", "Open"),
            "pr-merged" => (Octicons.Merge, "Merged", "Merged"),
            "pr-closed" => (Octicons.PullRequestClosed, "Closed", "Closed"),
            "issue-closed" => (Octicons.IssueClosed, "Completed", "Closed"),
            _ => (Octicons.IssueOpened, "Open", "Open")
        };
        Glyph.Data = Octicons.Geometry(data);
        PillGlyph.Data = Octicons.Geometry(data);
        PillText.Text = label;
        Glyph.Visibility = ShowLabel ? Visibility.Collapsed : Visibility.Visible;
        Pill.Visibility = ShowLabel ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAccessibilityView(this, ShowLabel
            ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content
            : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        VisualStateManager.GoToState(this, state, false);
    }
}
