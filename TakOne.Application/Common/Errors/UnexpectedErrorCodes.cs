namespace TakOne.Application.Common.Errors;

/// <summary>
/// Stable, opaque, user-facing reference codes for every UNEXPECTED error
/// surface in the system. Each code is a 7-character alphanumeric string
/// (2 digits + 3 letters + 2 digits, drawn from the alphabet
/// <c>23456789</c> + <c>BCDFGHJKMNPQRSTVWXYZ</c> — i.e. no
/// <c>0</c>/<c>O</c>/<c>1</c>/<c>I</c>/<c>L</c> to avoid legibility
/// collisions) that is:
/// <list type="bullet">
/// <item><b>Unique</b> across the entire codebase — one code per
/// call-site.</item>
/// <item><b>Opaque</b> — the code does NOT encode the file path, class
/// name, method name, or area. An outsider looking at the code cannot
/// reverse-engineer the program's structure from it.</item>
/// <item><b>Stable</b> — once published, a code's meaning never changes
/// (the codes are <c>const</c> values, so any rename is a compile-time
/// break). This is what makes the developer reference PDF valid for
/// long-term support.</item>
/// <item><b>Developer-traceable</b> — the internal developer reference
/// PDF (kept OUT of source control, distributed separately to the
/// support team) maps each code to its exact file:line, classification,
/// root cause, and remediation hint. The user-facing message is just
/// "Unexpected error occurred! Error code: 47NQR83" — the support
/// engineer looks up 47NQR83 in the PDF to find the file and the fix.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE — what gets a code and what doesn't.</b>
/// </para>
/// <para>
/// Only UNEXPECTED errors get a code. An error is UNEXPECTED if it
/// surfaces from a <c>catch (Exception)</c> block (the page cannot
/// tell what specifically went wrong), OR if it is a generic
/// "could not X" / "an unexpected error occurred" message returned
/// from the Application/Infrastructure layer. PURE validation
/// ("Product name is required"), PURE auth ("Wrong password",
/// "Access denied"), PURE business-rule ("Purchase limit exceeded",
/// "Cart conflict"), and PURE not-found ("Sale 'X' was not found")
/// errors do NOT get a code — they already have meaningful,
/// actionable, localized messages via the existing
/// <c>XxxErrors.Format*()</c> stable-code catalogs and the per-page
/// <c>.resx</c> files.
/// </para>
/// <para>
/// <b>WIRE FORMAT.</b> The Application layer returns
/// <c>Result.Failure($"UE|{UnexpectedErrorCodes.X}")</c> for
/// unexpected failures — the <c>UE|</c> prefix tags the message so
/// the UI's <see cref="TakOne.WebUI.Services.ErrorDisplayService.Localize"/>
/// can recognize it, strip the prefix, and substitute a friendly
/// localized message + the visible code. UI <c>catch (Exception)</c>
/// blocks call <c>Toast.UnexpectedError(code)</c> directly — the
/// toast service formats the localized message and visible code itself.
/// </para>
/// <para>
/// <b>NAMING CONVENTION.</b> The C# symbol name encodes the file +
/// operation for developer navigation only — it is never shown to
/// users. Format: <c>PageName_Operation_Failure</c> for UI catch
/// sites, <c>Class_Method_Failure</c> for backend throw/failure
/// sites.
/// </para>
/// </remarks>
public static class UnexpectedErrorCodes
{
    // ════════════════════════════════════════════════════════════════════
    // TIER 1 — Backend INVARIANT throws (should never fire; if they do,
    // it's a programmer error / data corruption / capacity ceiling).
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Notifications/Commands/BroadcastFanout.cs:97</c><br/>
    /// <b>Class</b>: switch-default sanity check on <c>BroadcastScope</c> enum.<br/>
    /// <b>Means</b>: an unknown <c>BroadcastScope</c> value reached the
    /// fanout. Either the enum gained a new variant not handled here, or
    /// reflection/binary-deserialization produced an undefined value.<br/>
    /// <b>Fix</b>: extend the switch in <c>BroadcastFanout.ResolveRecipientsAsync</c>
    /// to handle the new variant, OR audit the caller sending the undefined value.
    /// </summary>
    public const string BroadcastFanout_UnknownScope = "57THJ48";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Persistence/Repositories/SaleRepository.cs:258</c><br/>
    /// <b>Class</b>: defensive duplicate of the <c>DeleteDraftSaleCommandhandler</c> business-rule check.<br/>
    /// <b>Means</b>: <c>HardDeleteDraftAsync</c> was called on a Sale whose Status is not Draft.
    /// The handler should have caught this first; if this fires, the handler's guard
    /// is missing or bypassed.<br/>
    /// <b>Fix</b>: ensure every caller of <c>HardDeleteDraftAsync</c> validates Status==Draft first.
    /// </summary>
    public const string SaleRepository_HardDeleteNonDraft = "78PGN68";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UnitOfWork.cs:221</c><br/>
    /// <b>Class</b>: compiler-flow-analysis throw — the retry loop's
    /// contract guarantees a return or throw before reaching this line.<br/>
    /// <b>Means</b>: the retry loop's logic is broken (an internal bug).<br/>
    /// <b>Fix</b>: audit <c>ExecuteWithRetryAsync</c> for unreachable
    /// paths that allow fall-through.
    /// </summary>
    public const string UnitOfWork_RetryExhausted = "24FSM83";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/SaleNumberGenerator.cs:252</c><br/>
    /// <b>Class</b>: system capacity ceiling — the 8-digit Persian-year sale
    /// sequence is exhausted (more than <c>SaleNumber.MaxSequence</c> sales in one year).<br/>
    /// <b>Means</b>: either extreme data volume or a runaway sequence-allocation bug.<br/>
    /// <b>Fix</b>: extend the SaleNumber format to 9+ digits (schema change), OR
    /// audit the sequence-allocation path for leaks.
    /// </summary>
    public const string SaleNumberGenerator_SequenceCapacityReached = "84VFN95";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Domain/Notifications/Entities/BroadcastNotification.cs:259</c><br/>
    /// <b>Class</b>: entity-factory enum guard — <c>BroadcastScope</c> is not a defined enum value.<br/>
    /// <b>Means</b>: same as <see cref="BroadcastFanout_UnknownScope"/> — undefined scope reached the factory.
    /// The <c>SendBroadcastNotificationCommandValidator</c> should have caught this earlier.<br/>
    /// <b>Fix</b>: extend the validator to reject undefined scope values explicitly.
    /// </summary>
    public const string BroadcastNotification_InvalidScope = "82PXW45";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Persistence/Repositories/SystemSettingsRepository.cs:126</c><br/>
    /// <b>Class</b>: DB CHECK-constraint violation surfaced from the
    /// singleton-row guard. The INSERT failed and the subsequent load
    /// returned null.<br/>
    /// <b>Means</b>: the <c>CK_SystemSettings_Id_IsSingleton</c> constraint is
    /// missing or the singleton row was deleted out-of-band.<br/>
    /// <b>Fix</b>: inspect the DB schema — verify the CHECK constraint is in place
    /// and the singleton row exists with <c>Id = '00000000-0000-0000-0000-000000000000'</c>.
    /// </summary>
    public const string SystemSettingsRepository_SingletonMissing = "97CKC67";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/SaleNumberGenerator.cs:216</c><br/>
    /// <b>Class</b>: DB connection missing — <c>ApplicationDbContext</c>
    /// has no connection string configured at runtime.<br/>
    /// <b>Means</b>: <c>TakOneDatabaseOptions</c> did not validate at startup,
    /// OR the DbContext was constructed outside the DI container (e.g. a test).<br/>
    /// <b>Fix</b>: verify the connection string is bound in <c>Program.cs</c> /
    /// <c>appsettings.json</c> and that <c>TakOneDatabaseOptions.Validate</c>
    /// ran at startup.
    /// </summary>
    public const string SaleNumberGenerator_NoConnectionString = "38GWN42";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/SaleNumberGenerator.cs:492</c><br/>
    /// <b>Class</b>: DB concurrency anomaly — the sequence-counter row for the
    /// current Persian year disappeared between a failed INSERT and the retry UPDATE.<br/>
    /// <b>Means</b>: a concurrent process deleted the row mid-allocation.
    /// Caught by <c>UnitOfWork.ExecuteWithRetryAsync</c>; surfaces to UI only if
    /// retry exhausts.<br/>
    /// <b>Fix</b>: re-seed the counter row for the current Persian year, audit
    /// any out-of-band deletes.
    /// </summary>
    public const string SaleNumberGenerator_CounterRowDisappeared = "72KRX32";

