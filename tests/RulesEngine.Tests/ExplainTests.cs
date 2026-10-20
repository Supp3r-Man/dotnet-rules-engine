using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class ExplainTests
{
    private static string Norm(string s) => s.Replace("\r\n", "\n");

    [Fact]
    public void And_with_both_sides_true()
    {
        ExplainNode n = H.C("a > 5 and flag").Explain(H.Values());
        Assert.Equal("and", n.Kind);
        Assert.True(n.Result);
        Assert.Equal([true, true], n.Children.Select(c => c.Result).ToArray());
        Assert.Equal(["a > 5", "flag"], n.Children.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void And_short_circuits_and_marks_the_right_side_as_skipped()
    {
        ExplainNode n = H.C("a > 100 and flag").Explain(H.Values());
        Assert.False(n.Result);
        Assert.Equal(false, n.Children[0].Result);
        Assert.Null(n.Children[1].Result);
    }

    [Fact]
    public void Or_short_circuits_when_the_left_side_is_true()
    {
        ExplainNode n = H.C("flag or a > 100").Explain(H.Values());
        Assert.True(n.Result);
        Assert.Null(n.Children[1].Result);
    }

    [Fact]
    public void Or_evaluates_the_right_side_when_the_left_is_false()
    {
        ExplainNode n = H.C("a > 100 or flag").Explain(H.Values());
        Assert.True(n.Result);
        Assert.Equal([false, true], n.Children.Select(c => c.Result).ToArray());
    }

    [Fact]
    public void Not_inverts_its_child()
    {
        ExplainNode n = H.C("not (a > 5)").Explain(H.Values());
        Assert.Equal("not", n.Kind);
        Assert.False(n.Result);
        Assert.True(n.Children[0].Result);
        Assert.Equal("a > 5", n.Children[0].Text);
    }

    [Fact]
    public void Tree_text_snapshot()
    {
        ExplainNode n = H.C("a > 5 and (flag or name startswith \"Z\")").Explain(H.Values());
        Assert.Equal(
            "[TRUE ] AND\n  [TRUE ] a > 5\n  [TRUE ] OR\n    [TRUE ] flag\n    [SKIP ] name startswith \"Z\"",
            Norm(n.ToText()));
    }

    [Fact]
    public void Tree_text_marks_false_conditions()
    {
        ExplainNode n = H.C("b is null or name == \"Bob\"").Explain(H.Values());
        Assert.Equal(
            "[FALSE] OR\n  [FALSE] b is null\n  [FALSE] name == \"Bob\"",
            Norm(n.ToText()));
    }

    [Fact]
    public void Leaf_text_is_the_normalised_source()
    {
        ExplainNode n = H.C("a>5").Explain(H.Values());
        Assert.Equal("test", n.Kind);
        Assert.Equal("a > 5", n.Text);
        Assert.Empty(n.Children);
    }

    [Fact]
    public void Nullable_boolean_leaf_is_false_when_null()
    {
        Assert.False(H.C("flagN").Explain(H.Nulls()).Result);
        Assert.True(H.C("not flagN").Explain(H.Nulls()).Result);
    }

    [Fact]
    public void In_and_between_are_single_leaf_conditions()
    {
        ExplainNode n = H.C("a in [1, 7] and b between 1 and 9").Explain(H.Values());
        Assert.Equal(["a in [1, 7]", "b between 1 and 9"], n.Children.Select(c => c.Text).ToArray());
        Assert.All(n.Children, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void Explain_of_the_spec_rule_for_a_real_input()
    {
        var rec = new Rec { A = 17, Name = "Aaron", Flag = false };
        ExplainNode n = H.C("a >= 18 and name in ['Aaron'] or flag").Explain(rec);
        Assert.False(n.Result);
        Assert.Equal("or", n.Kind);
        ExplainNode and = n.Children[0];
        Assert.Equal("and", and.Kind);
        Assert.Equal(false, and.Children[0].Result);
        Assert.Null(and.Children[1].Result);
        Assert.Equal(false, n.Children[1].Result);
    }
}
