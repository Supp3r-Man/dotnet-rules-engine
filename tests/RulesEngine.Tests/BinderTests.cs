using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class BinderTests
{
    [Theory]
    [InlineData("nmae == \"x\"", 0, "Name")]
    [InlineData("flg", 0, "Flag")]
    [InlineData("scor > 1", 0, "Score")]
    [InlineData("child.valu > 1", 6, "child.Value")]
    [InlineData("child.deeper.valeu > 1", 13, "child.deeper.Value")]
    [InlineData("a > 1 and pricee > 2", 10, "Price")]
    [InlineData("MAYBECOLOR2 == \"Red\"", 0, "MaybeColor")]
    public void Unknown_property_gets_position_and_suggestion(string rule, int position, string suggestion)
    {
        RuleException ex = H.Fail(rule);
        Assert.Equal(position, ex.Position);
        Assert.Equal(suggestion, ex.Suggestion);
        Assert.Contains("unknown property", ex.Message);
        Assert.Contains($"Did you mean '{suggestion}'?", ex.Message);
    }

    [Theory]
    [InlineData("zzzzzz == 1")]
    [InlineData("q > 1")]
    [InlineData("completelyunrelatedname")]
    public void Far_names_get_no_suggestion(string rule)
    {
        RuleException ex = H.Fail(rule);
        Assert.Null(ex.Suggestion);
        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    [Theory]
    [InlineData("lenn(name) > 1", "len")]
    [InlineData("uper(name) == \"A\"", "upper")]
    [InlineData("dayssincee(day) > 1", "daysSince")]
    [InlineData("nowe() > day", "now")]
    public void Unknown_function_suggests_a_whitelisted_one(string rule, string suggestion)
    {
        RuleException ex = H.Fail(rule);
        Assert.Equal(0, ex.Position);
        Assert.Equal(suggestion, ex.Suggestion);
        Assert.Contains("unknown function", ex.Message);
    }

    [Fact]
    public void Function_names_are_case_insensitive()
    {
        Assert.True(H.Eval("LEN(name) == 5 and DaysSince(day) == 10 and Lower(name) == \"alice\"", H.Values()));
    }

    [Theory]
    [InlineData("color == \"Gren\"", 9, "Green")]
    [InlineData("maybeColor in [\"Red\", \"Bleu\"]", 22, "Blue")]
    [InlineData("color != \"Purple\"", 9, null)]
    public void Unknown_enum_member(string rule, int position, string? suggestion)
    {
        RuleException ex = H.Fail(rule);
        Assert.Equal(position, ex.Position);
        Assert.Equal(suggestion, ex.Suggestion);
        Assert.Contains("is not a member of enum Color", ex.Message);
    }

    [Theory]
    [InlineData("a == \"x\"", "cannot be applied to integer and string")]
    [InlineData("name > 3", "cannot be applied to string and integer")]
    [InlineData("flag < true", "not supported for boolean")]
    [InlineData("color > \"Red\"", "not supported for enum Color")]
    [InlineData("price + score > 1", "cannot mix decimal and double")]
    [InlineData("price == score", "cannot mix decimal and double")]
    [InlineData("name + 1 == \"x\"", "cannot be applied to string and integer")]
    [InlineData("a", "must evaluate to a boolean")]
    [InlineData("name", "must evaluate to a boolean")]
    [InlineData("a + 1", "must evaluate to a boolean")]
    [InlineData("a and flag", "expected a boolean condition but found integer")]
    [InlineData("not a", "expected a boolean condition but found integer")]
    [InlineData("flag or name", "expected a boolean condition but found string")]
    [InlineData("abs(name) > 1", "needs a number")]
    [InlineData("len(a) > 1", "needs a string")]
    [InlineData("daysSince(a) > 1", "needs a date")]
    [InlineData("len(name, code) > 1", "takes 1 argument but 2 were given")]
    [InlineData("len() > 1", "takes 1 argument but 0 were given")]
    [InlineData("now(1) > day", "takes 0 arguments but 1 was given")]
    [InlineData("name matches code", "must be a string literal")]
    [InlineData("day < \"2024-13-01\"", "cannot read")]
    [InlineData("day < \"01/02/2024\"", "cannot read")]
    [InlineData("moment < stamp", "cannot be applied to datetime and datetimeoffset")]
    [InlineData("day < moment", "cannot be applied to date and datetime")]
    [InlineData("-name == 1", "needs a number")]
    [InlineData("color in [\"Red\", 1]", "cannot be applied to")]
    [InlineData("a in [1, \"x\"]", "cannot be applied to integer and string")]
    [InlineData("a between 1 and \"x\"", "cannot be applied to")]
    [InlineData("flag between false and true", "not supported for boolean")]
    [InlineData("name startswith 1", "needs two strings")]
    [InlineData("flag == 1", "cannot be applied to boolean and integer")]
    [InlineData("a == null", "use 'x is null'")]
    public void Type_errors_have_clear_messages(string rule, string messagePart)
    {
        RuleException ex = H.Fail(rule);
        Assert.Contains(messagePart, ex.Message);
        Assert.True(ex.Position >= 0);
    }

    [Theory]
    [InlineData("id == 1", "has type Guid")]
    [InlineData("numbers == 1", "has type List")]
    [InlineData("child == 1", "has type Inner")]
    [InlineData("publicField > 1", "unknown property")]
    [InlineData("counter > 1", "unknown property")]
    [InlineData("secret == \"x\"", "unknown property")]
    [InlineData("hiddenNumber > 1", "unknown property")]
    [InlineData("protected", "unknown property")]
    [InlineData("item > 1", "unknown property")]
    [InlineData("describe == \"x\"", "unknown property")]
    public void Unsupported_and_inaccessible_members(string rule, string messagePart)
    {
        RuleException ex = H.Fail(rule);
        Assert.Contains(messagePart, ex.Message);
    }

    [Theory]
    [InlineData("name.length > 1", "cannot read 'length' of 'name'", 5)]
    [InlineData("a.b == 1", "cannot read 'b' of 'a'", 2)]
    [InlineData("child.label.length > 1", "cannot read 'length' of 'label'", 12)]
    public void Cannot_navigate_into_scalars(string rule, string messagePart, int position)
    {
        RuleException ex = H.Fail(rule);
        Assert.Contains(messagePart, ex.Message);
        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void Invalid_regex_is_reported_at_the_pattern()
    {
        RuleException ex = H.Fail("name matches \"(\"");
        Assert.Equal(13, ex.Position);
        Assert.Contains("invalid regular expression", ex.Message);
    }

    [Fact]
    public void Type_error_position_is_the_start_of_the_offending_expression()
    {
        RuleException ex = H.Fail("flag and a == \"x\"");
        Assert.Equal(9, ex.Position);
        Assert.Equal(8, ex.Length);
    }

    [Fact]
    public void Property_lookup_is_case_insensitive_and_prefers_exact_case()
    {
        Assert.True(H.Eval("A == 7 and a == 7 and Child.VALUE == 11", H.Values()));
    }

    [Fact]
    public void Ambiguous_case_insensitive_names_are_rejected_but_exact_case_works()
    {
        var ex = Assert.Throws<RuleException>(() => RuleCompiler.Compile<Ambiguous>("name == 1"));
        Assert.Contains("ambiguous", ex.Message);
        CompiledRule<Ambiguous> ok = RuleCompiler.Compile<Ambiguous>("Name == 1 and NAME == 2");
        Assert.True(ok.Evaluate(new Ambiguous { Name = 1, NAME = 2 }));
    }

    [Fact]
    public void Struct_root_types_work()
    {
        CompiledRule<Point> rule = RuleCompiler.Compile<Point>("x > 1 and y < 5");
        Assert.True(rule.Evaluate(new Point(2, 4)));
        Assert.False(rule.Interpret(new Point(0, 4)));
    }

    [Fact]
    public void Allowed_function_list_is_exactly_the_documented_whitelist()
    {
        Assert.Equal(["abs", "daysSince", "len", "lower", "now", "upper"], RuleCompiler.AllowedFunctions.Order().ToArray());
    }

    [Fact]
    public void Edit_distance_basics()
    {
        Assert.Equal(0, EditDistance.Distance("Name", "name"));
        Assert.Equal(1, EditDistance.Distance("nmae", "name"));
        Assert.Equal(1, EditDistance.Distance("flg", "flag"));
        Assert.Equal(3, EditDistance.Distance("kitten", "sitting"));
        Assert.Null(EditDistance.Suggest(new string('x', 200), ["x"]));
        Assert.Null(EditDistance.Suggest(string.Empty, ["x"]));
        Assert.Equal("Name", EditDistance.Suggest("name", ["Name", "Other"]));
        // equal distance: the ordinally first candidate wins, independent of input order
        Assert.Equal("Nama", EditDistance.Suggest("nam", ["Name", "Nama"]));
        Assert.Equal("Nama", EditDistance.Suggest("nam", ["Nama", "Name"]));
    }
}
