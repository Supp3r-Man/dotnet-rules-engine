using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RulesEngine.Tests.Support;

namespace RulesEngine.Tests;

public sealed class Home
{
    public string? City { get; set; }
}

public sealed class Person
{
    public int Id { get; set; }

    public string? Name { get; set; }

    public int Age { get; set; }

    public int? Level { get; set; }

    public string? Country { get; set; }

    public bool Vip { get; set; }

    public decimal Balance { get; set; }

    public double Ratio { get; set; }

    public Color? Tier { get; set; }

    public DateOnly Joined { get; set; }

    public DateTimeOffset? Seen { get; set; }

    public Home? Home { get; set; }
}

public sealed class PeopleDb(SqliteConnection connection) : DbContext
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseSqlite(connection);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Person>().ComplexProperty(p => p.Home).IsRequired();
}

/// <summary>
/// Which constructs survive EF Core (SQLite provider) translation when the compiled expression tree is used as an
/// IQueryable predicate. "translates" means the SQL ran AND returned exactly the rows the in-memory delegate returns.
/// </summary>
public class EfCoreTests(ITestOutputHelper output)
{
    private const string Translates = "translates";
    private const string Differs = "translates-but-different-rows";
    private const string Throws = "throws";

    private static readonly (string Rule, bool Friendly, string Expected)[] Table =
    [
        ("age >= 18", false, Translates),
        ("age >= 18 and vip", false, Translates),
        ("not vip or age < 30", false, Translates),
        ("vip == true", false, Translates),
        ("country in ['US','CA']", false, Translates),
        ("country not in ['US']", false, Translates),
        ("name startswith \"A\"", false, Throws),
        ("name startswith \"A\"", true, Translates),
        ("name endswith \"e\"", true, Translates),
        ("name contains \"li\"", false, Translates),
        ("name is null", false, Translates),
        ("name is not null and age > 20", false, Translates),
        ("level > 3", false, Translates),
        ("level is null", false, Translates),
        ("not (level > 3)", false, Translates),
        ("level between 2 and 5", false, Translates),
        ("age between 18 and 65", false, Translates),
        ("age + 1 > 30", false, Translates),
        ("age * 2 - 5 < 60", false, Translates),
        ("age / 2 > 10", false, Translates),
        ("age % 2 == 0", false, Translates),
        ("age / level > 1", false, Throws),
        ("len(name) > 3", false, Translates),
        ("lower(name) == \"bob\"", false, Throws),
        ("upper(name) == \"BOB\"", false, Throws),
        ("abs(age - 40) < 5", false, Translates),
        ("name matches \"^A\"", false, Throws),
        ("daysSince(joined) > 100", false, Throws),
        ("joined < \"2024-01-01\"", false, Translates),
        ("seen < \"2025-01-01T00:00:00Z\"", false, Throws),
        ("joined < now()", false, Translates),
        ("tier == \"Red\"", false, Translates),
        ("tier in [\"Red\",\"Blue\"]", false, Translates),
        ("home.city == \"Austin\"", false, Translates),
        ("home.city is null", false, Translates),
        ("balance > 100", false, Translates),
        ("balance * 2 > 150.5", false, Translates),
        ("ratio > 0.5", false, Translates),
        ("ratio * 2 + 1 > 2", false, Translates),
        ("name + \"x\" == \"Bobx\"", false, Translates),
        ("name > \"M\"", false, Throws),
        ("(age > 20 or vip) and (country == \"US\" or level is null)", false, Translates),
    ];

    private static List<Person> Seed()
    {
        var r = new Random(5);
        string?[] names = [null, "Alice", "bob", "Bob", "Eli", "Zed", "Aline", "Ann", string.Empty];
        string?[] countries = [null, "US", "CA", "GB", "DE"];
        string?[] cities = [null, "Austin", "Denver", "Toronto"];
        var list = new List<Person>();
        for (int i = 1; i <= 80; i++)
        {
            string? city = cities[r.Next(cities.Length)];
            list.Add(new Person
            {
                Id = i,
                Name = names[r.Next(names.Length)],
                Age = r.Next(10, 80),
                Level = r.Next(4) == 0 ? null : r.Next(0, 8),
                Country = countries[r.Next(countries.Length)],
                Vip = r.Next(3) == 0,
                Balance = r.Next(0, 50000) / 100m,
                Ratio = r.NextDouble(),
                Tier = r.Next(3) == 0 ? null : (Color)r.Next(3),
                Joined = new DateOnly(2020, 1, 1).AddDays(r.Next(0, 2500)),
                Seen = r.Next(3) == 0 ? null : new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(r.Next(0, 20000)),
                Home = new Home { City = city },
            });
        }

        return list;
    }

    private static (string Outcome, string Detail) Classify(PeopleDb db, List<Person> memory, string rule, bool friendly)
    {
        var options = new RuleOptions { QueryFriendly = friendly, TimeProvider = H.Options.TimeProvider };
        CompiledRule<Person> compiled = RuleCompiler.Compile<Person>(rule, options);
        List<int> expected = memory.Where(compiled.Predicate).Select(p => p.Id).Order().ToList();
        try
        {
            List<int> actual = db.People.Where(compiled.Tree).Select(p => p.Id).ToList().Order().ToList();
            return actual.SequenceEqual(expected)
                ? (Translates, $"{actual.Count} rows")
                : (Differs, $"in-memory {expected.Count} rows, SQL {actual.Count} rows");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            string first = ex.Message.Split('\n')[0];
            if (ex is NotSupportedException)
            {
                first += " @ " + string.Join(" <- ", (ex.StackTrace ?? string.Empty).Split('\n').Take(5).Select(l => l.Trim()));
            }

            return (Throws, $"{ex.GetType().Name}: {(first.Length > 700 ? first[..700] : first)}");
        }
    }

    [Fact]
    public void Construct_by_construct_translation_report_matches_the_documented_findings()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new PeopleDb(connection);
        db.Database.EnsureCreated();
        List<Person> memory = Seed();
        db.People.AddRange(memory);
        db.SaveChanges();

        var mismatches = new List<string>();
        foreach ((string rule, bool friendly, string expected) in Table)
        {
            (string outcome, string detail) = Classify(db, memory, rule, friendly);
            output.WriteLine($"{outcome,-30} friendly={friendly,-5} {rule}    [{detail}]");
            if (outcome != expected)
            {
                mismatches.Add($"{rule} (friendly={friendly}): expected {expected} but got {outcome} [{detail}]");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void The_table_covers_every_function_and_operator_family()
    {
        string all = string.Join("\n", Table.Select(t => t.Rule));
        foreach (string fragment in new[] { "len(", "lower(", "upper(", "abs(", "daysSince(", "now()", "matches", "startswith", "endswith", "contains", " in [", " between ", " is null", "/", "%" })
        {
            Assert.Contains(fragment, all);
        }
    }
}
