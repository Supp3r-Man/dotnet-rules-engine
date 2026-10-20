using System.Globalization;

namespace RulesEngine.Demo;

public enum CustomerStatus
{
    Active,
    Suspended,
    Closed,
}

public sealed class Address
{
    public string? City { get; init; }
}

/// <summary>Synthetic customer used by the demo. All data in samples/customers.csv is invented.</summary>
public sealed class Customer
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public int Age { get; init; }

    public string Country { get; init; } = string.Empty;

    public CustomerStatus Status { get; init; }

    public bool Vip { get; init; }

    public decimal Balance { get; init; }

    public double Score { get; init; }

    public DateOnly Signup { get; init; }

    public DateTimeOffset? LastLogin { get; init; }

    public string? Email { get; init; }

    public Address? Address { get; init; }
}

/// <summary>Minimal CSV reader for the synthetic sample (no quoting support; fields never contain commas).</summary>
public static class CustomerLoader
{
    private const string Header = "id,name,age,country,status,vip,balance,score,signup,last_login,email,city";

    public static IReadOnlyList<Customer> Parse(string csv)
    {
        string[] lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0 || !lines[0].Equals(Header, StringComparison.Ordinal))
        {
            throw new FormatException($"customer file must start with the header '{Header}'");
        }

        var result = new List<Customer>();
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split(',');
            if (f.Length != 12)
            {
                throw new FormatException($"customer line {i + 1}: expected 12 fields but found {f.Length}");
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            result.Add(new Customer
            {
                Id = f[0],
                Name = f[1],
                Age = int.Parse(f[2], inv),
                Country = f[3],
                Status = Enum.Parse<CustomerStatus>(f[4]),
                Vip = bool.Parse(f[5]),
                Balance = decimal.Parse(f[6], inv),
                Score = double.Parse(f[7], inv),
                Signup = DateOnly.ParseExact(f[8], "yyyy-MM-dd", inv),
                LastLogin = f[9].Length == 0 ? null : DateTimeOffset.Parse(f[9], inv, DateTimeStyles.AssumeUniversal),
                Email = f[10].Length == 0 ? null : f[10],
                Address = f[11].Length == 0 ? null : new Address { City = f[11] },
            });
        }

        return result;
    }
}
