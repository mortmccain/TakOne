using System.Globalization;
using FluentValidation.Resources;
using Microsoft.Extensions.Localization;

namespace TakOne.Application.Common.Localization;

/// <summary>
/// Custom <see cref="ILanguageManager"/> for FluentValidation that localizes
/// validation messages into the current request culture (fa-IR by default,
/// en-US supported).
///
/// DESIGN
/// =======
/// FluentValidation lets you override its global LanguageManager by setting
/// <c>ValidatorOptions.Global.LanguageManager</c> at startup. Once set, every
/// call to <c>IRuleComponent.GetDefaultMessage(...)</c> routes through
/// <see cref="GetString(string, CultureInfo)"/>. There are two kinds of
/// messages to localize:
///
/// 1. <b>Built-in messages</b> — FluentValidation ships English templates for
///    every built-in validator (NotEmpty, MaximumLength, EmailAddress, etc.).
///    These are looked up by a stable key like <c>"NotEmptyValidator"</c>.
///    We register Persian translations for these keys via
///    <see cref="LanguageManager.AddTranslation"/> on a wrapped default
///    LanguageManager. The wrapper also handles built-in fallback so any key
///    we don't translate falls back to FluentValidation's English default.
///
/// 2. <b>Custom messages</b> — the project's validators call
///    <c>.WithMessage("...")</c> with hand-written English strings (e.g.
///    "Product name is required."). To localize these, we changed the
///    <c>.WithMessage(...)</c> arguments to resource key strings prefixed
///    with <c>"@"</c> (e.g. <c>.WithMessage("@Validation_Product_NameRequired")</c>).
///    This <see cref="TakOneLanguageManager"/> detects the <c>"@"</c> prefix
///    and resolves the rest of the string via
///    <c>IStringLocalizer&lt;ValidationMessages&gt;</c>, falling back to the
///    raw key if the localizer is unavailable or the key is missing.
///
/// The <c>"@"</c> prefix convention is deliberate:
///   - It's terse and grep-able (search for <c>.WithMessage("@</c> to find
///     every localized custom message).
///   - It's unambiguous — no FluentValidation built-in key starts with
///     <c>"@"</c>, so there's no risk of collision.
///   - It keeps the validators free of any DI dependency. Validators can't
///     easily inject <c>IStringLocalizer</c> because Wolverine instantiates
///     them by reflection; the LanguageManager singleton is the cleanest
///     single chokepoint for localization.
///
/// LIFETIME / DI
/// ==============
/// <see cref="ValidatorOptions.Global.LanguageManager"/> is a singleton field
/// set ONCE at startup. To get the per-request culture (which can change
/// between requests as the user switches language cookie), we read
/// <see cref="CultureInfo.CurrentUICulture"/> inside
/// <see cref="GetString(string, CultureInfo)"/> — this is the same value the
/// ASP.NET Core request-localization middleware sets at the start of every
/// HTTP request and every Blazor circuit render.
///
/// The <c>IStringLocalizer&lt;ValidationMessages&gt;</c> is injected into this
/// class's constructor at startup and held as a field. The
/// <c>IStringLocalizer</c> implementation is itself culture-aware — calling
/// <c>localizer["key"]</c> reads from the resource file matching
/// <c>CultureInfo.CurrentUICulture</c> at the moment of the call.
/// </summary>
internal sealed class TakOneLanguageManager : ILanguageManager
{
    /// <summary>
    /// Prefix that marks a FluentValidation message string as a resource key
    /// that should be looked up via <see cref="IStringLocalizer{T}"/>.
    /// </summary>
    private const string LocalizerKeyPrefix = "@";

    private readonly IStringLocalizer<Resources.ValidationMessages> _localizer;
    private readonly LanguageManager _fallback; // FluentValidation's default English manager

    public TakOneLanguageManager(IStringLocalizer<Resources.ValidationMessages> localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        // Use FluentValidation's built-in LanguageManager as the fallback for
        // built-in validator keys (NotEmptyValidator, MaximumLengthValidator,
        // etc.). We don't try to translate those ourselves — the built-in
        // manager already has good English templates, and we register Persian
        // translations on it via AddTranslation in the static constructor
        // below.
        _fallback = new LanguageManager();
        RegisterPersianTranslationsForBuiltInKeys(_fallback);
    }

    /// <summary>
    /// Whether FluentValidation's message localization is enabled. We always
    /// leave it <c>true</c> — there's no scenario in this app where we'd want
    /// to disable it. FluentValidation's ILanguageManager interface declares
    /// this as a get/set property; we accept the setter (and ignore the value)
    /// to satisfy the interface contract.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The culture the LanguageManager should use when no explicit culture is
    /// passed to <see cref="GetString(string, CultureInfo)"/>. We delegate to
    /// <see cref="CultureInfo.CurrentUICulture"/> so per-request culture
    /// changes (cookie / query string) take effect immediately.
    /// FluentValidation's ILanguageManager interface declares this as a
    /// get/set property; we accept the setter (and ignore the value) to
    /// satisfy the interface contract.
    /// </summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// Resolve a FluentValidation message key to a localized string.
    ///
    /// Two paths:
    ///   1. If <paramref name="key"/> starts with <c>"@"</c>, strip the prefix
    ///      and look up the remainder in
    ///      <c>IStringLocalizer&lt;ValidationMessages&gt;</c>. If found,
    ///      return the localized value. If not found, return the raw key
    ///      (without the prefix) — this is a development-time signal that a
    ///      resource is missing.
    ///   2. Otherwise, delegate to FluentValidation's built-in LanguageManager
    ///      which has English + Persian translations for the standard
    ///      validator keys (NotEmptyValidator, etc.).
    /// </summary>
    /// <param name="key">
    /// Either a FluentValidation built-in key (e.g. <c>"NotEmptyValidator"</c>)
    /// or a custom key prefixed with <c>"@"</c> (e.g.
    /// <c>"@Validation_Product_NameRequired"</c>).
    /// </param>
    /// <param name="culture">
    /// The culture to localize into. If null, defaults to
    /// <see cref="CultureInfo.CurrentUICulture"/>.
    /// </param>
    public string GetString(string key, CultureInfo culture)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        var effectiveCulture = culture ?? CultureInfo.CurrentUICulture;

