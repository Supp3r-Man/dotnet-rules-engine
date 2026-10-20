using RulesEngine.Demo;

namespace RulesEngine.Tests;

public class DemoTests
{
    public static string FindSample(string file)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("samples/" + file);
    }

    private static string RunDemo(params string[] args)
    {
        var output = new StringWriter();
        int code = Program.Run(args, new StringReader(string.Empty), output, false);
        Assert.Equal(0, code);
        return output.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void Sample_customers_load()
    {
        IReadOnlyList<Customer> customers = CustomerLoader.Parse(File.ReadAllText(FindSample("customers.csv")));
        Assert.Equal(12, customers.Count);
        Customer c = customers[0];
        Assert.Equal("C001", c.Id);
        Assert.Equal(CustomerStatus.Active, c.Status);
        Assert.Equal(1520.50m, c.Balance);
        Assert.Equal("Austin", c.Address!.City);
        Assert.Null(customers[3].Email);
        Assert.Null(customers[5].Address);
        Assert.Null(customers[6].LastLogin);
    }

    [Fact]
    public void Customer_loader_rejects_bad_headers_and_rows()
    {
        Assert.Throws<FormatException>(() => CustomerLoader.Parse("id,name\nC1,x"));
        const string header = "id,name,age,country,status,vip,balance,score,signup,last_login,email,city\n";
        var ex = Assert.Throws<FormatException>(() => CustomerLoader.Parse(header + "C1,x,1"));
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Demo_prints_rules_matches_explanations_and_errors()
    {
        string o = RunDemo("--customers", FindSample("customers.csv"), "--rules", FindSample("rules.txt"));
        Assert.Contains("loaded 12 customers", o);
        Assert.Contains("loaded 9 rules", o);
        Assert.Contains("C001 Alice Smith    vip_review, eligible_us_ca, good_score, local_city, long_tenure", o);
        Assert.Contains("C002 Aaron Blake    (none)", o);
        Assert.Contains("first-match: highest-priority matching rule per customer", o);
        Assert.Contains("explain C002 Aaron Blake / rule eligible_us_ca: no match", o);
        Assert.Contains("[SKIP ] country in [\"US\", \"CA\"]", o);
        Assert.Contains("Did you mean 'Country'?", o);
        Assert.Contains("Did you mean 'Closed'?", o);
        Assert.Contains("method calls are not supported", o);
    }

    [Fact]
    public void Demo_output_is_deterministic_with_the_fixed_clock()
    {
        string[] args = ["--customers", FindSample("customers.csv"), "--rules", FindSample("rules.txt")];
        Assert.Equal(RunDemo(args), RunDemo(args));
    }

    [Fact]
    public void Moving_the_clock_changes_daysSince_results()
    {
        string[] common = ["--customers", FindSample("customers.csv"), "--rules", FindSample("rules.txt")];
        string early = RunDemo([.. common, "--now", "2026-10-20T12:00:00Z"]);
        string late = RunDemo([.. common, "--now", "2031-10-20T12:00:00Z"]);
        Assert.NotEqual(early, late);
        Assert.Contains("C003 Chloe Martin   dormant, good_score", late);
        Assert.DoesNotContain("C003 Chloe Martin   dormant", early);
    }

    [Fact]
    public void Demo_rejects_unknown_arguments()
    {
        var output = new StringWriter();
        Assert.Equal(2, Program.Run(["--bogus"], new StringReader(string.Empty), output, false));
        Assert.Contains("usage:", output.ToString());
    }

    [Fact]
    public void Demo_reports_a_broken_rule_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "rules-engine-broken-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "bad: contry == \"US\"\n");
        try
        {
            var output = new StringWriter();
            int code = Program.Run(["--customers", FindSample("customers.csv"), "--rules", path], new StringReader(string.Empty), output, false);
            Assert.Equal(1, code);
            Assert.Contains("rule 'bad': unknown property 'contry'", output.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Repl_evaluates_rules_explains_and_reports_errors()
    {
        string script = string.Join(
            "\n",
            "age >= 18 and country == \"US\"",
            "contry == \"US\"",
            ":print age>=18 AND not(vip)",
            ":explain C001 vip and balance > 5000",
            ":explain C999 vip",
            ":schema",
            ":customers",
            ":bogus",
            "",
            ":quit",
            "age >= 0");
        var output = new StringWriter();
        int code = Program.Run(["--repl", "--customers", FindSample("customers.csv")], new StringReader(script), output, false);
        string o = output.ToString().Replace("\r\n", "\n");
        Assert.Equal(0, code);
        Assert.Contains("3 of 12 match: C001, C009, C011", o);
        Assert.Contains("Did you mean 'Country'?", o);
        Assert.Contains("  age >= 18 and not vip", o);
        Assert.Contains("[TRUE ] vip", o);
        Assert.Contains("[FALSE] balance > 5000", o);
        Assert.Contains("usage: :explain", o);
        Assert.Contains("address.city: String", o);
        Assert.Contains("functions: len, lower, upper, abs, now, daysSince", o);
        Assert.Contains("C012  Ivan Novak", o);
        Assert.Contains("unknown command ':bogus'", o);
        Assert.DoesNotContain("12 of 12 match", o);
        Assert.DoesNotContain("rule> ", o);
    }

    [Fact]
    public void Repl_shows_a_prompt_only_when_interactive()
    {
        var output = new StringWriter();
        Program.Run(["--repl", "--customers", FindSample("customers.csv")], new StringReader(":quit\n"), output, true);
        Assert.Contains("rule> ", output.ToString());
    }
}
