using System.Globalization;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public class CultureTests
{
    private static void UnderCulture(string name, Action action)
    {
        CultureInfo savedCulture = CultureInfo.CurrentCulture;
        CultureInfo savedUi = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
            CultureInfo.CurrentUICulture = savedUi;
        }
    }

    [Fact]
    public void The_de_DE_precondition_holds_on_this_machine()
    {
        UnderCulture("de-DE", () => Assert.Equal(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator));
    }

    [Fact]
    public void Decimal_literal_1_5_is_1_5_under_de_DE()
    {
        UnderCulture("de-DE", () =>
        {
            var b = (Binary)Parser.Parse("x == 1.5");
            Assert.Equal(1.5m, ((NumberLiteral)b.Right).Value);
            Assert.Equal(1.5m, Lexer.Tokenize("1.5")[0].Value);
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("sv-SE")]
    [InlineData("ar-SA")]
    public void Rules_evaluate_identically_under_other_cultures(string culture)
    {
        UnderCulture(culture, () =>
        {
            var rec = new Rec { Price = 1.5m, Score = 0.5, Name = "Illinois", A = 1234567, Code = "I" };
            Assert.True(H.Eval("price == 1.5 and score == 0.5 and a == 1234567 and price * 2 == 3.0", rec));
            Assert.True(H.Eval("lower(name) == \"illinois\" and upper(code) == \"I\" and lower(code) == \"i\"", rec));
            Assert.True(H.Eval("name startswith \"Ill\" and name endswith \"ois\" and name contains \"lin\"", rec));
            Assert.True(H.Eval("name matches \"^Il+inois$\" and name == \"Illinois\" and name < \"Zed\"", rec));
            Assert.Equal("price == 1.5 and a == 1234567", Printer.Print(Parser.Parse("price==1.5 and a==1234567")));
        });
    }

    [Fact]
    public void Turkish_dotless_i_does_not_change_case_mapping()
    {
        UnderCulture("tr-TR", () =>
        {
            Assert.True(H.Eval("lower(name) == \"i\" and upper(name) == \"I\"", new Rec { Name = "I" }));
            Assert.True(H.Eval("upper(name) == \"I\"", new Rec { Name = "i" }));
            Assert.True(H.Eval("name startswith \"I\" and name contains \"i\" and not (name startswith \"i\")", new Rec { Name = "Ii" }));
        });
    }

    [Fact]
    public void Date_literals_use_the_invariant_iso_format_under_any_culture()
    {
        UnderCulture("de-DE", () =>
        {
            Assert.True(H.Eval("day == \"2025-06-05\" and moment == \"2025-06-01T00:00:00Z\"", H.Values()));
            H.Fail("day == \"05.06.2025\"");
        });
    }

    [Fact]
    public void Enum_names_are_matched_without_culture_effects()
    {
        UnderCulture("tr-TR", () => Assert.True(H.Eval("color == \"GREEN\" and maybeColor == \"blue\"", H.Values())));
    }

    [Fact]
    public void Syntax_errors_are_identical_under_a_comma_decimal_culture()
    {
        string invariant = H.Fail("a == 1.5.2").Message;
        UnderCulture("de-DE", () => Assert.Equal(invariant, H.Fail("a == 1.5.2").Message));
    }
}
