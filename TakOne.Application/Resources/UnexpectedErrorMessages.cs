namespace TakOne.Application.Resources;

/// <summary>
/// Marker class used as the type parameter for
/// <c>IStringLocalizer&lt;UnexpectedErrorMessages&gt;</c>.
///
/// Provides a single shared resource key
/// <c>Unexpected_Error_Format</c> whose value is the user-facing
/// "unexpected error" message with a <c>{0}</c> placeholder for the
/// opaque 7-character reference code from
/// <see cref="TakOne.Application.Common.Errors.UnexpectedErrorCodes"/>.
///
/// The matching <c>UnexpectedErrorMessages.{culture}.resx</c> files
/// (located next to this file in the <c>Resources</c> folder) provide
/// the actual translation pairs. ASP.NET Core's
/// <c>ResourceManagerStringLocalizerFactory</c> finds them automatically
/// because the file name matches the type name and the namespace matches
/// the (default) root namespace <c>TakOne.Application</c>.
///
/// <b>Why a shared resource (vs. per-page resx)?</b>
/// <list type="bullet">
/// <item>The existing per-page <c>Error_Unexpected</c> /
/// <c>Error_Generic</c> / <c>Err_Generic</c> keys all carry the SAME
/// semantic meaning ("an unexpected error happened — try again").</item>
/// <item>Centralizing here means a future wording tweak touches ONE
/// resource, not 50+ scattered resx keys.</item>
/// <item>The visible reference code makes the message unique per
/// call-site even with a single shared template — the developer
/// looks up the code in the reference PDF to pinpoint the file/line.</item>
/// </list>
/// </summary>
public class UnexpectedErrorMessages { }
