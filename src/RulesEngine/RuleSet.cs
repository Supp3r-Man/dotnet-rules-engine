using System.Text.RegularExpressions;

namespace RulesEngine;

/// <summary>A named rule. Higher <see cref="Priority"/> is evaluated first; ties keep declaration order.</summary>
public sealed record RuleDefinition(string Name, string Expression, int Priority = 0);

public sealed record NamedRule<T>(string Name, int Priority, CompiledRule<T> Rule);

/// <summary>Per-rule outcome of <see cref="RuleSet{T}.Explain"/>.</summary>
public sealed record RuleEvaluation(string Name, int Priority, bool Matched, ExplainNode Explanation);

/// <summary>
/// Reads rule files. One rule per line: <c>name [priority N]: expression</c>.
/// Blank lines and lines starting with '#' are ignored.
/// </summary>
public static class RuleFile
{
    private static readonly Regex Line = new(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_\-]*)(?:\s+priority\s+(?<p>-?\d+))?\s*:\s*(?<expr>\S.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static IReadOnlyList<RuleDefinition> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<RuleDefinition>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            Match m = Line.Match(line);
            if (!m.Success)
            {
                throw new RuleException($"rule file line {i + 1}: expected 'name [priority N]: expression'", -1);
            }

            int priority = 0;
            if (m.Groups["p"].Success && !int.TryParse(m.Groups["p"].Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out priority))
            {
                throw new RuleException($"rule file line {i + 1}: priority is out of range", -1);
            }

            result.Add(new RuleDefinition(m.Groups["name"].Value, m.Groups["expr"].Value.Trim(), priority));
        }

        return result;
    }
}

/// <summary>Named, prioritised rules compiled once and evaluated first-match or all-match.</summary>
public sealed class RuleSet<T>
{
    private readonly IReadOnlyList<NamedRule<T>> _rules;

    private RuleSet(IReadOnlyList<NamedRule<T>> rules) => _rules = rules;

    /// <summary>Rules in evaluation order (priority descending, then declaration order).</summary>
    public IReadOnlyList<NamedRule<T>> Rules => _rules;

    public static RuleSet<T> Create(IEnumerable<RuleDefinition> definitions, RuleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<NamedRule<T>>();
        foreach (RuleDefinition d in definitions)
        {
            if (string.IsNullOrWhiteSpace(d.Name))
            {
                throw new RuleException("a rule needs a name", -1);
            }

            if (!seen.Add(d.Name))
            {
                throw new RuleException($"duplicate rule name '{d.Name}'", -1, ruleName: d.Name);
            }

            try
            {
                list.Add(new NamedRule<T>(d.Name, d.Priority, RuleCompiler.Compile<T>(d.Expression, options)));
            }
            catch (RuleException ex)
            {
                throw ex.WithRuleName(d.Name);
            }
        }

        return new RuleSet<T>(list.OrderByDescending(r => r.Priority).ToList());
    }

    /// <summary>First matching rule in priority order, or null.</summary>
    public NamedRule<T>? FirstMatch(T item) => _rules.FirstOrDefault(r => r.Rule.Evaluate(item));

    /// <summary>Every matching rule, in priority order.</summary>
    public IReadOnlyList<NamedRule<T>> AllMatches(T item) => _rules.Where(r => r.Rule.Evaluate(item)).ToList();

    /// <summary>
    /// Evaluates rules in priority order with the interpreter and returns the explanation tree of each.
    /// With <paramref name="stopAtFirstMatch"/> the list ends at the first matching rule.
    /// </summary>
    public IReadOnlyList<RuleEvaluation> Explain(T item, bool stopAtFirstMatch = false)
    {
        var result = new List<RuleEvaluation>();
        foreach (NamedRule<T> r in _rules)
        {
            ExplainNode node = r.Rule.Explain(item);
            bool matched = node.Result == true;
            result.Add(new RuleEvaluation(r.Name, r.Priority, matched, node));
            if (matched && stopAtFirstMatch)
            {
                break;
            }
        }

        return result;
    }
}