    // ════════════════════════════════════════════════════════════════════
    // TIER 2 — Backend Result.Failure sites that surface generic /
    // infrastructure-failure messages to the UI.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Customers/Queries/GetAllCustomerGroups/GetAllCustomerGroupsQueryHandler.cs:53</c><br/>
    /// <b>Class</b>: catch-all on repository load failure — returns "Could not
    /// load customer groups. Please try again." to the UI.<br/>
    /// <b>Means</b>: an arbitrary <c>Exception</c> was thrown by the repository
    /// (typically DB connectivity or a transient SqlException).<br/>
    /// <b>Fix</b>: inspect the server log for the inner exception; verify DB
    /// connectivity and that the CustomerGroups table is accessible.
    /// </summary>
    public const string GetAllCustomerGroups_LoadFailed = "37VFT74";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Sales/Commands/SubmitSale/SubmitSaleCommandhandler.cs:157</c><br/>
    /// <b>Class</b>: defensive data-integrity failure — the customer associated
    /// with the sale was not found at submit time (was loaded at sale-create time).<br/>
    /// <b>Means</b>: the customer was deleted (soft or hard) between cart-create
    /// and submit. Should never fire in normal operation.<br/>
    /// <b>Fix</b>: audit <c>UserRepository.GetByIdAsync</c> callers; consider
    /// re-validating customer existence just before submit.
    /// </summary>
    public const string SubmitSale_CustomerDisappeared = "94BDW62";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Sales/Commands/AddItemToSale/AddItemToSaleCommandHandler.cs:209</c><br/>
    /// <b>Class</b>: same defensive data-integrity pattern as
    /// <see cref="SubmitSale_CustomerDisappeared"/>, but on the add-item path.<br/>
    /// <b>Means</b>: customer deleted mid-session.<br/>
    /// <b>Fix</b>: same as <see cref="SubmitSale_CustomerDisappeared"/>.
    /// </summary>
    public const string AddItemToSale_CustomerDisappeared = "97YNH74";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Sales/Commands/updateSaleLineItem/UpdateSaleLineItemCommandHandler.cs:153</c><br/>
    /// <b>Class</b>: same defensive data-integrity pattern on the update-line path.<br/>
    /// <b>Means</b>: customer deleted mid-session.<br/>
    /// <b>Fix</b>: same as <see cref="SubmitSale_CustomerDisappeared"/>.
    /// </summary>
    public const string UpdateSaleLineItem_CustomerDisappeared = "53XBS88";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:467</c><br/>
    /// <b>Class</b>: <c>ResetPasswordAsync</c> — Identity <c>ResetPasswordAsync</c>
    /// returned a failed <c>IdentityResult</c>. The flattened error string
    /// could be either validation (<c>PasswordTooShort</c>) or token (<c>InvalidToken</c>)
    /// — the page does fragile string-parsing on the message to decide presentation.<br/>
    /// <b>Means</b>: most likely an expired/invalid token; could also be a
    /// password-complexity failure. The localizer distinguishes these via
    /// <c>IdentityErrorMessages</c> keys, but the verbatim string surfaces in
    /// fa-IR mode if no key matches.<br/>
    /// <b>Fix</b>: refactor <c>ResetPasswordAsync</c> to return a stable-code
    /// (e.g., <c>IdentityErrors.InvalidToken</c> / <c>IdentityErrors.PasswordMismatch</c>)
    /// instead of a flattened string.
    /// </summary>
    public const string UserAccountService_ResetPasswordFailed = "27WPP94";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:237</c><br/>
    /// <b>Class</b>: <c>CreateIdentityAccountAsync</c> — <c>AddToRoleAsync</c> failed.<br/>
    /// <b>Means</b>: the target role doesn't exist (role-seed hasn't run), or
    /// the Identity store hit a DB error.<br/>
    /// <b>Fix</b>: verify <c>RoleSeeder</c> ran at startup; verify DB connectivity;
    /// inspect the flattened Identity error for specifics.
    /// </summary>
    public const string UserAccountService_AddToRoleFailed = "65RCC78";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:287</c><br/>
    /// <b>Class</b>: <c>ResetPasswordAsync</c> — <c>RemovePasswordAsync</c> failed.<br/>
    /// <b>Means</b>: DB connectivity issue or Identity store failure mid-reset.<br/>
    /// <b>Fix</b>: inspect the flattened Identity error; verify DB connectivity.
    /// </summary>
    public const string UserAccountService_RemovePasswordFailed = "44TCB42";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:512</c><br/>
    /// <b>Class</b>: <c>AssignRoleAsync</c> — <c>AddToRoleAsync</c> failed.<br/>
    /// <b>Means</b>: same as <see cref="UserAccountService_AddToRoleFailed"/>.<br/>
    /// <b>Fix</b>: same as <see cref="UserAccountService_AddToRoleFailed"/>.
    /// </summary>
    public const string UserAccountService_AssignRoleFailed = "72SCH97";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:551</c><br/>
    /// <b>Class</b>: <c>RemoveFromRoleAsync</c> — <c>RemoveFromRoleAsync</c> failed.<br/>
    /// <b>Means</b>: DB connectivity issue or Identity store failure mid-role-removal.<br/>
    /// <b>Fix</b>: inspect the flattened Identity error; verify DB connectivity.
    /// </summary>
    public const string UserAccountService_RemoveFromRoleFailed = "29QRJ72";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Infrastructure/Services/UserAccountService.cs:723</c><br/>
    /// <b>Class</b>: <c>SetUserActiveStatusAsync</c> — <c>UserManager.UpdateAsync</c> failed.<br/>
    /// <b>Means</b>: DB concurrency (optimistic-lock collision) or DB connectivity issue.<br/>
    /// <b>Fix</b>: inspect the flattened Identity error; verify DB connectivity;
    /// consider retry on optimistic-lock collisions.
    /// </summary>
    public const string UserAccountService_UpdateUserFailed = "59MTJ75";

