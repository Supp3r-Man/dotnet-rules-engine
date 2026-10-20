using System.Reflection;

namespace RulesEngine.Demo;

/// <summary>Interactive read-eval-print loop: type a rule, see which customers match.</summary>
public static class Repl
{
    public static void Run(IReadOnlyList<Customer> customers, RuleOptions options, TextReader input, TextWriter output, bool showPrompt)
    {
        output.WriteLine("Rules REPL. Type a rule to see matching customers, ':help' for commands, ':quit' to exit.");
        while (true)
        {
            if (showPrompt)
            {
                output.Write("rule> ");
            }

            string? line = input.ReadLine();
            if (line == null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == ':')
            {
                if (!Command(line, customers, options, output))
                {
                    break;
                }

                continue;
            }

            Evaluate(line, customers, options, output);
        }
    }

    private static bool Command(string line, IReadOnlyList<Customer> customers, RuleOptions options, TextWriter output)
    {
        string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
        string rest = parts.Length > 1 ? parts[1] : string.Empty;
        switch (parts[0].ToLowerInvariant())
        {
            case ":quit":
            case ":q":
                return false;
            case ":help":
                output.WriteLine("  <rule>               evaluate a rule against every customer");
                output.WriteLine("  :explain <id> <rule> show which sub-conditions were true/false for one customer");
                output.WriteLine("  :print <rule>        parse and pretty-print a rule (normalised form)");
                output.WriteLine("  :schema              list the properties a rule can use");
                output.WriteLine("  :customers           list the loaded customers");
                output.WriteLine("  :quit                leave");
                break;
            case ":schema":
                DescribeType(typeof(Customer), string.Empty, output);
                output.WriteLine("  functions: " + string.Join(", ", RuleCompiler.AllowedFunctions));
                break;
            case ":customers":
                foreach (Customer c in customers)
                {
                    output.WriteLine($"  {c.Id}  {c.Name}");
                }

                break;
            case ":print":
                try
                {
                    output.WriteLine("  " + Printer.Print(Parser.Parse(rest)));
                }
                catch (RuleException ex)
                {
                    output.WriteLine(ex.Render(rest));
                }

                break;
            case ":explain":
                {
                    string[] args = rest.Split(' ', 2, StringSplitOptions.TrimEntries);
                    Customer? customer = args.Length == 2 ? customers.FirstOrDefault(c => c.Id.Equals(args[0], StringComparison.OrdinalIgnoreCase)) : null;
                    if (customer == null)
                    {
                        output.WriteLine("  usage: :explain <customer id> <rule>");
                        break;
                    }

                    try
                    {
                        CompiledRule<Customer> rule = RuleCompiler.Compile<Customer>(args[1], options);
                        output.WriteLine($"  {customer.Id} {customer.Name}:");
                        foreach (string l in rule.Explain(customer).ToText().Split('\n'))
                        {
                            output.WriteLine("    " + l.TrimEnd('\r'));
                        }
                    }
                    catch (RuleException ex)
                    {
                        output.WriteLine(ex.Render(args[1]));
                    }

                    break;
                }

            default:
                output.WriteLine($"  unknown command '{parts[0]}' (try :help)");
                break;
        }

        return true;
    }

    private static void Evaluate(string text, IReadOnlyList<Customer> customers, RuleOptions options, TextWriter output)
    {
        try
        {
            CompiledRule<Customer> rule = RuleCompiler.Compile<Customer>(text, options);
            List<Customer> matches = customers.Where(rule.Evaluate).ToList();
            output.WriteLine($"  {matches.Count} of {customers.Count} match: " + string.Join(", ", matches.Select(c => c.Id)));
        }
        catch (RuleException ex)
        {
            output.WriteLine(ex.Render(text));
        }
    }

    private static void DescribeType(Type type, string prefix, TextWriter output)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            Type t = p.PropertyType;
            bool nested = t.IsClass && t != typeof(string);
            output.WriteLine($"  {prefix}{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}: {FriendlyName(t)}");
            if (nested)
            {
                DescribeType(t, $"{prefix}{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}.", output);
            }
        }
    }

    private static string FriendlyName(Type t)
    {
        Type? under = Nullable.GetUnderlyingType(t);
        return under != null ? FriendlyName(under) + "?" : t.Name;
    }
}
