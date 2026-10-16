using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHubToDo.Core;
using GitHubToDo.Services;

namespace GitHubToDo.ViewModels;

public sealed record QueryPreviewRow(string Title, string Subtitle, string StateKey, string ShortTime);

public sealed record QueryAttemptRow(QueryAttempt Attempt)
{
    public string Header => $"Attempt {Attempt.Number}";
    public string Expression => Attempt.Expression;
    public string Outcome => Attempt.Outcome;
    public bool Succeeded => Attempt.Succeeded;
    public string Glyph => Attempt.Succeeded ? "\uE73E" : "\uE7BA";
}

public sealed partial class QueryBuilderViewModel : ObservableObject, IDisposable
{
    public const int PreviewCount = 20;
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _preview;
    private CancellationTokenSource? _generation;
    private (string Expression, SearchResult Result)? _lastCheck;
    private bool _suppressAutoPreview;
    private bool _nameFromAi;
    private bool _isDisposed;

    public QueryBuilderViewModel(ShellViewModel shell, SavedQuery? existing)
    {
        _shell = shell;
        IsEditing = existing is not null;
        (IsAiAvailable, AiDescription) = shell.GetLocalAiAvailability();
        Name = existing?.Name ?? "";
        _suppressAutoPreview = true;
        Expression = existing?.Expression ?? "";
        _suppressAutoPreview = false;
        ApplyQualifierFilter();
        SelectedQualifier = FilteredQualifiers.FirstOrDefault();
        if (Expression.Length == 0) PreviewStatus = "Build or describe a search to see matching issues and pull requests.";
    }

    public bool IsEditing { get; }
    public bool IsAiAvailable { get; }
    public string AiDescription { get; }
    public string Overview => QueryLanguage.Overview;
    public ObservableCollection<QualifierDoc> FilteredQualifiers { get; } = [];
    public ObservableCollection<QueryAttemptRow> Attempts { get; } = [];
    public ObservableCollection<QueryPreviewRow> PreviewItems { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Expression { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial string ValidationText { get; set; } = "";
    [ObservableProperty] public partial bool HasValidationIssues { get; set; }
    [ObservableProperty] public partial bool IsQueryValid { get; set; }
    [ObservableProperty] public partial string QualifierFilter { get; set; } = "";
    [ObservableProperty] public partial QualifierDoc? SelectedQualifier { get; set; }
    [ObservableProperty] public partial string QualifierValue { get; set; } = "";
    [ObservableProperty] public partial bool ExcludeQualifier { get; set; }
    [ObservableProperty] public partial string QualifierError { get; set; } = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(PreviewCommand))]
    public partial bool IsGenerating { get; set; }
    [ObservableProperty] public partial string GenerationStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsPreviewing { get; set; }
    [ObservableProperty] public partial string PreviewStatus { get; set; } = "";
    [ObservableProperty] public partial bool HasPreviewItems { get; set; }

    public bool HasQualifierChoices => SelectedQualifier?.Values is { Count: > 0 };
    public bool HasFreeValue => SelectedQualifier is not null && !HasQualifierChoices;
    public IReadOnlyList<string> QualifierChoices => SelectedQualifier?.Values ?? [];
    public string QualifierExamples => SelectedQualifier is null ? "" : string.Join("\n", SelectedQualifier.Examples);
    public string QualifierHeading => SelectedQualifier is null ? "" : $"{SelectedQualifier.Name}:";
    public string QualifierValueHint => SelectedQualifier?.Kind switch
    {
        QualifierValueKind.User => SelectedQualifier.SupportsMe ? "@me or a login" : "A login",
        QualifierValueKind.Repository => "owner/name",
        QualifierValueKind.Date => ">=2026-01-01",
        QualifierValueKind.Number => ">10",
        _ => "Value"
    };

    partial void OnExpressionChanged(string value)
    {
        Validate();
        if (_suppressAutoPreview) return;
        PreviewStatus = IsQueryValid ? "Updating preview…" : "Fix the query to preview results.";
        _ = PreviewAsync(debounce: true);
    }