    /// <summary>
    /// <b>File</b>: <c>TakOne.Application/Common/Middlewares/AuthorizationMiddleware.cs:118</c><br/>
    /// <b>Class</b>: fail-closed defense — a Command/Query reached the middleware
    /// without any of <c>[RequireRoles]</c> / <c>[RequireAuthentication]</c> /
    /// <c>[RequireSystemInternal]</c>.<br/>
    /// <b>Means</b>: <c>AuthorizationPolicyVerifier</c> should have caught this
    /// at startup; if it fires at runtime, a message bypassed the startup scan
    /// (e.g., dynamically-constructed message type).<br/>
    /// <b>Fix</b>: add the missing auth attribute to the offending Command/Query;
    /// if dynamic, extend <c>AuthorizationPolicyVerifier</c> to scan the dynamic type.
    /// </summary>
    public const string AuthorizationMiddleware_PolicyMissing = "27JSF84";

    // ════════════════════════════════════════════════════════════════════
    // TIER 3 — Mobile Blazor pages: catch (Exception) blocks + dialog-open
    // failures + ex.Message leak sites.
    // ════════════════════════════════════════════════════════════════════

    public const string MobileManageGroups_DialogOpen = "28TZH69";
    public const string MobileManageGroups_ActivateFailure = "63HWR77";
    public const string MobileManageGroups_DeactivateFailure = "72WVS67";
    public const string MobileManageGroups_LimitModeSaveFailure = "95GRP39";
    public const string MobileSettings_ThemeSwitchFailure = "72HTZ86";
    public const string MobileUserDetail_UpdateRoleFailure = "96TFS36";
    public const string MobileUserDetail_UpdateGroupFailure = "86SHC82";
    public const string MobileProducts_AddToCartFailure = "98JQM27";
    public const string MobileProducts_ReorderFailure = "88YQB53";
    public const string MobileSaleDetail_ActionFailure = "28YCK59";
    public const string MobileAdminUsers_DialogOpen = "79VRN93";
    public const string MobileAdminUsers_ActivateFailure = "85PJZ59";
    public const string MobileAdminUsers_DeactivateFailure = "36GBW93";
    public const string MobileEditGroup_RenameFailure = "87CMT34";
    public const string MobileEditGroup_SalaryUpdateFailure = "46XRV72";
    public const string MobileEditGroup_LimitModeSaveFailure = "86CCN69";
    public const string MobileEditGroup_StatusToggleFailure = "55RNT44";
    public const string MobileAdminCategories_RenameCategoryFailure = "27YGZ93";
    public const string MobileAdminCategories_RenameSubCategoryFailure = "63PSK35";
    public const string MobileAdminCategories_RenameSubSubCategoryFailure = "45GTG99";
    public const string MobileAdminCategories_CreateCategoryFailure = "39XNP93";
    public const string MobileAdminCategories_CreateSubCategoryFailure = "52HTZ77";
    public const string MobileAdminCategories_CreateSubSubCategoryFailure = "38KHK67";
    public const string MobileAdminCategories_ToggleCategoryFailure = "68RBK96";
    public const string MobileAdminCategories_ToggleSubCategoryFailure = "44DPK88";
    public const string MobileAdminCategories_ToggleSubSubCategoryFailure = "47XSZ65";
    public const string MobileAdminProducts_RestockDialogOpen = "77ZNG94";
    public const string MobileAdminProducts_RestockFailure = "78NGP95";
    public const string MobileAdminProducts_DeactivateDialogOpen = "49YCJ45";
    public const string MobileAdminProducts_DeactivateFailure = "77VWH42";
    public const string MobileProductDetail_AddToCartFailure = "24YKB77";
    public const string MobileAdminNotifications_LoadFailure = "99XNN24";
    public const string MobileAdminNotifications_SendFailure = "67ZKP22";
    public const string MobileCart_UpdateFailure = "26BXY73";
    public const string MobileCart_RemoveFailure = "79FVY32";
    public const string MobileCart_ClearFailure = "58YDJ89";
    public const string MobileCart_SubmitFailure = "94DVV22";
    public const string MobileDashboard_LoadFailure = "23ZRC83";
    public const string MobileOrderTracker_LoadFailure = "97SZM88";
    public const string MobileCreateProduct_SubmitFailure = "96SPS53";
    public const string MobileCreateGroup_SubmitFailure = "99GNW48";
    public const string MobileCreateUser_SubmitFailure = "83YFW93";
    public const string MobileUserDetail_LoadFailure = "94HRT26";
    public const string MobileSaleDetail_LoadFailure = "73PJQ67";
    public const string MobileEditGroup_LoadFailure = "47GMB36";
    public const string MobileProductDetail_LoadFailure = "44NFR26";

