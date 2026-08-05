namespace TakOne.Infrastructure.Localization;

/// <summary>
/// Marker class used as the type parameter for
/// <c>IStringLocalizer&lt;IdentityErrorMessages&gt;</c> in the Infrastructure
/// layer.
///
/// The matching <c>IdentityErrorMessages.{culture}.resx</c> files (located
/// next to this file in the <c>Localization</c> folder) provide the
/// translation pairs. ASP.NET Core's
/// <c>ResourceManagerStringLocalizerFactory</c> finds them automatically
/// because the file name matches the type name and the namespace matches
/// the default root namespace <c>TakOne.Infrastructure</c>.
///
/// USED BY:
///   <see cref="TakOne.Infrastructure.Identity.TakOneIdentityErrorDescriber"/>
///   to localize ASP.NET Identity's built-in error messages (password
///   complexity, duplicate email/username, invalid token, etc.) at the
///   SOURCE — i.e. inside <c>UserManager.CreateAsync</c>,
///   <c>ResetPasswordAsync</c>, etc. — instead of mapping error codes
///   after-the-fact.
///
/// WHY THIS EXISTS:
///   Identity's default <c>IdentityErrorDescriber</c> always returns English
///   strings, regardless of the current culture. When a Persian user
///   submits a weak password on the Create User page, the resulting error
///   ("PasswordRequiresNonAlphanumeric: Passwords must have at least one
///   non alphanumeric character.") was rendered verbatim under a Persian
///   "خطایی رخ داد" heading — half the message English, half Persian.
///   Registering a custom describer that resolves strings via
///   <c>IStringLocalizer&lt;IdentityErrorMessages&gt;</c> makes Identity
///   itself return the localized message, so EVERY call site
///   (UserManager, SignInManager, ResetPassword flow, ForgotPassword
///   flow) gets the localized message for free — no per-call-site
///   mapping required.
/// </summary>
public class IdentityErrorMessages { }