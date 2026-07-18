using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Sales.ValueObjects;

public class SaleNumber : BaseValueObject
{
    public string Prefix { get; } // "SALE"
    public int Year { get; }      // 2024
    public int Sequence { get; }  // 42
    public string Value { get; }

    private SaleNumber(string prefix, int year, int sequence)
    {
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("Prefix is required");

        if (year < 2000 || year > 2100) throw new ArgumentException("Invalid year");

        // needs changing if we have more sales than 9999 in a year
        if (sequence < 1 || sequence > 9999) throw new ArgumentException("Sequence must be between 1 and 9999");

        Prefix = prefix.ToUpper();
        Year = year;
        Sequence = sequence;
        // D4 make it shot small numbers like : 0042
        Value = $"{Prefix}-{Year}-{Sequence:D4}";
    }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private SaleNumber() { }
#pragma warning restore CS8618

    public static SaleNumber First(string prefix)
    {
        return new SaleNumber(prefix, DateTime.Now.Year, 1);
    }

    public static SaleNumber Next(SaleNumber previous, string prefix)
    {
        if (previous.Year != DateTime.Now.Year) return First(prefix);
        return new SaleNumber(prefix, previous.Year, previous.Sequence + 1);
    }

    public static SaleNumber FromParts(string prefix, int year, int sequence)
    {
        return new SaleNumber(prefix, year, sequence);
    }

    public override string ToString() => Value;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Prefix;
        yield return Year;
        yield return Sequence;
    }
}