    // ════════════════════════════════════════════════════════════════════
    // TIER 4 — Desktop Blazor pages: catch (Exception) blocks + dialog-open
    // failures + ex.Message leak sites + page-load failures.
    // ════════════════════════════════════════════════════════════════════

    public const string UserDetail_RemoveRoleDialogOpen = "35YNV48";
    public const string UserDetail_RemoveGroupDialogOpen = "58TQH67";
    public const string UserDetail_ActionDialogOpen = "63YWY49";
    public const string UserDetail_ActionFailure = "22ZZR25";
    public const string SaleDetail_CancelDialogOpen = "83CVW28";
    public const string SaleDetail_ActionFailure = "22WSC29";
    public const string AdminNotifications_SendFailure = "87DKG64";
    public const string Cart_UpdateFailure = "67PWB69";
    public const string Cart_RemoveFailure = "26CJV87";
    public const string Cart_ClearFailure = "43VTQ77";
    public const string Cart_SubmitFailure = "27CCP96";
    public const string AdminUsers_DialogOpen = "92PPJ99";
    public const string AdminUsers_ActivateFailure = "23VNJ38";
    public const string AdminUsers_DeactivateFailure = "33CTM82";
    public const string AdminProducts_RestockDialogOpen = "45BZN37";
    public const string AdminProducts_RestockFailure = "67QBZ86";
    public const string AdminProducts_DeactivateDialogOpen = "29WVF72";
    public const string AdminProducts_DeactivateFailure = "64XZV63";
    public const string AdminCategories_CreateCategoryFailure = "26XWN49";
    public const string AdminCategories_CreateSubCategoryFailure = "44RMZ24";
    public const string AdminCategories_CreateSubSubCategoryFailure = "26RHT74";
    public const string AdminCategories_RenameCategoryFailure = "44TYX46";
    public const string AdminCategories_RenameSubCategoryFailure = "93CVJ36";
    public const string AdminCategories_RenameSubSubCategoryFailure = "72NCP95";
    public const string AdminCategories_ToggleCategoryFailure = "72HKF48";
    public const string AdminCategories_ToggleSubCategoryFailure = "56JQF85";
    public const string AdminCategories_ToggleSubSubCategoryFailure = "45FPZ92";
    public const string ManageGroups_LimitModeSaveFailure = "24DXK75";
    public const string ManageGroups_DialogOpen = "33VDB98";
    public const string ManageGroups_ActivateFailure = "29YFH62";
    public const string ManageGroups_DeactivateFailure = "42NPZ49";
    public const string Products_AddToCartFailure = "83NFW62";
    public const string Products_UpdateCartFailure = "62PRN36";
    public const string Products_SubmitOrderFailure = "66RCS33";
    public const string Products_ReorderFailure = "63QCD42";
    public const string EditGroup_DialogOpen = "37SNB37";
    public const string EditGroup_RenameFailure = "83CFW42";
    public const string EditGroup_SalaryUpdateFailure = "98XNZ79";
    public const string EditGroup_LimitModeSaveFailure = "36FPX48";
    public const string ProductDetail_SaveBasicFailure = "44NPP59";
    public const string ProductDetail_SavePricingFailure = "43TWB26";
    public const string ProductDetail_SaveStockFailure = "74GNC32";
    public const string ProductDetail_SaveCategoryFailure = "44XRV64";
    public const string ProductDetail_SaveLimitsFailure = "84RGG54";
    public const string ProductDetail_SaveStatusFailure = "78SJZ48";
    public const string CreateProduct_UploadFailure = "69PND56";
    public const string CreateProduct_SubmitFailure = "32THP72";
    public const string AdminProducts_LoadFailure = "78VKB42";
    public const string AdminUsers_LoadFailure = "55XJK67";
    public const string Dashboard_LoadFailure = "65MSR63";
    public const string Products_LoadFailure = "55THS77";
    public const string UserDetail_LoadFailure = "76ZCR74";
    public const string SaleDetail_LoadFailure = "79BJN77";
    public const string Sales_LoadFailure = "25BST39";
    public const string ChangePassword_UnexpectedFailure = "92GWR98";
    public const string Login_UnexpectedFailure = "82CGH87";
    public const string ResetPassword_UnexpectedFailure = "89GSB98";
    public const string ProductDetail_UploadReadFailure = "36VBS86";