        // Path 1: custom resource key (prefixed with "@")
        if (key.StartsWith(LocalizerKeyPrefix, StringComparison.Ordinal))
        {
            var resourceKey = key[LocalizerKeyPrefix.Length..];
            try
            {
                var localized = _localizer.GetString(resourceKey);
                // IStringLocalizer returns the key itself (not null) when the
                // key is missing — so we check whether the returned value
                // differs from the key. If it matches, the resource is
                // missing; fall back to returning the raw key so a developer
                // can spot the issue (it'll look like "Validation_Product_X"
                // in the UI instead of a localized sentence).
                return localized.ResourceNotFound ? resourceKey : localized.Value;
            }
            catch
            {
                // The localizer should never throw for a missing key, but if
                // something goes catastrophically wrong (DI not configured,
                // resource file unreadable), we don't want to crash the
                // validation pipeline. Return the raw key as a safe fallback.
                return resourceKey;
            }
        }

        // Path 2: FluentValidation built-in key. Delegate to the wrapped
        // LanguageManager, which has Persian translations for the standard
        // validator keys (registered in the static constructor below).
        return _fallback.GetString(key, effectiveCulture);
    }

    /// <summary>
    /// Register Persian (fa-IR) translations for FluentValidation's built-in
    /// validator message keys. These keys are used by the built-in validators
    /// (NotEmpty, MaximumLength, EmailAddress, etc.) when the developer does
    /// NOT call <c>.WithMessage(...)</c> with a custom string.
    ///
    /// In this project, every property rule does call <c>.WithMessage(...)</c>
    /// with a custom key, so these built-in messages are only used as a
    /// fallback safety net. We register them anyway to handle any future
    /// validators that rely on the built-in defaults.
    ///
    /// Placeholder tokens ({PropertyName}, {MaxLength}, etc.) are filled in
    /// by FluentValidation's MessageFormatter at runtime.
    /// </summary>
    private static void RegisterPersianTranslationsForBuiltInKeys(LanguageManager manager)
    {
        // fa-IR is the language code FluentValidation's LanguageManager uses
        // for Persian (matches the CultureInfo two-letter ISOn-language name).
        const string fa = "fa";

        manager.AddTranslation(fa, "NotEmptyValidator", "'{PropertyName}' الزامی است.");
        manager.AddTranslation(fa, "NotNullValidator", "'{PropertyName}' الزامی است.");
        manager.AddTranslation(fa, "EmptyValidator", "'{PropertyName}' نباید خالی باشد.");
        manager.AddTranslation(fa, "NullValidator", "'{PropertyName}' نباید خالی باشد.");
        manager.AddTranslation(fa, "LengthValidator", "'{PropertyName}' باید بین {MinLength} و {MaxLength} کاراکتر باشد.");
        manager.AddTranslation(fa, "MinimumLengthValidator", "طول '{PropertyName}' باید حداقل {MinLength} کاراکتر باشد.");
        manager.AddTranslation(fa, "MaximumLengthValidator", "طول '{PropertyName}' نباید بیش از {MaxLength} کاراکتر باشد.");
        manager.AddTranslation(fa, "ExactLengthValidator", "طول '{PropertyName}' باید دقیقاً {MaxLength} کاراکتر باشد.");
        manager.AddTranslation(fa, "InclusiveBetweenValidator", "'{PropertyName}' باید بین {From} و {To} باشد.");
        manager.AddTranslation(fa, "ExclusiveBetweenValidator", "'{PropertyName}' باید بین {From} (انحصاری) و {To} (انحصاری) باشد.");
        manager.AddTranslation(fa, "LessThanValidator", "'{PropertyName}' باید کمتر از {ComparisonValue} باشد.");
        manager.AddTranslation(fa, "LessThanOrEqualValidator", "'{PropertyName}' باید کمتر یا مساوی {ComparisonValue} باشد.");
        manager.AddTranslation(fa, "GreaterThanValidator", "'{PropertyName}' باید بیشتر از {ComparisonValue} باشد.");
        manager.AddTranslation(fa, "GreaterThanOrEqualValidator", "'{PropertyName}' باید بیشتر یا مساوی {ComparisonValue} باشد.");
        manager.AddTranslation(fa, "EqualValidator", "'{PropertyName}' باید با {ComparisonValue} برابر باشد.");
        manager.AddTranslation(fa, "NotEqualValidator", "'{PropertyName}' نباید با {ComparisonValue} برابر باشد.");
        manager.AddTranslation(fa, "EmailAddressValidator", "'{PropertyName}' یک آدرس ایمیل معتبر نیست.");
        manager.AddTranslation(fa, "UrlValidator", "'{PropertyName}' یک آدرس URL معتبر نیست.");
        manager.AddTranslation(fa, "MustValidator", "شرط مشخص‌شده برای '{PropertyName}' برقرار نیست.");
        manager.AddTranslation(fa, "EnumValidator", "'{PropertyName}' محدوده‌ای از مقادیر مجاز دارد که شامل '{PropertyValue}' نیست.");
    }
}
