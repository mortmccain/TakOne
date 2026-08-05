using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using TakOne.Infrastructure.Localization;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// Localized ASP.NET Identity error describer for TakOne.
///
/// REPLACES Identity's default <see cref="IdentityErrorDescriber"/>, which
/// always returns English strings regardless of the current culture. The
/// default describer is what produced the user-visible bug:
///
///     خطایی رخ داد   ← (CreateUser.fa-IR.resx, correctly localized)
///     PasswordRequiresNonAlphanumeric: Passwords must have at least one
///     non alphanumeric character.   ← (Identity's default, English)
///
/// This subclass overrides every method that the project's Identity flows
/// (Create User, Reset Password, Forgot Password, Change Password) can
/// surface to a user. Each override returns an <see cref="IdentityError"/>
/// whose <c>Code</c> is left as Identity's default (so log greps and
/// error-code switches keep working) and whose <c>Description</c> is
/// resolved from <c>IStringLocalizer&lt;IdentityErrorMessages&gt;</c> —
/// which in turn reads from
/// <c>IdentityErrorMessages.{culture}.resx</c> in this folder.
///
/// REGISTRATION:
///   Wired in <c>AddTakOneInfrastructure</c> (ServiceCollectionExtensions.cs)
///   via <c>.AddErrorDescriber&lt;TakOneIdentityErrorDescriber&gt;()</c> on
///   the <c>AddIdentity&lt;...&gt;()</c> chain. Identity resolves the
///   describer from DI on every <c>UserManager.CreateAsync</c>,
///   <c>ResetPasswordAsync</c>, etc. call.
///
/// WHY THIS IS BETTER THAN MAPPING AFTER-THE-FACT:
///   An alternative fix is to translate the <c>IdentityError.Code</c> to a
///   localized string in <c>UserAccountService.FlattenErrors</c>. But that
///   only fixes the Create User flow — it leaves ResetPassword,
///   ForgotPassword, and any future Identity call sites still returning
///   English. Overriding the describer at the SOURCE means every Identity
///   operation in the entire app returns the localized message for free.
///
/// PLACEHOLDERS:
///   Several of Identity's describer methods take a numeric or string
///   argument (e.g. <c>PasswordTooShort(int length)</c>). We pass these
///   through to the resx value via <c>string.Format</c> — the resx value
///   uses <c>{0}</c> as the placeholder, exactly like Identity's default
///   English strings do.
///
/// CULTURE RESOLUTION:
///   <c>IStringLocalizer&lt;T&gt;</c> reads
///   <c>CultureInfo.CurrentUICulture</c>, which is set per-request by
///   <c>RequestLocalizationMiddleware</c> (configured in Program.cs —
///   fa-IR default, en-US secondary, cookie name <c>takone_culture</c>).
///   So this describer returns Persian for fa-IR requests and English for
///   en-US requests, with no per-flow branching.
/// </summary>
public sealed class TakOneIdentityErrorDescriber : IdentityErrorDescriber
{
    private readonly IStringLocalizer<IdentityErrorMessages> _localizer;

    public TakOneIdentityErrorDescriber(IStringLocalizer<IdentityErrorMessages> localizer)
    {
        _localizer = localizer;
    }

    // ── Password complexity ────────────────────────────────────────────

    /// <inheritdoc />
    public override IdentityError PasswordTooShort(int length)
    {
        return new IdentityError
        {
            Code = nameof(PasswordTooShort),
            Description = string.Format(_localizer["PasswordTooShort"], length)
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordRequiresNonAlphanumeric()
    {
        return new IdentityError
        {
            Code = nameof(PasswordRequiresNonAlphanumeric),
            Description = _localizer["PasswordRequiresNonAlphanumeric"]
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordRequiresDigit()
    {
        return new IdentityError
        {
            Code = nameof(PasswordRequiresDigit),
            Description = _localizer["PasswordRequiresDigit"]
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordRequiresUpper()
    {
        return new IdentityError
        {
            Code = nameof(PasswordRequiresUpper),
            Description = _localizer["PasswordRequiresUpper"]
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordRequiresLower()
    {
        return new IdentityError
        {
            Code = nameof(PasswordRequiresLower),
            Description = _localizer["PasswordRequiresLower"]
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars)
    {
        return new IdentityError
        {
            Code = nameof(PasswordRequiresUniqueChars),
            Description = string.Format(_localizer["PasswordRequiresUniqueChars"], uniqueChars)
        };
    }

    // ── Duplicate user / email ─────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// In TakOne, <c>UserName</c> IS the <c>WorkerId</c> — the user-facing
    /// copy says "worker ID" instead of "user name" so the message matches
    /// the rest of the app's vocabulary.
    /// </remarks>
    public override IdentityError DuplicateUserName(string userName)
    {
        return new IdentityError
        {
            Code = nameof(DuplicateUserName),
            Description = string.Format(_localizer["DuplicateUserName"], userName)
        };
    }

    /// <inheritdoc />
    public override IdentityError DuplicateEmail(string email)
    {
        return new IdentityError
        {
            Code = nameof(DuplicateEmail),
            Description = string.Format(_localizer["DuplicateEmail"], email)
        };
    }

    // ── Invalid user / email format ────────────────────────────────────

    /// <inheritdoc />
    public override IdentityError InvalidUserName(string? userName)
    {
        return new IdentityError
        {
            Code = nameof(InvalidUserName),
            Description = string.Format(_localizer["InvalidUserName"], userName)
        };
    }

    /// <inheritdoc />
    public override IdentityError InvalidEmail(string? email)
    {
        return new IdentityError
        {
            Code = nameof(InvalidEmail),
            Description = string.Format(_localizer["InvalidEmail"], email)
        };
    }

    // ── Token / password reset ─────────────────────────────────────────

    /// <inheritdoc />
    public override IdentityError InvalidToken()
    {
        return new IdentityError
        {
            Code = nameof(InvalidToken),
            Description = _localizer["InvalidToken"]
        };
    }

    /// <inheritdoc />
    public override IdentityError PasswordMismatch()
    {
        return new IdentityError
        {
            Code = nameof(PasswordMismatch),
            Description = _localizer["PasswordMismatch"]
        };
    }

    // ── Default fallback ───────────────────────────────────────────────

    /// <inheritdoc />
    public override IdentityError DefaultError()
    {
        return new IdentityError
        {
            Code = nameof(DefaultError),
            Description = _localizer["DefaultError"]
        };
    }
}
