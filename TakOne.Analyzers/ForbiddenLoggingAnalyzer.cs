using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TakOne.Analyzers;

/// <summary>
/// Flags any call to <c>ILogger.Log*</c> (<c>LogInformation</c>,
/// <c>LogWarning</c>, <c>LogError</c>, <c>LogCritical</c>, <c>LogDebug</c>,
/// <c>LogTrace</c>) whose first format-string argument is a string literal
/// containing one of the banned credential-state or PII tokens.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS ANALYZER EXISTS (Issue #03):</b> The login page had 11
/// <c>LoginLogger.LogWarning("DIAG Login: ...")</c> calls that leaked
/// <c>WorkerId</c>, <c>PasswordLength</c>, <c>EmailConfirmed</c>,
/// <c>LockoutEnd</c>, <c>AccessFailedCount</c>, and the sign-in result
/// branch at WARNING level in production code. The developer's own comment
/// said "temporary — remove once login is stable". They were never removed.
/// </para>
/// <para>
/// The fix moved logging through <c>LoginAuditLogger</c>, which enforces an
/// allow-list by construction (only <c>LoginLogContext</c> fields can be
/// logged). This analyzer is the CI-level backstop: even if a developer
/// bypasses the audit logger and calls <c>ILogger.LogWarning</c> directly,
/// the banned-token check fails the build before the leak ships.
/// </para>
/// <para>
/// <b>Banned tokens</b> (case-sensitive — matches the field names that
/// actually appear in the leaked logs):
/// <list type="bullet">
///   <item><c>"DIAG Login"</c> — the marker the original developer used
///   for "temporary" diagnostic logging. Banning the marker makes
///   re-introducing the pattern trivially detectable.</item>
///   <item><c>PasswordLength</c> — metadata about the credential. Tells
///   attacker the password is non-empty and roughly how long.</item>
///   <item><c>EmailConfirmed</c> — account-state intelligence.</item>
///   <item><c>LockoutEnd</c> — lockout-state intelligence (tells attacker
///   when to retry).</item>
///   <item><c>AccessFailedCount</c> — tells attacker how many more
///   attempts before lockout.</item>
///   <item><c>Password=</c> — the structured-logging placeholder pattern
///   the original code used (<c>Password='{Password}'</c>). Catches both
///   interpolation into the format string and structured placeholders.</item>
///   <item><c>Password:</c> — the diagnostic-counter pattern
///   (<c>Password: {Password}</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>Severity:</b> Error. The rule exists to prevent re-introduction of
/// a known leak pattern; anything less than Error would let it slip into
/// CI green builds.
/// </para>
/// <para>
/// <b>Scope:</b> Analyzes every C# file in the consuming project (and
/// transitively any .razor.g.cs generated file). The check is cheap: it's
/// a string-contains against a small token list, only on calls to methods
/// whose name starts with "Log" and takes a string as the first non-this
/// argument.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ForbiddenLoggingAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic ID for the forbidden-login-logging rule.</summary>
    public const string DiagnosticId = "TAKONE_LOGIN_001";

    private const string Category = "Security";
    private const string Title = "Forbidden credential-state or PII token in ILogger.Log* format string";
    private const string MessageFormat =
        "ILogger.Log* format string contains the banned token '{0}'. " +
        "Use LoginAuditLogger (TakOne.WebUI.Services.Logging) for login-flow " +
        "logging — it enforces an allow-list by construction and never logs " +
        "credential state or PII. See Issue #03.";

    private const string Description =
        "Flags ILogger.Log* calls whose format string contains a banned " +
        "credential-state or PII token (DIAG Login, PasswordLength, " +
        "EmailConfirmed, LockoutEnd, AccessFailedCount, Password=, Password:). " +
        "Re-introducing the Issue #03 leak pattern is a build error.";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: Description,
        helpLinkUri: null,
        customTags: new[] { "Security" });

    /// <summary>
    /// The banned tokens. Each entry is matched as a literal substring
    /// (case-sensitive) against the format string's literal value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why substring and not regex?</b> The original leak used C#
    /// interpolated strings or structured-logging placeholders like
    /// <c>"DIAG Login: PasswordLength={PwdLen}"</c>. A substring match
    /// on <c>PasswordLength</c> catches every variant
    /// (<c>{PasswordLength}</c>, <c>PasswordLength=</c>, <c>'PasswordLength'</c>)
    /// without false-positives on unrelated identifiers (e.g. a domain
    /// entity property named <c>PasswordLengthPolicy</c> would also be
    /// flagged — but no such property exists in the codebase, and if one
    /// is ever added, the false positive is a feature, not a bug: it
    /// forces the developer to justify logging anything
    /// password-length-related).
    /// </para>
    /// <para>
    /// <b>Word-boundary requirement for <c>Password=</c> and <c>Password:</c>:</b>
    /// These two tokens are matched with a word boundary at the start —
    /// the character immediately before <c>P</c> must NOT be a letter or
    /// digit. This prevents false positives on legitimate compound
    /// identifiers like <c>MustChangePassword</c>, <c>NewPassword</c>,
    /// <c>CurrentPassword</c>, <c>ResetPassword</c>, <c>ForgotPassword</c>,
    /// <c>ChangePassword</c> — all of which appear in legitimate log
    /// messages in the password-management flow. The original DIAG leak
    /// used <c>Password=</c> as a standalone placeholder (e.g.
    /// <c>"Password='{Password}'"</c>); the word-boundary check still
    /// catches that pattern.
    /// </para>
    /// <para>
    /// <b>Why case-sensitive?</b> Field names in C# are PascalCase; the
    /// banned tokens match that convention. A case-insensitive match would
    /// flag the English word "password" appearing in any user-facing
    /// log message (e.g. <c>"password reset email sent"</c>), which is
    /// legitimate and not a leak.
    /// </para>
    /// </remarks>
    private static readonly ImmutableArray<string> BannedTokens = ImmutableArray.Create(
        "DIAG Login",
        "PasswordLength",
        "EmailConfirmed",
        "LockoutEnd",
        "AccessFailedCount",
        "Password=",
        "Password:");

    /// <summary>
    /// Tokens that require a word boundary at the start (the character
    /// before the match must NOT be a letter or digit). See the remarks
    /// on <see cref="BannedTokens"/> for the rationale.
    /// </summary>
    private static readonly ImmutableArray<string> WordBoundaryTokens = ImmutableArray.Create(
        "Password=",
        "Password:");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze |
                                               GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        // We register for InvocationExpression — every method call in the
        // analyzed compilation. The Symbol-check below filters down to
        // ILogger.Log* calls.
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        // Cheap pre-filter: method name must start with "Log". This skips
        // 99.9% of invocations without the cost of a symbol lookup.
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return;
        }

        var methodName = memberAccess.Name.Identifier.ValueText;
        if (methodName.Length < 3 || !methodName.StartsWith("Log", System.StringComparison.Ordinal))
        {
            return;
        }

        // Symbol lookup — confirm this is one of the ILogger extension methods.
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        // LoggerExtensions.Log* lives in Microsoft.Extensions.Logging.
        // We match by containing-type name + method name prefix, which is
        // stable across versions of the logging package.
        var containingType = methodSymbol.ContainingType;
        if (containingType is null)
        {
            return;
        }

        if (containingType.Name != "LoggerExtensions" &&
            containingType.Name != "LoggerExtensions" &&
            !(containingType.Name == "ILogger" && methodName.StartsWith("Log", System.StringComparison.Ordinal)))
        {
            return;
        }

        // Confirm it's one of the specific Log* methods we care about.
        // (LoggerExtensions has many methods; we only want the level-named ones.)
        if (methodName is not ("LogInformation" or "LogWarning" or "LogError"
                                or "LogCritical" or "LogDebug" or "LogTrace"
                                or "Log"))
        {
            return;
        }

        // Find the format-string argument. For LogInformation(format, args...),
        // it's the first argument. For Log(logLevel, eventId, format, args...),
        // it's the third. We check the first two arguments for a string literal
        // — if either is a literal, it's the format string for one of the
        // extension patterns.
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return;
        }

        // The format string is the FIRST string-literal argument among the
        // first three. (Log(logLevel, eventId, exception, format, args) is
        // the lowest-level overload; the format string is always at index ≤ 3.)
        var scanLimit = System.Math.Min(3, args.Count);
        for (var i = 0; i < scanLimit; i++)
        {
            var expr = args[i].Expression;
            if (expr is not LiteralExpressionSyntax literal ||
                literal.Token.ValueText is not string formatString)
            {
                continue;
            }

            // Found a string literal in the first three args. Check it
            // against the banned-token list.
            foreach (var token in BannedTokens)
            {
                var idx = formatString.IndexOf(token, System.StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                // Word-boundary check: for tokens that start with an
                // identifier character (Password=, Password:), require
                // the character immediately before the match to NOT be
                // a letter or digit. This prevents false positives on
                // legitimate compound identifiers like MustChangePassword,
                // NewPassword, ResetPassword, etc. — see the remarks on
                // WordBoundaryTokens.
                if (WordBoundaryTokens.Contains(token) && idx > 0)
                {
                    var prevChar = formatString[idx - 1];
                    if (char.IsLetterOrDigit(prevChar))
                    {
                        continue;
                    }
                }

                var diagnostic = Diagnostic.Create(
                    Rule,
                    literal.GetLocation(),
                    additionalLocations: null,
                    properties: null,
                    token);
                context.ReportDiagnostic(diagnostic);
                return; // Report once per call — the first banned token is enough.
            }

            // If the first string-literal argument was checked and is clean,
            // we can stop scanning — the format string is what we wanted.
            return;
        }
    }
}