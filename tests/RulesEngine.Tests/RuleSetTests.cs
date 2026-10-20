using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class RuleSetTests
{
    private static RuleSet<Rec> Sample() => RuleSet<Rec>.Create(
        [
            new RuleDefinition("big_a", "a > 5", 1),
            new RuleDefinition("flagged", "flag", 10),
            new RuleDefinition("positive", "a > 0", 10),
            new RuleDefinition("anonymous", "name is null", 5),
        ],
        H.Options);

    [Fact]
    public void Rules_are_ordered_by_priority_then_declaration()
    {
        Assert.Equal(["flagged", "positive", "anonymous", "big_a"], Sample().Rules.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void First_match_uses_priority_order()
    {
        RuleSet<Rec> set = Sample();
        Assert.Equal("flagged", set.FirstMatch(new Rec { A = 7, Flag = true })!.Name);
        Assert.Equal("positive", set.FirstMatch(new Rec { A = 7, Flag = false, Name = "x" })!.Name);
        Assert.Equal("anonymous", set.FirstMatch(new Rec { A = -1, Name = null })!.Name);
        Assert.Null(set.FirstMatch(new Rec { A = -1, Name = "x" }));
    }

    [Fact]
    public void All_match_returns_every_matching_rule_in_priority_order()
    {
        RuleSet<Rec> set = Sample();
        Assert.Equal(["flagged", "positive", "anonymous", "big_a"], set.AllMatches(new Rec { A = 7, Flag = true, Name = null }).Select(r => r.Name).ToArray());
        Assert.Equal(["positive", "big_a"], set.AllMatches(new Rec { A = 7, Name = "n" }).Select(r => r.Name).ToArray());
        Assert.Empty(set.AllMatches(new Rec { A = -5, Name = "n" }));
    }

    [Fact]
    public void Negative_priorities_sort_last()
    {
        var set = RuleSet<Rec>.Create([new RuleDefinition("low", "true", -5), new RuleDefinition("zero", "true"), new RuleDefinition("high", "true", 3)], H.Options);
        Assert.Equal(["high", "zero", "low"], set.Rules.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively()
    {
        var ex = Assert.Throws<RuleException>(() => RuleSet<Rec>.Create([new RuleDefinition("x", "true"), new RuleDefinition("X", "false")], H.Options));
        Assert.Contains("duplicate rule name", ex.Message);
        Assert.Equal("X", ex.RuleName);
    }

    [Fact]
    public void Empty_name_is_rejected()
    {
        Assert.Throws<RuleException>(() => RuleSet<Rec>.Create([new RuleDefinition(" ", "true")], H.Options));
    }

    [Fact]
    public void Compile_errors_carry_the_rule_name_and_original_position()
    {
        var ex = Assert.Throws<RuleException>(() => RuleSet<Rec>.Create(
            [new RuleDefinition("fine", "a > 1"), new RuleDefinition("broken", "a > 1 and nmae == \"x\"")], H.Options));
        Assert.Equal("broken", ex.RuleName);
        Assert.Equal(10, ex.Position);
        Assert.Equal("Name", ex.Suggestion);
        Assert.StartsWith("rule 'broken': unknown property 'nmae'", ex.Message);
    }

    [Fact]
    public void Explain_reports_every_rule_with_its_tree()
    {
        IReadOnlyList<RuleEvaluation> all = Sample().Explain(new Rec { A = 7, Flag = true, Name = "n" });
        Assert.Equal(["flagged", "positive", "anonymous", "big_a"], all.Select(e => e.Name).ToArray());
        Assert.Equal([true, true, false, true], all.Select(e => e.Matched).ToArray());
        Assert.Equal("flag", all[0].Explanation.Text);
    }

    [Fact]
    public void Explain_can_stop_at_the_first_match()
    {
        IReadOnlyList<RuleEvaluation> some = Sample().Explain(new Rec { A = 7, Flag = true }, stopAtFirstMatch: true);
        Assert.Single(some);
        Assert.Equal("flagged", some[0].Name);
    }

    [Fact]
    public void Rule_file_parses_names_priorities_comments_and_blank_lines()
    {
        const string text = "# comment\r\n\r\nfirst priority 20: a > 1\r\nsecond: name == \"a:b\"\r\n   third priority -3 :  flag  \r\n";
        IReadOnlyList<RuleDefinition> defs = RuleFile.Parse(text);
        Assert.Equal(
            [new RuleDefinition("first", "a > 1", 20), new RuleDefinition("second", "name == \"a:b\"", 0), new RuleDefinition("third", "flag", -3)],
            defs);
    }

    [Theory]
    [InlineData("no colon here", 1)]
    [InlineData("# ok\nbad name: a > 1", 2)]
    [InlineData("good: a > 1\n\nx priority abc: a", 3)]
    [InlineData("name:", 1)]
    [InlineData("a priority 99999999999: x", 1)]
    public void Rule_file_errors_name_the_line(string text, int line)
    {
        var ex = Assert.Throws<RuleException>(() => RuleFile.Parse(text));
        Assert.Contains($"line {line}", ex.Message);
    }

    [Fact]
    public void Sample_rule_file_compiles_against_the_demo_model()
    {
        string path = DemoTests.FindSample("rules.txt");
        var set = RuleSet<Demo.Customer>.Create(RuleFile.Parse(File.ReadAllText(path)));
        Assert.Equal(9, set.Rules.Count);
    }
}
