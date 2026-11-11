using GitHubToDo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GitHubToDo;

public sealed partial class SettingsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ShellViewModel), typeof(SettingsView),
        new PropertyMetadata(null, (d, _) => ((SettingsView)d).Bindings.Update()));

    public ShellViewModel ViewModel
    {
        get => (ShellViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public event RoutedEventHandler? BackRequested;

    public SettingsView()
    {
        InitializeComponent();
        ThemePicker.SelectedIndex = Services.ThemeService.Load() switch
        {
            ElementTheme.Light => 1,
            ElementTheme.Dark => 2,
            _ => 0,
        };
    }

    private void BackToQueries_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, e);

    private async void AiProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is RadioButtons { SelectedIndex: >= 0 } picker && ViewModel is { CanChooseAiProvider: true } viewModel &&
            picker.SelectedIndex != viewModel.AiProviderIndex)
            await viewModel.SetAiProviderAsync(picker.SelectedIndex);
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<ElementTheme>(tag, out var theme)) return;
        Services.ThemeService.Save(theme);
        if (App.Window is { } window) Services.ThemeService.Apply(window, theme);
    }
}
