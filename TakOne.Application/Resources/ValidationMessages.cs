namespace TakOne.Application.Resources;

/// <summary>
/// Marker class used as the type parameter for
/// <c>IStringLocalizer&lt;ValidationMessages&gt;</c> in the Application layer.
///
/// The matching <c>ValidationMessages.{culture}.resx</c> files (located next
/// to this file in the <c>Resources</c> folder) provide the actual translation
/// pairs. ASP.NET Core's <c>ResourceManagerStringLocalizerFactory</c> finds
/// them automatically because the file name matches the type name and the
/// namespace matches the (default) root namespace <c>TakOne.Application</c>.
///
/// Phase 7 item D — FluentValidation message localization. See
/// <c>TakOne.Application.Common.Localization.TakOneLanguageManager</c> for the
/// consumer of these resources.
/// </summary>
internal sealed class ValidationMessages { }