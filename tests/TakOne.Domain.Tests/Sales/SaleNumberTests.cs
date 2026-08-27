using FluentAssertions;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Sales;

/// <summary>
/// Unit tests for the <see cref="SaleNumber"/> value object — the
/// globally-unique, Persian-calendar-rendered sale identifier.
/// Verifies the year/sequence range guards, the constants
/// (Prefix, MinPersianYear, MaxPersianYear, MinSequence, MaxSequence),
/// the Persian-digit rendering of <see cref="SaleNumber.Value"/>,
/// equality, and ToString.
/// </summary>
public class SaleNumberTests
{
    // ======================================================================
    //                          CREATE — HAPPY PATH
    // ======================================================================

    [Fact]
    public void Create_WithValidYearAndSequence_SetsBothProperties()
    {
        // Arrange
        const int year = 1403;
        const int seq = 42;

        // Act
        var number = SaleNumber.Create(year, seq);

        // Assert
        number.Year.Should().Be(year);
        number.Sequence.Should().Be(seq);
    }

    [Fact]
    public void Create_WithBoundaryYearMin_Works()
    {
        // Arrange — Persian year 1300 is the smallest legal value
        // Act
        var number = SaleNumber.Create(SaleNumber.MinPersianYear, 1);

        // Assert
        number.Year.Should().Be(SaleNumber.MinPersianYear);
    }

    [Fact]
    public void Create_WithBoundaryYearMax_Works()
    {
        // Arrange — Persian year 1500 is the largest legal value
        // Act
        var number = SaleNumber.Create(SaleNumber.MaxPersianYear, 1);

        // Assert
        number.Year.Should().Be(SaleNumber.MaxPersianYear);
    }

    [Fact]
    public void Create_WithBoundarySequenceMin_Works()
    {
        // Arrange — sequence 1 is the smallest legal value
        // Act
        var number = SaleNumber.Create(1403, SaleNumber.MinSequence);

        // Assert
        number.Sequence.Should().Be(SaleNumber.MinSequence);
    }

    [Fact]
    public void Create_WithBoundarySequenceMax_Works()
    {
        // Arrange — sequence 99999999 is the largest legal value (8-digit D8)
        // Act
        var number = SaleNumber.Create(1403, SaleNumber.MaxSequence);

        // Assert
        number.Sequence.Should().Be(SaleNumber.MaxSequence);
    }

    // ======================================================================
    //                          CREATE — GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithYearBelowMin_Throws()
    {
        // Arrange — year 1299 is below MinPersianYear
        Action act = () => SaleNumber.Create(1299, 42);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage($"Persian year 1299 is out of the supported range [{SaleNumber.MinPersianYear}, {SaleNumber.MaxPersianYear}].*");
    }

    [Fact]
    public void Create_WithYearAboveMax_Throws()
    {
        // Arrange — year 1501 is above MaxPersianYear
        Action act = () => SaleNumber.Create(1501, 42);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage($"Persian year 1501 is out of the supported range [{SaleNumber.MinPersianYear}, {SaleNumber.MaxPersianYear}].*");
    }

    [Fact]
    public void Create_WithSequenceBelowMin_Throws()
    {
        // Arrange — sequence 0 is below MinSequence
        Action act = () => SaleNumber.Create(1403, 0);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage($"Sale sequence 0 is out of the supported range*");
    }

    [Fact]
    public void Create_WithSequenceAboveMax_Throws()
    {
        // Arrange — sequence 100000000 exceeds the 8-digit D8 max
        Action act = () => SaleNumber.Create(1403, 100000000);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage($"Sale sequence 100000000 is out of the supported range*");
    }

    // ======================================================================
    //                          CONSTANTS
    // ======================================================================

    [Fact]
    public void Prefix_IsInt()
    {
        SaleNumber.Prefix.Should().Be("INT");
    }

    [Fact]
    public void MinPersianYear_Is1300()
    {
        SaleNumber.MinPersianYear.Should().Be(1300);
    }

    [Fact]
    public void MaxPersianYear_Is1500()
    {
        SaleNumber.MaxPersianYear.Should().Be(1500);
    }

    [Fact]
    public void MinSequence_IsOne()
    {
        SaleNumber.MinSequence.Should().Be(1);
    }

    [Fact]
    public void MaxSequence_Is99999999()
    {
        SaleNumber.MaxSequence.Should().Be(99999999);
    }

    // ======================================================================
    //                          VALUE (Persian-digit rendering)
    // ======================================================================

    [Fact]
    public void Value_ForYear1403Seq42_ReturnsPersianDigitString()
    {
        // Arrange
        var number = SaleNumber.Create(1403, 42);

        // Act
        var value = number.Value;

        // Assert — INT-{PersianDigits(1403)}-{PersianDigits("00000042")}
        // Persian digits: ۱ ۴ ۰ ۳ and ۰۰۰۰۰۰۴۲
        value.Should().Be("INT-۱۴۰۳-۰۰۰۰۰۰۴۲");
    }

    [Fact]
    public void Value_ForYear1403Seq1_ReturnsZeroPaddedPersianSequence()
    {
        // Arrange — the smallest valid sequence, zero-padded to 8 Persian digits
        var number = SaleNumber.Create(1403, 1);

        // Act
        var value = number.Value;

        // Assert
        value.Should().Be("INT-۱۴۰۳-۰۰۰۰۰۰۰۱");
    }

    [Fact]
    public void Value_ForYear1403Seq99999999_ReturnsMaxSequenceString()
    {
        // Arrange — boundary: max sequence renders as ۹۹۹۹۹۹۹۹
        var number = SaleNumber.Create(1403, 99999999);

        // Act
        var value = number.Value;

        // Assert
        value.Should().Be("INT-۱۴۰۳-۹۹۹۹۹۹۹۹");
    }

    // ======================================================================
    //                          EQUALITY / TOSTRING
    // ======================================================================

    [Fact]
    public void Equality_WhenSameYearAndSequence_AreEqual()
    {
        // Arrange
        var a = SaleNumber.Create(1403, 42);
        var b = SaleNumber.Create(1403, 42);

        // Assert
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void Equality_WhenDifferentYear_AreNotEqual()
    {
        // Arrange
        var a = SaleNumber.Create(1403, 42);
        var b = SaleNumber.Create(1404, 42);

        // Assert
        a.Should().NotBe(b);
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void Equality_WhenDifferentSequence_AreNotEqual()
    {
        // Arrange
        var a = SaleNumber.Create(1403, 42);
        var b = SaleNumber.Create(1403, 43);

        // Assert
        a.Should().NotBe(b);
        (a == b).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_WhenEqualObjects_ProduceSameHash()
    {
        // Arrange
        var a = SaleNumber.Create(1403, 42);
        var b = SaleNumber.Create(1403, 42);

        // Assert
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsSameAsValue()
    {
        // Arrange
        var number = SaleNumber.Create(1403, 42);

        // Assert
        number.ToString().Should().Be(number.Value);
    }
}
