using System.Linq.Expressions;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class CompilerApiTests
{
    [Fact]
    public void Compiled_rule_exposes_source_ast_tree_and_delegate()
    {
        CompiledRule<Rec> rule = H.C("a > 5 and flag");
        Assert.Equal("a > 5 and flag", rule.Source);
        Assert.IsType<Binary>(rule.Ast);
        Expression<Func<Rec, bool>> tree = rule.Tree;
        Func<Rec, bool> viaTree = tree.Compile();
        foreach (Rec r in new[] { H.Values(), H.Nulls() })
        {
            Assert.Equal(rule.Predicate(r), viaTree(r));
            Assert.Equal(rule.Evaluate(r), rule.Interpret(r));
        }
    }

    [Fact]
    public void Null_arguments_are_argument_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => RuleCompiler.Compile<Rec>(null!));
        Assert.Throws<ArgumentNullException>(() => H.C("flag").Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => H.C("flag").Interpret(null!));
        Assert.Throws<ArgumentNullException>(() => H.C("flag").Explain(null!));
        Assert.Throws<ArgumentNullException>(() => Printer.Print(null!));
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleCompiler.Compile<Rec>("flag", new RuleOptions { MaxDepth = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleCompiler.Compile<Rec, bool>("flag", new RuleOptions { RegexTimeout = TimeSpan.FromMilliseconds(-5) }));
    }

    [Fact]
    public void Default_options_use_the_system_clock()
    {
        Assert.Same(RuleOptions.Default, RuleOptions.Default);
        Assert.Equal(TimeProvider.System, RuleOptions.Default.TimeProvider);
        Assert.Equal(TimeSpan.FromMilliseconds(250), RuleOptions.Default.RegexTimeout);
        Assert.True(RuleCompiler.Compile<Rec>("moment < now()").Evaluate(new Rec { Moment = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
    }

    [Fact]
    public void Injected_clock_drives_daysSince()
    {
        var rec = new Rec { Day = new DateOnly(2025, 6, 5) };
        var june = new RuleOptions { TimeProvider = new FixedTimeProvider(new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero)) };
        var july = new RuleOptions { TimeProvider = new FixedTimeProvider(new DateTimeOffset(2025, 7, 15, 0, 0, 0, TimeSpan.Zero)) };
        Assert.Equal(10L, RuleCompiler.Compile<Rec, long?>("daysSince(day)", june).Evaluate(rec));
        Assert.Equal(40L, RuleCompiler.Compile<Rec, long?>("daysSince(day)", july).Evaluate(rec));
        Assert.Equal(40L, RuleCompiler.Compile<Rec, long?>("daysSince(day)", july).Interpret(rec));
    }

    [Fact]
    public void Fixed_time_provider_normalises_to_utc()
    {
        var p = new FixedTimeProvider(new DateTimeOffset(2025, 6, 15, 14, 0, 0, TimeSpan.FromHours(2)));
        Assert.Equal(new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero), p.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, p.GetUtcNow().Offset);
    }

    private sealed class StartsWithFinder : ExpressionVisitor
    {
        public int ParameterCount { get; private set; } = -1;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "StartsWith")
            {
                ParameterCount = node.Method.GetParameters().Length;
            }

            return base.VisitMethodCall(node);
        }
    }

    [Fact]
    public void Query_friendly_option_switches_to_the_translatable_overload()
    {
        var strict = new StartsWithFinder();
        strict.Visit(H.C("name startswith \"A\"").Tree);
        Assert.Equal(2, strict.ParameterCount);

        var friendly = new StartsWithFinder();
        friendly.Visit(RuleCompiler.Compile<Rec>("name startswith \"A\"", new RuleOptions { QueryFriendly = true }).Tree);
        Assert.Equal(1, friendly.ParameterCount);
    }

    [Fact]
    public void Exception_render_handles_multi_line_sources()
    {
        const string source = "flag and\nname ==";
        var ex = Assert.Throws<RuleException>(() => Parser.Parse(source));
        string[] lines = ex.Render(source).Split(Environment.NewLine);
        Assert.Equal("  name ==", lines[1]);
        Assert.Equal(new string(' ', 2 + 7) + "^", lines[2]);
    }

    [Fact]
    public void Exception_render_without_position_returns_the_message()
    {
        var ex = new RuleException("no position", -1);
        Assert.Equal("no position", ex.Render("anything"));
        Assert.Equal("rule 'r': no position", ex.WithRuleName("r").Message);
    }

    [Fact]
    public void Exception_message_combines_detail_position_and_suggestion()
    {
        var ex = new RuleException("unknown property 'x'", 4, 1, "y");
        Assert.Equal("unknown property 'x' (at position 4). Did you mean 'y'?", ex.Message);
        Assert.Equal("unknown property 'x'", ex.Detail);
        Assert.Equal(1, ex.Length);
    }

    [Fact]
    public void Bool_rule_compiled_as_typed_expression_returns_bool()
    {
        Assert.True(RuleCompiler.Compile<Rec, bool>("a > 1 and flag", H.Options).Evaluate(H.Values()));
        Assert.Equal(true, RuleCompiler.Compile<Rec, bool>("a > 1 and flag", H.Options).Interpret(H.Values()));
    }

    [Fact]
    public void A_rule_must_be_boolean_even_when_a_typed_expression_would_be_fine()
    {
        var ex = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>("a * 2", H.Options));
        Assert.Contains("must evaluate to a boolean but this expression is integer", ex.Message);
    }
}
