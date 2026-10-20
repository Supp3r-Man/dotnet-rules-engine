using System.Text;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

/// <summary>
/// Hostile-input tests. The contract: parsing/compiling either returns a result or throws RuleException.
/// A NullReferenceException, IndexOutOfRangeException or similar is a bug; a StackOverflow would kill the test host.
/// </summary>
public class DepthAndFuzzTests(ITestOutputHelper output)
{
    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    [Fact]
    public void Ten_thousand_nested_parentheses_give_a_clean_error()
    {
        string rule = Repeat("(", 10_000) + "flag" + Repeat(")", 10_000);
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(rule));
        Assert.Contains("nested too deeply", ex.Message);
        Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>(rule));
    }

    [Theory]
    [InlineData("not ", "flag", "", 10_000)]
    [InlineData("-", "a", "", 10_000)]
    [InlineData("not (", "flag", ")", 10_000)]
    [InlineData("abs(", "a", ")", 10_000)]
    [InlineData("a in [", "a", "]", 5_000)]
    public void Deeply_nested_prefix_and_call_shapes_give_a_clean_error(string open, string core, string close, int n)
    {
        string rule = Repeat(open, n) + core + Repeat(close, n);
        var ex = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>(rule));
        Assert.Contains("nested too deeply", ex.Message);
    }

    [Theory]
    [InlineData("a", "+a", 100_000)]
    [InlineData("flag", " and flag", 100_000)]
    [InlineData("flag", " or flag", 100_000)]
    [InlineData("a", "*2", 100_000)]
    public void Very_long_left_deep_chains_give_a_clean_error(string first, string repeated, int n)
    {
        string rule = first + Repeat(repeated, n);
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(rule));
        Assert.Contains("nested too deeply", ex.Message);
    }

    [Fact]
    public void Depth_limit_is_configurable_and_exact()
    {
        Parser.Parse("((a))", 3);
        Assert.Throws<RuleException>(() => Parser.Parse("(((a)))", 3));
        var options = new RuleOptions { MaxDepth = 3, TimeProvider = H.Options.TimeProvider };
        RuleCompiler.Compile<Rec>("flag and flag and flag", options);
        Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>("flag and flag and flag and flag", options));
    }

    [Fact]
    public void Reasonable_nesting_below_the_default_limit_works()
    {
        string rule = Repeat("(", 200) + "flag" + Repeat(")", 200);
        Assert.True(H.Eval(rule, new Rec { Flag = true }));
        string chain = "a" + Repeat(" + 1", 200) + " > 0";
        Assert.True(H.Eval(chain, new Rec { A = 1 }));
    }

    [Fact]
    public void Huge_but_flat_rules_are_fine()
    {
        string rule = "a in [" + string.Join(", ", Enumerable.Range(0, 5000)) + "]";
        Assert.True(H.Eval(rule, new Rec { A = 4999 }));
        Assert.False(H.Eval(rule, new Rec { A = 5000 }));
    }

    private static readonly string[] Valid =
    [
        "age >= 18 and country in ['US','CA'] and not (status == \"closed\") and name startswith \"A\" or vip",
        "a + b * 2 > 10 or not flag",
        "x between 1 and 5 and y not in [1, 2, 3]",
        "name matches \"^[A-Z][a-z]+$\" and len(name) between 2 and 20",
        "daysSince(day) > 30 and stamp is not null",
        "child.deeper.value >= -3.5 / 2",
        "lower(code) == upper(name) or abs(a - b) % 3 == 0",
        "color in [\"Red\", \"Blue\"] and maybeColor != \"Green\"",
        "(a < b) == (b < c)",
        "price * 1.5 + rebate > score",
    ];

    private static readonly string[] Pieces =
    [
        "and", "or", "not", "in", "not in", "between", "startswith", "endswith", "contains", "matches", "is", "null",
        "true", "false", "==", "!=", "<", "<=", ">", ">=", "+", "-", "*", "/", "%", "(", ")", "[", "]", ",", ".",
        "a", "b", "name", "child.value", "flag", "len(", "abs(", "now()", "daysSince(", "1", "2.5", "007", "\"x\"",
        "'y'", "\"", "'", "\\", "\\u00", "1.", ".5", "a.", "..", "é", "\u0000", "\t", "\n", " ",
    ];

    private static string CharFuzz(Random r)
    {
        const string alphabet = "abcXYZ019 \t\n\"'()[],.+-*/%=!<>&|\\_@#$~^:;?{}é中\u0000";
        int n = r.Next(0, 41);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(alphabet[r.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }

    private static string TokenFuzz(Random r)
    {
        int n = r.Next(1, 16);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(Pieces[r.Next(Pieces.Length)]);
            if (r.Next(3) != 0)
            {
                sb.Append(' ');
            }
        }

        return sb.ToString();
    }

    private static string Mutate(string source, Random r)
    {
        var sb = new StringBuilder(source);
        int edits = r.Next(1, 4);
        for (int e = 0; e < edits && sb.Length > 0; e++)
        {
            int pos = r.Next(sb.Length);
            switch (r.Next(6))
            {
                case 0: sb.Remove(pos, 1); break;
                case 1: sb.Insert(pos, Pieces[r.Next(Pieces.Length)]); break;
                case 2: sb[pos] = "\"'()[],.+-*/%=!<> a1"[r.Next(20)]; break;
                case 3: sb.Length = pos; break;
                case 4:
                    {
                        int len = Math.Min(r.Next(1, 8), sb.Length - pos);
                        sb.Insert(pos, sb.ToString(pos, len));
                        break;
                    }

                default:
                    if (pos + 1 < sb.Length)
                    {
                        (sb[pos], sb[pos + 1]) = (sb[pos + 1], sb[pos]);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string Show(string s) => s.Length > 200 ? s[..200] + "..." : s;

    /// <returns>true if the parser produced an AST, false if it threw RuleException.</returns>
    private static bool ParseOnly(string input)
    {
        try
        {
            Parser.Parse(input);
            return true;
        }
        catch (RuleException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Assert.Fail($"Parser threw {ex.GetType().Name} on input {Show(input)}: {ex.Message}");
            return false;
        }
    }

    private static void CompileAndRun(string input, Rec[] samples)
    {
        try
        {
            CompiledRule<Rec> rule = RuleCompiler.Compile<Rec>(input, H.Options);
            foreach (Rec s in samples)
            {
                bool compiled = rule.Evaluate(s);
                Assert.Equal(compiled, rule.Interpret(s));
            }
        }
        catch (RuleException)
        {
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"Compile/evaluate threw {ex.GetType().Name} on input {Show(input)}: {ex.Message}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_character_strings_only_ever_give_an_ast_or_RuleException(int seed)
    {
        var r = new Random(seed);
        int asts = 0;
        int errors = 0;
        for (int i = 0; i < 6000; i++)
        {
            if (ParseOnly(CharFuzz(r)))
            {
                asts++;
            }
            else
            {
                errors++;
            }
        }

        output.WriteLine($"seed {seed}: {asts} parsed, {errors} RuleException");
        Assert.True(errors > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_token_soup_only_ever_gives_an_ast_or_RuleException(int seed)
    {
        var r = new Random(seed * 101);
        int asts = 0;
        int errors = 0;
        for (int i = 0; i < 6000; i++)
        {
            if (ParseOnly(TokenFuzz(r)))
            {
                asts++;
            }
            else
            {
                errors++;
            }
        }

        output.WriteLine($"seed {seed}: {asts} parsed, {errors} RuleException");
        Assert.True(asts > 0 && errors > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Mutated_valid_rules_parse_bind_and_evaluate_or_throw_RuleException(int seed)
    {
        var r = new Random(seed * 7919);
        Rec[] samples = [H.Values(), H.Nulls()];
        int asts = 0;
        int errors = 0;
        for (int i = 0; i < 4000; i++)
        {
            string input = Mutate(Valid[r.Next(Valid.Length)], r);
            if (ParseOnly(input))
            {
                asts++;
            }
            else
            {
                errors++;
            }

            CompileAndRun(input, samples);
        }

        output.WriteLine($"seed {seed}: {asts} parsed, {errors} RuleException");
        Assert.True(asts > 0 && errors > 0);
    }

    [Fact]
    public void Mutated_generated_rules_bind_and_evaluate_or_throw_RuleException()
    {
        var gen = new RuleGenerator(2024);
        var r = new Random(99);
        Rec[] samples = [H.Values(), H.Nulls()];
        for (int i = 0; i < 1500; i++)
        {
            CompileAndRun(Mutate(gen.Bool(3), r), samples);
        }
    }

    [Fact]
    public void Every_prefix_of_every_valid_rule_is_handled()
    {
        foreach (string rule in Valid)
        {
            for (int len = 0; len <= rule.Length; len++)
            {
                ParseOnly(rule[..len]);
                CompileAndRun(rule[..len], [H.Values()]);
            }
        }
    }
}
