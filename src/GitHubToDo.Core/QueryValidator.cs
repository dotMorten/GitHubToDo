using System.Text.RegularExpressions;

namespace GitHubToDo.Core;

public sealed record QueryIssue(string Message, bool IsError = true);

/// <summary>Offline checks against <see cref="QueryLanguage"/>. GitHub silently treats unknown
/// qualifiers as free text, so catching them locally avoids queries that quietly match nothing.</summary>
public static partial class QueryValidator
{
    public static IReadOnlyList<QueryIssue> Validate(string input)
    {
        var issues = new List<QueryIssue>();
        List<string> tokens;
        try
        {
            var expression = QueryParser.ExtractExpression(input);
            tokens = QueryParser.Tokenize(expression);
            QueryParser.Parse(expression, "viewer");
        }
        catch (ArgumentException ex) { return [new(ex.Message)]; }

        var depth = 0;
        foreach (var token in tokens)
        {
            if (token == "(") { depth++; continue; }
            if (token == ")")
            {
                if (--depth < 0) { issues.Add(new("A closing parenthesis has no matching opening parenthesis.")); depth = 0; }
                continue;
            }
            if (token is "AND" or "OR" or "NOT" || token.StartsWith('"')) continue;
            var match = QualifierToken().Match(token);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            if (QueryLanguage.Find(name) is not { } doc)
            {
                var hint = Closest(name) is { } close ? $" Did you mean {close}:?" : "";
                issues.Add(new($"Unknown qualifier “{name}:”. GitHub would search for it as text.{hint}"));
                continue;
            }
            if (value.Length == 0) { issues.Add(new($"{doc.Name}: needs a value, e.g. {doc.Examples[0]}.")); continue; }
            if (CheckValue(doc, value) is { } problem) issues.Add(new(problem));
        }
        if (depth > 0) issues.Add(new("An opening parenthesis is not closed."));
        if (tokens.Count > 0 && tokens[0] is "AND" or "OR" || tokens.Count > 0 && tokens[^1] is "AND" or "OR")
            issues.Add(new("AND/OR must sit between two terms."));
        return issues;
    }

    private static string? CheckValue(QualifierDoc doc, string value)
    {
        var bare = value.Trim('"');
        switch (doc.Kind)
        {
            case QualifierValueKind.Choice:
                return doc.Values!.Any(allowed => string.Equals(allowed.Trim('"'), bare, StringComparison.OrdinalIgnoreCase))
                    ? null : $"{doc.Name}:{value} is not supported. Use one of: {string.Join(", ", doc.Values!)}.";
            case QualifierValueKind.User:
                if (bare.Equals("@me", StringComparison.OrdinalIgnoreCase))
                    return doc.SupportsMe ? null : $"{doc.Name}: does not accept @me.";
                return UserValue().IsMatch(bare) ? null : $"“{value}” is not a GitHub login for {doc.Name}:.";
            case QualifierValueKind.Repository:
                return RepositoryValue().IsMatch(bare) ? null : $"repo: needs OWNER/NAME, not “{value}”.";
            case QualifierValueKind.Date:
                return DateValue().IsMatch(bare) ? null : $"{doc.Name}:{value} needs a date such as >=2026-01-01 or 2026-01-01..2026-02-01.";
            case QualifierValueKind.Number:
                return NumberValue().IsMatch(bare) ? null : $"{doc.Name}:{value} needs a number such as >10 or 5..20.";
            default:
                return null;
        }
    }

    private static string? Closest(string name) => QueryLanguage.Qualifiers
        .Select(doc => (doc.Name, Distance: Distance(name.ToLowerInvariant(), doc.Name)))
        .Where(pair => pair.Distance <= Math.Max(2, name.Length / 3))
        .OrderBy(pair => pair.Distance).Select(pair => pair.Name).FirstOrDefault();

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }

    [GeneratedRegex("^-?(?<name>[A-Za-z][A-Za-z_-]*):(?<value>.*)$")]
    private static partial Regex QualifierToken();
    [GeneratedRegex(@"^(app/)?[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(\[bot\])?$")]
    private static partial Regex UserValue();
    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex RepositoryValue();
    [GeneratedRegex(@"^((>=|<=|>|<)?\d{4}-\d{2}-\d{2}(T[\d:]+Z?)?|(\*|\d{4}-\d{2}-\d{2})\.\.(\*|\d{4}-\d{2}-\d{2}))$")]
    private static partial Regex DateValue();
    [GeneratedRegex(@"^((>=|<=|>|<)?\d+|(\*|\d+)\.\.(\*|\d+))$")]
    private static partial Regex NumberValue();
}
