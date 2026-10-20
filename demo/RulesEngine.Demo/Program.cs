using System.Globalization;

namespace RulesEngine.Demo;

public static class Program
{
    private const string DefaultNow = "2026-10-20T12:00:00Z";

    private static readonly string[] BadRules =
    [
        "contry == \"US\"",
        "age >= \"18\"",
        "status == \"Clsoed\"",
        "address.cty == \"Austin\"",
        "name.ToUpper() == \"A\"",
        "age >= 18 and",
    ];

    public static int Main(string[] args) => Run(args, Console.In, Console.Out, !Console.IsInputRedirected);

    public static int Run(string[] args, TextReader input, TextWriter output, bool interactive)
    {
        string? customersPath = null;
        string? rulesPath = null;
        string now = DefaultNow;
        string explain = "C002:eligible_us_ca,C007:dormant";
        bool repl = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--customers" when i + 1 < args.Length: customersPath = args[++i]; break;
                case "--rules" when i + 1 < args.Length: rulesPath = args[++i]; break;
                case "--now" when i + 1 < args.Length: now = args[++i]; break;
                case "--explain" when i + 1 < args.Length: explain = args[++i]; break;
                case "--repl": repl = true; break;
                default:
                    output.WriteLine("usage: RulesEngine.Demo [--customers file.csv] [--rules rules.txt] [--now ISO-8601|system] [--explain id[:rule],...] [--repl]");
                    return args[i] is "--help" or "-h" ? 0 : 2;
            }
        }

        customersPath ??= FindSample("customers.csv");
        rulesPath ??= FindSample("rules.txt");
        if (customersPath == null || rulesPath == null)
        {
            output.WriteLine("could not find samples/customers.csv and samples/rules.txt; pass --customers and --rules");
            return 2;
        }

        TimeProvider clock = now.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? TimeProvider.System
            : new FixedTimeProvider(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        var options = new RuleOptions { TimeProvider = clock };

        IReadOnlyList<Customer> customers = CustomerLoader.Parse(File.ReadAllText(customersPath));
        output.WriteLine("rules-engine demo");
        output.WriteLine($"clock: {clock.GetUtcNow():yyyy-MM-dd'T'HH:mm:ss'Z'}{(now.Equals("system", StringComparison.OrdinalIgnoreCase) ? " (system)" : " (fixed; use --now system for the real clock)")}");
        output.WriteLine($"loaded {customers.Count} customers");

        if (repl)
        {
            Repl.Run(customers, options, input, output, interactive);
            return 0;
        }

        RuleSet<Customer> ruleSet;
        try
        {
            IReadOnlyList<RuleDefinition> definitions = RuleFile.Parse(File.ReadAllText(rulesPath));
            ruleSet = RuleSet<Customer>.Create(definitions, options);
        }
        catch (RuleException ex)
        {
            output.WriteLine("rule file error: " + ex.Message);
            return 1;
        }

        output.WriteLine($"loaded {ruleSet.Rules.Count} rules (evaluation order: priority descending)");
        foreach (NamedRule<Customer> r in ruleSet.Rules)
        {
            output.WriteLine($"  {r.Priority,3}  {r.Name,-16} {r.Rule.Source}");
        }

        output.WriteLine();
        output.WriteLine("all-match: every rule that matches each customer");
        var hits = ruleSet.Rules.ToDictionary(r => r.Name, _ => 0);
        foreach (Customer c in customers)
        {
            IReadOnlyList<NamedRule<Customer>> matches = ruleSet.AllMatches(c);
            foreach (NamedRule<Customer> m in matches)
            {
                hits[m.Name]++;
            }

            output.WriteLine($"  {c.Id} {c.Name,-14} {(matches.Count == 0 ? "(none)" : string.Join(", ", matches.Select(m => m.Name)))}");
        }

        output.WriteLine();
        output.WriteLine("first-match: highest-priority matching rule per customer");
        foreach (Customer c in customers)
        {
            NamedRule<Customer>? first = ruleSet.FirstMatch(c);
            output.WriteLine($"  {c.Id} {c.Name,-14} {(first == null ? "(none)" : first.Name)}");
        }

        output.WriteLine();
        output.WriteLine("hits per rule: " + string.Join(", ", ruleSet.Rules.Select(r => $"{r.Name}={hits[r.Name]}")));

        foreach (string spec in explain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = spec.Split(':', 2);
            Customer? customer = customers.FirstOrDefault(c => c.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (customer == null)
            {
                output.WriteLine($"explain: no customer '{parts[0]}'");
                continue;
            }

            foreach (RuleEvaluation e in ruleSet.Explain(customer).Where(e => parts.Length == 1 || e.Name.Equals(parts[1], StringComparison.OrdinalIgnoreCase)))
            {
                output.WriteLine();
                output.WriteLine($"explain {customer.Id} {customer.Name} / rule {e.Name}: {(e.Matched ? "MATCH" : "no match")}");
                foreach (string line in e.Explanation.ToText().Split('\n'))
                {
                    output.WriteLine("  " + line.TrimEnd('\r'));
                }
            }
        }

        output.WriteLine();
        output.WriteLine("bad rules are rejected with a position and, for unknown names, a suggestion:");
        foreach (string bad in BadRules)
        {
            try
            {
                RuleCompiler.Compile<Customer>(bad, options);
                output.WriteLine($"  unexpectedly accepted: {bad}");
            }
            catch (RuleException ex)
            {
                foreach (string line in ex.Render(bad).Split('\n'))
                {
                    output.WriteLine("  " + line.TrimEnd('\r'));
                }
            }
        }

        return 0;
    }

    private static string? FindSample(string file)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? dir = new(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "samples", file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