    partial void OnNameChanged(string value) => _nameFromAi = false;

    partial void OnQualifierFilterChanged(string value) => ApplyQualifierFilter();

    partial void OnSelectedQualifierChanged(QualifierDoc? value)
    {
        QualifierError = "";
        ExcludeQualifier = false;
        QualifierValue = value switch
        {
            { Values.Count: > 0 } => value.Values[0],
            { SupportsMe: true } => "@me",
            _ => ""
        };
        OnPropertyChanged(nameof(HasQualifierChoices));
        OnPropertyChanged(nameof(HasFreeValue));
        OnPropertyChanged(nameof(QualifierChoices));
        OnPropertyChanged(nameof(QualifierExamples));
        OnPropertyChanged(nameof(QualifierHeading));
        OnPropertyChanged(nameof(QualifierValueHint));
    }

    private void ApplyQualifierFilter()
    {
        var filter = QualifierFilter.Trim();
        var selected = SelectedQualifier;
        FilteredQualifiers.Clear();
        foreach (var doc in QueryLanguage.Qualifiers.Where(doc => filter.Length == 0 ||
            doc.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            doc.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            doc.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            FilteredQualifiers.Add(doc);
        if (selected is null || !FilteredQualifiers.Contains(selected))
            SelectedQualifier = FilteredQualifiers.FirstOrDefault() ?? selected;
        // The list lost its selection while being refilled; let the one-way binding restore it.
        OnPropertyChanged(nameof(SelectedQualifier));
    }

    private void Validate()
    {
        var issues = QueryValidator.Validate(Expression);
        if (string.IsNullOrWhiteSpace(Expression))
        {
            IsQueryValid = false;
            HasValidationIssues = false;
            ValidationText = "";
            return;
        }
        var parses = true;
        try { QueryParser.Parse(Expression, "viewer"); }
        catch (ArgumentException) { parses = false; }
        IsQueryValid = parses;
        HasValidationIssues = issues.Count > 0;
        ValidationText = issues.Count == 0 ? "Looks good. Every qualifier is recognized."
            : string.Join("\n", issues.Select(issue => issue.Message));
    }

    [RelayCommand]
    private void AddQualifier()
    {
        if (SelectedQualifier is not { } doc) return;
        try
        {
            AppendToken(doc.Token(QualifierValue ?? "", ExcludeQualifier));
            QualifierError = "";
        }
        catch (ArgumentException ex) { QualifierError = ex.Message; }
    }

    [RelayCommand]
    private void InsertExample(string? example)
    {
        if (!string.IsNullOrWhiteSpace(example)) AppendToken(example);
    }

    private void AppendToken(string token) =>
        Expression = string.IsNullOrWhiteSpace(Expression) ? token : $"{Expression.TrimEnd()} {token}";

    private bool CanPreview() => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private Task PreviewAsync() => PreviewAsync(debounce: false);

    private async Task PreviewAsync(bool debounce)
    {
        _preview?.Cancel();
        if (!IsQueryValid) { ClearPreview(); return; }
        var cancellation = _preview = new CancellationTokenSource();
        var expression = Expression;
        try
        {
            if (debounce) await Task.Delay(700, cancellation.Token);
            IsPreviewing = true;
            var result = _lastCheck is { } cached && cached.Expression == expression
                ? cached.Result
                : await _shell.PreviewQueryAsync(expression, PreviewCount, cancellation.Token);
            if (cancellation.IsCancellationRequested || _isDisposed) return;
            ShowPreview(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or ArgumentException)
        {
            if (cancellation.IsCancellationRequested || _isDisposed) return;
            ClearPreview();
            PreviewStatus = $"GitHub could not run this search: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_preview, cancellation)) IsPreviewing = false;
        }
    }

    private void ShowPreview(SearchResult result)
    {
        PreviewItems.Clear();
        foreach (var item in result.Items.Take(PreviewCount))
            PreviewItems.Add(new(item.Title, $"{item.Repository} #{item.Number} · @{item.AuthorLogin ?? "unknown"}",
                (item.IsPullRequest ? "pr-" : "issue-") + (item.State.ToLowerInvariant() is "merged" or "closed" ? item.State.ToLowerInvariant() : "open"),
                ActivityRow.ShortTime(item.UpdatedAt)));
        HasPreviewItems = PreviewItems.Count > 0;
        PreviewStatus = result.TotalCount switch
        {
            0 => "No matches right now. The query is valid; new matches will appear after refresh.",
            _ when result.TotalCount > PreviewItems.Count => $"{result.TotalCount:N0} matches · showing the {PreviewItems.Count} most recently updated",
            _ => $"{result.TotalCount:N0} match(es)"
        } + (result.TotalCount > 1000 ? " · only the first 1,000 can be tracked" : "");
    }

    private void ClearPreview()
    {
        PreviewItems.Clear();
        HasPreviewItems = false;
        IsPreviewing = false;
        PreviewStatus = string.IsNullOrWhiteSpace(Expression)
            ? "Build or describe a search to see matching issues and pull requests."
            : "Fix the query to preview results.";
    }

    private bool CanGenerate() => IsAiAvailable && !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(Description))
        {
            GenerationStatus = "Describe the issues or pull requests you want, e.g. “open PRs in dotnet/maui waiting for my review”.";
            return;
        }
        _generation?.Cancel();
        _preview?.Cancel();
        var cancellation = _generation = new CancellationTokenSource();
        IsGenerating = true;
        Attempts.Clear();
        GenerationStatus = "Writing a query with the on-device model…";
        var progress = new Progress<QueryAttempt>(attempt =>
        {
            if (_isDisposed || cancellation.IsCancellationRequested) return;
            Attempts.Add(new(attempt));
            GenerationStatus = attempt.Succeeded ? "Validated against GitHub." : $"Attempt {attempt.Number} didn't work. Retrying with the error…";
        });
        try
        {
            var result = await LocalAiQueryWriter.GenerateAsync(Description, CheckAsync, progress, cancellation.Token);
            if (_isDisposed || cancellation.IsCancellationRequested) return;
            if (result.Expression is not null)
            {
                _suppressAutoPreview = true;
                Expression = result.Expression;
                _suppressAutoPreview = false;
                if (result.Name is not null && (Name.Length == 0 || _nameFromAi))
                {
                    Name = result.Name;
                    _nameFromAi = true;
                }
            }
            var count = result.Attempts.Count;
            GenerationStatus = result.Succeeded
                ? $"Generated and verified against GitHub in {count} attempt{(count == 1 ? "" : "s")}. Review it before saving."
                : $"No working query after {count} attempts. The last attempt is in the query box: edit it, or rephrase the description.";
            if (result.Succeeded) await PreviewAsync(debounce: false);
            else ClearPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isDisposed) GenerationStatus = $"Local AI could not generate a query: {Services.LocalAiErrors.Describe(ex)}";
        }
        finally
        {
            if (ReferenceEquals(_generation, cancellation)) IsGenerating = false;
        }
    }

    private async Task<QueryCheck> CheckAsync(string expression, CancellationToken ct)
    {
        try
        {
            var result = await _shell.PreviewQueryAsync(expression, PreviewCount, ct);
            _lastCheck = (expression, result);
            return new(null, result.TotalCount);
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or ArgumentException)
        {
            return new(ex.Message);
        }
    }

    /// <summary>Called when the editor opens: show what an existing query matches right away.</summary>
    public void Start()
    {
        if (IsQueryValid) _ = PreviewAsync(debounce: false);
    }

    public string? GetSaveError()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Enter a display name.";
        try { QueryParser.Parse(Expression, "viewer"); }
        catch (ArgumentException ex) { return ex.Message; }
        return null;
    }

    public void Dispose()
    {
        _isDisposed = true;
        _preview?.Cancel();
        _generation?.Cancel();
    }
}
