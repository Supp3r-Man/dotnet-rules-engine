using System.Text;

namespace RulesEngine;

/// <summary>
/// The only exception type the library throws for bad rule text, binding errors, rule-file errors
/// and run-time rule failures (for example a regular expression timeout).
/// </summary>
public class RuleException : Exception
{
    public RuleException(string detail, int position, int length = 0, string? suggestion = null, string? ruleName = null)
        : base(BuildMessage(detail, position, suggestion, ruleName))
    {
        Detail = detail;
        Position = position;
        Length = length;
        Suggestion = suggestion;
        RuleName = ruleName;
    }

    /// <summary>The message without position, suggestion or rule name.</summary>
    public string Detail { get; }

    /// <summary>0-based UTF-16 offset into the rule text, or -1 when there is no position.</summary>
    public int Position { get; }

    public int Length { get; }

    /// <summary>"Did you mean" candidate for unknown names, or null.</summary>
    public string? Suggestion { get; }

    public string? RuleName { get; }

    public RuleException WithRuleName(string ruleName) => new(Detail, Position, Length, Suggestion, ruleName);

    private static string BuildMessage(string detail, int position, string? suggestion, string? ruleName)
    {
        var sb = new StringBuilder();
        if (ruleName != null)
        {
            sb.Append("rule '").Append(ruleName).Append("': ");
        }

        sb.Append(detail);
        if (position >= 0)
        {
            sb.Append(" (at position ").Append(position).Append(')');
        }

        if (suggestion != null)
        {
            sb.Append(". Did you mean '").Append(suggestion).Append("'?");
        }

        return sb.ToString();
    }

    /// <summary>The message followed by the offending source line and a caret marker.</summary>
    public string Render(string source)
    {
        if (Position < 0 || Position > source.Length)
        {
            return Message;
        }

        int lineStart = Position == 0 ? 0 : source.LastIndexOf('\n', Position - 1) + 1;
        int lineEnd = source.IndexOf('\n', Position);
        if (lineEnd < 0)
        {
            lineEnd = source.Length;
        }

        string line = source[lineStart..lineEnd].TrimEnd('\r');
        int caretLength = Math.Max(1, Math.Min(Length, lineEnd - Position));
        return Message + Environment.NewLine + "  " + line + Environment.NewLine
               + "  " + new string(' ', Position - lineStart) + new string('^', caretLength);
    }
}