    // ════════════════════════════════════════════════════════════════════
    // TIER 5 — Minimal API endpoint catches (Program.cs).
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>File</b>: <c>TakOne.WebUI/Program.cs:832</c><br/>
    /// <b>Class</b>: <c>/api/product-image</c> endpoint — <c>InvalidDataException</c>
    /// from <c>LocalFileStorage</c> validation (file type/size mismatch).<br/>
    /// <b>Means</b>: client uploaded an invalid file (wrong content-type or
    /// oversized). Returns 400 with the validation message.<br/>
    /// <b>Fix</b>: see <c>LocalFileStorage.cs</c> — narrow the validation
    /// message or document the accepted types/sizes in the API contract.
    /// </summary>
    public const string ProductImageEndpoint_InvalidUpload = "66YZQ29";

    /// <summary>
    /// <b>File</b>: <c>TakOne.WebUI/Program.cs:841</c><br/>
    /// <b>Class</b>: <c>/api/product-image</c> endpoint — generic
    /// <c>Exception</c> catch returning a 500 ProblemDetails with
    /// title "Upload failed."<br/>
    /// <b>Means</b>: the storage subsystem threw an unexpected exception
    /// (IO failure, disk full, unauthorized access).<br/>
    /// <b>Fix</b>: inspect the server log for the inner exception;
    /// verify disk space + write permissions on the storage root.
    /// </summary>
    public const string ProductImageEndpoint_UploadFailed = "54STJ34";
}
