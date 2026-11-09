using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Resources;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.SaleDetail;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests for the SaleDetail page's receipt CSV download
/// (the data-processing twin of the printable invoice).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. Clicking the download button on a SUBMITTED sale produces a
///      <c>takDownload.csv</c> JS invocation whose payload is one FLAT
///      RFC 4180 table: the 12 localized header columns, one row per
///      line item, sale metadata repeated per row, RAW invariant amounts,
///      ISO-8601 UTC timestamps, and RFC 4180 quoting for CSV-hostile
///      product names. The button renders alongside (and only alongside)
///      the Print button in the .invoice-actions group.
///   2. DRAFTS (no SaleNumber) get NO document actions at all — the
///      print button's visibility gate now covers the whole group.
/// </para>
/// <para>
/// <b>IDENTITY LOCALIZER</b>: headers/status render as their resx KEYS
/// (e.g. <c>ColSaleNumber</c>, <c>StatusApproved</c>) — the established
/// stable-markup convention (see
/// <see cref="ManageGroupsPageExportTests"/>).
/// </para>
/// <para>
/// <b>WIRING</b>: a fake bus answering <see cref="GetSaleByIdQuery"/>
/// shape with a canned successful <see cref="Result{T}"/>, the identity
/// localizer, a substitute ILogger + DialogService spy (the page injects
/// both), and a CUSTOMER viewer — the receipt is NOT staff-gated: whoever
/// the server let see the sale (a customer's own order) can export it.
/// </para>
/// </remarks>
public class SaleDetailReceiptExportTests
{
    /// <summary>
    /// A canned sale. Submitted by default (real SaleNumber, Approved
    /// status, full audit trail); pass <c>submitted: false</c> for a
    /// DRAFT (pseudo-id only, nothing to take away as a document).
    /// Line items: two — one carrying a CSV-hostile product name (comma
    /// + quotes) to pin the RFC 4180 quoting. Line totals sum to the
    /// grand total (2500.5 + 1250).
    /// </summary>
    private static SaleDto MakeSale(bool submitted = true)
    {
        return new SaleDto
        {
            Id = Guid.NewGuid(),
            SaleNumber = submitted ? "INT-1405-00000042" : null,
            Status = submitted ? "Approved" : "Draft",
            CustomerName = "Customer 42",
            CreatedByName = "Test Staff",
            CreatedAtUtc = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero),
            SubmittedAtUtc = submitted ? new DateTimeOffset(2026, 10, 3, 10, 0, 0, TimeSpan.Zero) : null,
            ApprovedAtUtc = submitted ? new DateTimeOffset(2026, 10, 3, 11, 0, 0, TimeSpan.Zero) : null,
            Total = new MoneyDto { Amount = 3750.5m, Currency = "IRR" },
            LineItems =
            [
                new SaleLineItemDto
                {
                    Id = Guid.NewGuid(),
                    LineNumber = 1,
                    ProductId = Guid.NewGuid(),
                    ProductName = "Product, \"Basic\"",
                    Quantity = 2,
                    UnitPrice = new MoneyDto { Amount = 1250.25m, Currency = "IRR" },
                    GrossTotal = new MoneyDto { Amount = 2500.5m, Currency = "IRR" },
                    LineTotal = new MoneyDto { Amount = 2500.5m, Currency = "IRR" }
                },
                new SaleLineItemDto
                {
                    Id = Guid.NewGuid(),
                    LineNumber = 2,
                    ProductId = Guid.NewGuid(),
                    ProductName = "Product Two",
                    Quantity = 1,
                    UnitPrice = new MoneyDto { Amount = 1250m, Currency = "IRR" },
                    GrossTotal = new MoneyDto { Amount = 1250m, Currency = "IRR" },
                    LineTotal = new MoneyDto { Amount = 1250m, Currency = "IRR" }
                }
            ]
        };
    }

    private static (BunitContext ctx, BunitJSInterop js) Setup(SaleDto sale)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer — headers/status labels render as keys.
        ComponentTestSetup.AddIdentityLocalizer<SaleDetail>(ctx);

        // ErrorDisplayService + ToastService (the export toasts on
        // success/failure through the real ToastService).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // The page injects ILogger + Radzen DialogService (the cancel
        // flow — not exercised here). Same substitutes as the
        // ManageGroups page-test harness.
        ctx.Services.AddSingleton(Substitute.For<ILogger<SaleDetail>>());
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // A CUSTOMER viewer: the receipt export is NOT staff-gated (the
        // server already scoped the load — customers only ever load
        // their own sales; staff load any sale).
        ComponentTestSetup.AddAuthenticatedUser(ctx, "customer", Roles.Customer);

        // The fake bus: the sale-detail query always succeeds with the
        // canned sale.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<Result<SaleDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<SaleDto>.Success(sale));
        ctx.Services.AddSingleton(bus);

        return (ctx, ctx.JSInterop);
    }

    /// <summary> Finds the receipt-download button (labelled by its resx key). </summary>
    private static AngleSharp.Dom.IElement FindDownloadButton(
        IRenderedComponent<SaleDetail> cut)
        => cut.FindAll("button").First(b => b.TextContent.Contains("DownloadReceiptButton"));

    [Fact]
    public async Task SaleDetail_DownloadReceiptClick_DownloadsFlatReceiptCsv()
    {
        // Arrange — a submitted, approved sale with 2 line items.
        var sale = MakeSale();
        var (ctx, js) = Setup(sale);
        using (ctx)
        {
            var cut = ctx.Render<SaleDetail>(p => p.Add(s => s.Id, sale.Id));

            // The page loads and the document-action group renders with
            // BOTH buttons: Print (paper copy) + Download CSV (data copy).
            cut.WaitForAssertion(
                () => cut.FindAll(".invoice-actions button").Should().HaveCount(2),
                TimeSpan.FromSeconds(10));
            FindDownloadButton(cut).HasAttribute("disabled").Should().BeFalse();

            // Act — click the receipt download.
            await cut.InvokeAsync(() => FindDownloadButton(cut).Click());

            // Assert — exactly one takDownload.csv invocation.
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args.Should().HaveCount(2);
            args[0].Should().BeOfType<string>().Which
                .Should().StartWith("takone-receipt-INT-1405-00000042-")
                .And.EndWith(".csv");

            // The CSV: 12 localized header columns (sale metadata first,
            // then the line-item table's own columns, then currency +
            // grand total), followed by one row per line item.
            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith(
                "ColSaleNumber,ColStatus,InvoiceCustomer,ColCreatedBy,ColCreatedAt,"
                + "ColLineNumber,ColProduct,ColQuantity,ColUnitPrice,ColCurrency,ColLineTotal,GrandTotal\r\n");

            // Row 1: metadata repeated + RAW invariant amounts + ISO-8601
            // UTC created-at + RFC 4180 quoting of the hostile product
            // name (comma + escaped quotes). NOTE: the status label falls
            // back to the RAW status under the identity localizer
            // (GetStatusLabel's missing-key fallback) — "Approved".
            csv.Should().Contain(
                "INT-1405-00000042,Approved,Customer 42,Test Staff,2026-10-03 09:30:00Z,"
                + "1,\"Product, \"\"Basic\"\"\",2,1250.25,IRR,2500.5,3750.5\r\n");

            // Row 2: same metadata, second line; the grand total rides
            // every row (self-contained rows for downstream consumers).
            csv.Should().Contain(
                "INT-1405-00000042,Approved,Customer 42,Test Staff,2026-10-03 09:30:00Z,"
                + "2,Product Two,1,1250,IRR,1250,3750.5\r\n");

            // Header + exactly 2 data records — nothing else.
            csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
        }
    }

    [Fact]
    public void SaleDetail_DraftSale_HidesDocumentActionGroup()
    {
        // Arrange — a DRAFT (SaleNumber null): the print button's
        // visibility gate (drafts have nothing invoice-like to take
        // away) now covers the whole .invoice-actions group, download
        // included.
        var sale = MakeSale(submitted: false);
        var (ctx, _) = Setup(sale);
        using (ctx)
        {
            // Act
            var cut = ctx.Render<SaleDetail>(p => p.Add(s => s.Id, sale.Id));

            // Assert — the page loads (the pseudo-id header renders) but
            // NO document actions render for a draft.
            cut.WaitForAssertion(
                () => cut.Find(".detail-sale-number"),
                TimeSpan.FromSeconds(10));
            cut.FindAll(".invoice-actions").Should().BeEmpty();
        }
    }
}
