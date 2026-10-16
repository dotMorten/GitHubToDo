using System.Text;
using System.Text.RegularExpressions;

namespace GitHubToDo.Core;

public sealed record ParsedQuery(string Search, string? Sort, string? Order);

public static partial class QueryParser
{
    public const string ReviewsQuery = "is:pr review-requested:@me state:open archived:false sort:updated-desc";

    public static string ExtractExpression(string input)
    {
        input = input.Trim();
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Enter a GitHub search query.");
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !(input.Contains("://", StringComparison.Ordinal)))
            return input;
        if (uri.Scheme != "https" || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an HTTPS github.com issue or pull-request search URL.");
        if (uri.AbsolutePath.TrimEnd('/') == "/pulls/reviews")
            return ReviewsQuery;
        if (uri.AbsolutePath.TrimEnd('/') is not ("/issues" or "/issues/search" or "/pulls" or "/pulls/search" or "/search"))
            throw new ArgumentException("That URL is not a GitHub issue or pull-request search.");
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == "q" && pair.Length == 2)
            {
                var expression = Uri.UnescapeDataString(pair[1].Replace("+", " ")).Trim();
                if (expression.Length != 0)
                    return expression;
            }
        }
        throw new ArgumentException("The search URL must contain a nonempty q parameter.");
    }

    public static ParsedQuery Parse(string expression, string login)
    {
        expression = ExtractExpression(expression);
        var tokens = Tokenize(expression);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.StartsWith("sort:", StringComparison.OrdinalIgnoreCase))
            {
                tokens[i] = "";
            }
            else
            {
                var match = MeQualifier().Match(token);
                if (match.Success)
                    tokens[i] = match.Groups[1].Value + login;
            }
        }
        tokens.RemoveAll(token => token.Length == 0);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] != "(" || tokens[i + 1] != ")") continue;
            tokens.RemoveRange(i, 2);
            i = Math.Max(-1, i - 2);
        }
        var search = string.Join(" ", tokens);
        if (search.Length == 0)
            throw new ArgumentException("Enter a search expression, not just a sort order.");
        return new(search, "updated", "desc");
    }

    public static List<string> Tokenize(string expression)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in expression)
        {
            if (character == '"' && !escaped)
                quoted = !quoted;
            if (!quoted && character is '(' or ')')
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                tokens.Add(character.ToString());
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
                current.Append(character);
            escaped = quoted && character == '\\' && !escaped;
        }
        if (quoted)
            throw new ArgumentException("The query contains an unmatched quote.");
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    [GeneratedRegex("^(-?(?:author|assignee|mentions|commenter|involves|reviewed-by|review-requested|user-review-requested):)@me$", RegexOptions.IgnoreCase)]
    private static partial Regex MeQualifier();
}
