namespace TakOne.Testing;

/// <summary>
/// Stable, deterministic test constants shared across all test projects.
/// Centralized so every test that needs a "known good" Guid or currency
/// uses the SAME value — making cross-test correlations in failure
/// reports easier to read.
/// </summary>
public static class TestValues
{
    // ── Stable Guids ──────────────────────────────────────────────────
    //
    // These are NOT random — they are stable, named, well-known values
    // so that a failing test's actual-vs-expected diff is readable.
    // (Random Guids would force the reader to eyeball the values to
    // confirm they're "the same" rather than reading a name.)

    public static readonly Guid CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CreatedByUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ApprovedByUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid InvoicedByUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid CancelledByUserId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid ProductId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid CategoryId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid SubCategoryId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public static readonly Guid SubSubCategoryId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    public static readonly Guid GroupId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid GroupId2 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid NotificationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public static readonly Guid BroadcastId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly Guid SaleId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid SaleLineItemId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public static readonly Guid UserId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    // ── Currency codes (per ISO 4217) ─────────────────────────────────

    public const string IRR = "IRR"; // Iranian Rial — the default currency for Sale aggregates.
    public const string USD = "USD"; // US Dollar — used in cross-currency mismatch tests.
    public const string EUR = "EUR"; // Euro — used in additional currency-mismatch tests.

    // ── Boundary values for domain guards ─────────────────────────────

    public const int PersianYearMin = 1300;
    public const int PersianYearMax = 1500;
    public const int PersianYearValid = 1403; // a representative valid Persian year for tests
    public const int SequenceMin = 1;
    public const int SequenceMax = 99_999_999;
}
