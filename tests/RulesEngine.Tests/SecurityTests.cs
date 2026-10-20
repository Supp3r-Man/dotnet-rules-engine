using System.Linq.Expressions;
using System.Reflection;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

/// <summary>
/// Security model: rule text can only (1) read public instance properties of the root type and nested classes,
/// (2) call the six whitelisted functions, (3) use the operators of the language. It cannot name a method, a
/// type, a field, a static member, an indexer or a non-public member, and regular expressions have a timeout.
/// </summary>
public class SecurityTests
{
    [Theory]
    [InlineData("name.ToUpper() == \"A\"")]
    [InlineData("name.Length > 1")]
    [InlineData("name.GetType() == 1")]
    [InlineData("a.GetHashCode() > 1")]
    [InlineData("System.Diagnostics.Process.Start(\"calc\") == 1")]
    [InlineData("System.Environment.Exit(0) == 1")]
    [InlineData("exec(\"calc\") == 1")]
    [InlineData("eval(name) == 1")]
    [InlineData("getType(name) == 1")]
    [InlineData("revealSecret() == \"x\"")]
    [InlineData("describe() == \"x\"")]
    [InlineData("math.abs(a) > 1")]
    [InlineData("string.Concat(name, name) == \"x\"")]
    [InlineData("typeof(string) == 1")]
    [InlineData("new(1) == 1")]
    public void Rules_cannot_call_arbitrary_methods(string rule)
    {
        var ex = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>(rule, H.Options));
        Assert.True(ex.Position >= 0);
    }

    [Theory]
    [InlineData("secret == \"s3cret\"")]
    [InlineData("Secret == \"s3cret\"")]
    [InlineData("hiddenNumber == 42")]
    [InlineData("HiddenNumber == 42")]
    [InlineData("protected")]
    [InlineData("publicField == 0")]
    [InlineData("counter == 5")]
    [InlineData("item == \"x\"")]
    [InlineData("child.nonexistent == 1")]
    public void Rules_cannot_reach_private_static_field_or_indexer_members(string rule)
    {
        var ex = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>(rule, H.Options));
        Assert.Contains("unknown property", ex.Message);
    }

    [Fact]
    public void Suggestions_never_leak_non_public_member_names()
    {
        RuleException ex = H.Fail("secrt == \"x\"");
        Assert.NotEqual("Secret", ex.Suggestion);
        RuleException ex2 = H.Fail("hiddenNumbr == 1");
        Assert.NotEqual("HiddenNumber", ex2.Suggestion);
    }

    [Fact]
    public void Private_member_values_are_unreachable_even_though_the_property_exists()
    {
        // The model has a private property 'Secret' and a public method that returns it; neither can be named.
        Assert.Equal("s3cret", new Rec().RevealSecret());
        H.Fail("secret == \"s3cret\"");
    }

    public static IEnumerable<object[]> ConstructCorpus() =>
        new[]
        {
            "a >= 18 and b < 5 or not flag",
            "b is null or b is not null",
            "name startswith \"A\" and name endswith \"z\" and name contains \"m\"",
            "name matches \"^[A-Z]+$\"",
            "lower(name) == upper(code) and len(name) > 2",
            "abs(a - b) > 1",
            "a + b * 2 - 1 / 3 % 2 > 0",
            "price * 2 > rebate + 1.5",
            "score / 2 > ratio",
            "a / b > 1",
            "a in [1, 2, 3] and b not in [4]",
            "a between 1 and 5 and b not between 1 and 5",
            "color == \"Red\" and maybeColor in [\"Blue\", \"Green\"]",
            "day < now() and moment < now() and stamp < now()",
            "daysSince(day) > 3 and daysSince(moment) > 3 and daysSince(stamp) > 3",
            "child.value > 1 and child.deeper.value < 5 and child.label is null",
            "name + code == \"ab\"",
            "name > \"a\" and name <= \"z\"",
            "day >= \"2024-01-01\" and moment < \"2025-01-01T00:00:00Z\" and stamp != \"2025-01-01T00:00:00+02:00\"",
            "-a < -b and not (flagN == true)",
        }.Select(s => new object[] { s });

    private sealed class CallCollector : ExpressionVisitor
    {
        public HashSet<string> Methods { get; } = [];

        public HashSet<string> Members { get; } = [];

        public int Invocations { get; private set; }

        public int Constructions { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Methods.Add(node.Method.DeclaringType!.Name + "." + node.Method.Name);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.Method != null)
            {
                Methods.Add(node.Method.DeclaringType!.Name + "." + node.Method.Name);
            }

            return base.VisitBinary(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.Method != null)
            {
                Methods.Add(node.Method.DeclaringType!.Name + "." + node.Method.Name);
            }

            return base.VisitUnary(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            Members.Add(node.Member.DeclaringType!.Name + "." + node.Member.Name);
            Assert.IsAssignableFrom<PropertyInfo>(node.Member);
            Assert.True(((PropertyInfo)node.Member).GetMethod!.IsPublic);
            return base.VisitMember(node);
        }

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            Invocations++;
            return base.VisitInvocation(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            Constructions++;
            return base.VisitNew(node);
        }
    }

    private static readonly HashSet<string> AllowedMethods =
    [
        "String.StartsWith", "String.EndsWith", "String.Contains", "String.ToLowerInvariant", "String.ToUpperInvariant",
        "String.Concat", "String.CompareOrdinal", "String.op_Equality", "String.op_Inequality", "Math.Abs",
        "DateOnly.FromDateTime", "TimeProvider.GetUtcNow", "RuleRuntime.RegexIsMatch", "RuleRuntime.DaysSince",
        "Decimal.op_Equality", "Decimal.op_Inequality", "Decimal.op_LessThan", "Decimal.op_LessThanOrEqual",
        "Decimal.op_GreaterThan", "Decimal.op_GreaterThanOrEqual", "Decimal.op_Addition", "Decimal.op_Subtraction",
        "Decimal.op_Multiply", "Decimal.op_Division", "Decimal.op_Modulus", "Decimal.op_UnaryNegation", "Decimal.op_Implicit",
        "DateOnly.op_Equality", "DateOnly.op_Inequality", "DateOnly.op_LessThan", "DateOnly.op_LessThanOrEqual",
        "DateOnly.op_GreaterThan", "DateOnly.op_GreaterThanOrEqual",
        "DateTime.op_Equality", "DateTime.op_Inequality", "DateTime.op_LessThan", "DateTime.op_LessThanOrEqual",
        "DateTime.op_GreaterThan", "DateTime.op_GreaterThanOrEqual",
        "DateTimeOffset.op_Equality", "DateTimeOffset.op_Inequality", "DateTimeOffset.op_LessThan", "DateTimeOffset.op_LessThanOrEqual",
        "DateTimeOffset.op_GreaterThan", "DateTimeOffset.op_GreaterThanOrEqual",
    ];

    [Theory]
    [MemberData(nameof(ConstructCorpus))]
    public void Compiled_trees_only_call_whitelisted_methods_and_read_public_properties(string rule)
    {
        var collector = new CallCollector();
        collector.Visit(H.C(rule).Tree);
        Assert.Empty(collector.Methods.Except(AllowedMethods));
        Assert.Equal(0, collector.Invocations);
        Assert.Equal(0, collector.Constructions);
    }

    [Fact]
    public void The_corpus_exercises_every_whitelisted_function_and_operator_family()
    {
        var all = new HashSet<string>();
        foreach (object[] row in ConstructCorpus())
        {
            var c = new CallCollector();
            c.Visit(H.C((string)row[0]).Tree);
            all.UnionWith(c.Methods);
        }

        foreach (string expected in new[]
                 {
                     "String.StartsWith", "String.EndsWith", "String.Contains", "String.ToLowerInvariant", "String.ToUpperInvariant",
                     "String.Concat", "String.CompareOrdinal", "Math.Abs", "DateOnly.FromDateTime", "TimeProvider.GetUtcNow",
                     "RuleRuntime.RegexIsMatch", "RuleRuntime.DaysSince", "Decimal.op_Multiply",
                 })
        {
            Assert.Contains(expected, all);
        }
    }

    [Fact]
    public void Catastrophic_regex_is_stopped_by_the_timeout()
    {
        var options = new RuleOptions { RegexTimeout = TimeSpan.FromMilliseconds(50), TimeProvider = H.Options.TimeProvider };
        CompiledRule<Rec> rule = RuleCompiler.Compile<Rec>("name matches \"^(a+)+$\"", options);
        var evil = new Rec { Name = new string('a', 45) + "!" };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<RuleException>(() => rule.Evaluate(evil));
        watch.Stop();
        Assert.Contains("timed out after 50 ms", ex.Message);
        Assert.Equal(13, ex.Position);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Interpreter_applies_the_same_regex_timeout()
    {
        var options = new RuleOptions { RegexTimeout = TimeSpan.FromMilliseconds(50), TimeProvider = H.Options.TimeProvider };
        CompiledRule<Rec> rule = RuleCompiler.Compile<Rec>("name matches \"^(a+)+$\"", options);
        var evil = new Rec { Name = new string('a', 45) + "!" };
        var ex = Assert.Throws<RuleException>(() => rule.Interpret(evil));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void Harmless_input_matches_normally_with_the_same_pattern()
    {
        Assert.True(H.Eval("name matches \"^(a+)+$\"", new Rec { Name = "aaaa" }));
        Assert.False(H.Eval("name matches \"^(a+)+$\"", new Rec { Name = "aaab" }));
    }

    [Fact]
    public void Non_positive_regex_timeout_is_rejected()
    {
        var options = new RuleOptions { RegexTimeout = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleCompiler.Compile<Rec>("flag", options));
        var infinite = new RuleOptions { RegexTimeout = Timeout.InfiniteTimeSpan };
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleCompiler.Compile<Rec>("flag", infinite));
    }

    [Fact]
    public void Regex_pattern_must_be_a_literal_so_data_cannot_choose_a_pattern()
    {
        RuleException ex = H.Fail("name matches code");
        Assert.Contains("string literal", ex.Message);
        H.Fail("name matches lower(\"^a\")");
    }

    [Fact]
    public void The_function_whitelist_has_exactly_six_entries()
    {
        Assert.Equal(6, RuleCompiler.AllowedFunctions.Count);
    }

    [Fact]
    public void Only_the_library_exception_type_leaves_the_compile_path_for_hostile_rules()
    {
        string[] hostile =
        [
            "name.GetType().Assembly.GetTypes()",
            "a.b.c.d.e.f",
            "((((",
            "\"unterminated",
            "name matches \"[\"",
            "1 +",
            "x in [",
            "@@@",
        ];
        foreach (string rule in hostile)
        {
            Assert.Throws<RuleException>(() => RuleCompiler.Compile<Rec>(rule, H.Options));
        }
    }
}
