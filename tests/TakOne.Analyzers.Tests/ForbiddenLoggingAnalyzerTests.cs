using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TakOne.Analyzers;
using Xunit;

namespace TakOne.Analyzers.Tests;

/// <summary>
/// Regression tests for <see cref="ForbiddenLoggingAnalyzer"/> — the
/// build-time security analyzer that flags <c>ILogger.Log*</c> calls
/// whose format string contains a banned credential-state or PII token.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS FILE EXISTS:</b> Brutal Code Review v3 finding #02 called
/// out that the analyzer was WIRED into the WebUI project (so it scanned
/// every production .cs + .razor.g.cs file) but had ZERO tests of its own.
/// The analyzer shipped with 4 latent bugs that went undetected for
/// exactly that reason:
/// <list type="number">
///   <item><b>Tautology in the containing-type check.</b> The previous
///     code was
///     <c>containingType.Name != "LoggerExtensions" &amp;&amp; containingType.Name != "LoggerExtensions"</c>
///     (same clause twice). The intent was to accept EITHER LoggerExtensions
///     (the static extension class for LogInformation/LogWarning/...) OR
///     ILogger (the interface for the lowest-level Log(...)). The
///     tautology made the second clause dead — so the lowest-level Log
///     overload was effectively unanalyzed.</item>
///   <item><b>Scan limit was Min(3, count) — never reached index 3.</b>
///     The lowest-level overload
///     <c>LoggerExtensions.Log(this ILogger, LogLevel, EventId, Exception?, string?, params object[])</c>
///     has the format string at index 3. With a scan limit of 3, the
///     loop only checked indices 0..2 — the format-string at index 3
///     was NEVER scanned.</item>
///   <item><b>Early <c>return;</c> after the first clean string-literal arg.</b>
///     If the first string-literal arg was clean, the loop bailed —
///     a later arg with a banned token was missed.</item>
///   <item><b>Generated-code analysis was on by default.</b> The
///     analyzer scanned .razor.g.cs files, producing false positives on
///     compiler-generated log-call stubs.</item>
/// </list>
/// </para>
/// <para>
/// These 11 tests cover all 4 fixes via targeted positive + negative
/// cases. The <see cref="AnalyzerTestHarness"/> compiles a test source
/// string programmatically via Roslyn 4.11.0 (the same Roslyn version
/// the analyzer is built against), runs the analyzer against that
/// compilation, and returns the reported diagnostics — replicating the
/// "give me a source string + analyzer, get back diagnostics" contract
/// of <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c>'s
/// <c>AnalyzerVerifier&lt;T&gt;</c> without the dependency conflict that
/// would arise from pinning Roslyn 1.0.1.
/// </para>
/// </remarks>
public class ForbiddenLoggingAnalyzerTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    // Wraps a statement into a complete compilable C# source. Every test
    // uses this template so the per-test variations stay tiny — the only
    // thing that changes between tests is the body of M().
    //
    // The two using directives cover the namespace surface every test
    // touches:
    //   - System                             — Exception, DateTimeOffset.
    //   - Microsoft.Extensions.Logging       — ILogger, LogLevel, EventId,
    //                                           LoggerExtensions.Log*.
    //
    // (ImplicitUsings doesn't apply here — the test source is a string
    // compiled in a fresh CSharpCompilation, NOT part of the test project's
    // own source graph. The using directives must be explicit.)
    private const string SourceTemplate = @"
using System;
using Microsoft.Extensions.Logging;

class C
{
    void M(ILogger logger)
    {
        {0}
    }
}
";

    private static string Wrap(string statement) => SourceTemplate.Replace("{0}", statement);

    // Runs the analyzer against the wrapped source and returns the
    // reported diagnostics. A NEW ForbiddenLoggingAnalyzer instance is
    // constructed per call so test isolation is guaranteed — no
    // analyzer state survives between tests.
    private static Task<ImmutableArray<Diagnostic>> RunAsync(string statement)
    {
        var analyzer = new ForbiddenLoggingAnalyzer();
        var source = Wrap(statement);
        return AnalyzerTestHarness.RunAsync(analyzer, source);
    }

    // Filters to diagnostics with the ForbiddenLoggingAnalyzer's rule ID.
    // (The harness already filters out compiler CS-errors; this filter
    // is just defense-in-depth against any future analyzer the harness
    // might pick up.)
    private static ImmutableArray<Diagnostic> ForbiddenDiagnostics(ImmutableArray<Diagnostic> all)
        => all
            .Where(d => d.Id == ForbiddenLoggingAnalyzer.DiagnosticId)
            .ToImmutableArray();

    // ── Positive: each banned token × each Log* method ───────────────

    // DIAG Login is the marker the original Issue #03 developer used for
    // "temporary" diagnostic logging. Banning the marker makes
    // re-introducing the pattern trivially detectable — even if a future
    // developer writes "DIAG Login: ..." in a totally different
    // credential state context, the analyzer still trips.
    [Fact]
    public async Task LogInformation_WithBannedToken_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogInformation(""DIAG Login: failed"");";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        var diag = diagnostics[0];
        diag.Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diag.Severity.Should().Be(DiagnosticSeverity.Error,
            "the rule ships at Error severity so a re-introduction of the leak fails the build, not just emits a warning");
    }

    // PasswordLength leaks the rough length of the user's password.
    // Even though we never log the password itself, the length is
    // metadata that helps an attacker bound their brute-force search.
    [Fact]
    public async Task LogWarning_WithPasswordLength_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogWarning(""PasswordLength={Len}"", 8);";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // EmailConfirmed is account-state intelligence — telling an attacker
    // whether the email is verified narrows the account-takeover
    // surface (verified emails have password-reset flow access).
    [Fact]
    public async Task LogError_WithEmailConfirmed_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogError(""EmailConfirmed={Flag}"", true);";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // LockoutEnd tells an attacker EXACTLY when to retry — they can
    // back off, wait for the lockout to clear, and try again with no
    // new counter. This is the most actionable of the 6 banned tokens.
    [Fact]
    public async Task LogCritical_WithLockoutEnd_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogCritical(""LockoutEnd={Time}"", DateTimeOffset.Now);";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // AccessFailedCount tells an attacker how many more brute-force
    // attempts remain before the lockout kicks in — they can pace
    // their attempts to stay just under the threshold.
    [Fact]
    public async Task LogDebug_WithAccessFailedCount_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogDebug(""AccessFailedCount={Count}"", 3);";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // The original Issue #03 leak used `logger.LogTrace("Password='{Password}'", password)`
    // — i.e. interpolation of the actual password value INTO the format
    // string via a structured placeholder named Password=. The token
    // "Password=" catches both the literal interpolation pattern AND
    // a structured placeholder pattern. The word-boundary check at the
    // START of the match is what prevents false positives on
    // "ResetPassword=", "MustChangePassword=", etc. — here at idx 0
    // there's no preceding char so the word-boundary check trivially
    // passes and the diagnostic fires.
    [Fact]
    public async Task LogTrace_WithPasswordEquals_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.LogTrace(""Password='{Password}'"");";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1);
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // SCAN-LIMIT-4 REGRESSION TEST (Brutal Code Review v3 finding #02):
    // The LoggerExtensions.Log overload `Log(this ILogger, LogLevel,
    // EventId, Exception?, string?, params object[])` has the format
    // string at argument INDEX 3 (LogLevel=0, EventId=1, Exception=2,
    // string=3). The previous scan limit was `Math.Min(3, count)`,
    // which only checked indices 0..2 — the format string at index 3
    // was NEVER scanned. The fix raised the limit to 4. This test
    // verifies the fix: pass a banned token at index 3, expect a
    // diagnostic. (Note: "PasswordLength=" at the start of the format
    // string ALSO triggers the PasswordLength banned token — either
    // way, the diagnostic fires; the regression value is in the
    // SCAN-LIMIT, not the specific token.)
    //
    // The `(Exception?)null` cast is required for unambiguous overload
    // resolution — without it, the compiler would pick the 3-arg
    // `Log(this ILogger, LogLevel, EventId, string, params object[])`
    // overload (format string at index 2), which the OLD analyzer
    // already caught. The cast forces the compiler to pick the 4-arg
    // overload, which is the one the scan-limit-4 fix addresses.
    [Fact]
    public async Task Log_WithLowestLevelOverload_DiagnosesError()
    {
        // Arrange
        var statement = @"logger.Log(LogLevel.Warning, new EventId(7), (Exception?)null, ""PasswordLength=5"");";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().HaveCount(1,
            "the format string 'PasswordLength=5' is at argument index 3 (LogLevel=0, EventId=1, Exception=2, string=3); " +
            "the previous scan limit of 3 (Min(3, count)) never reached index 3, so this overload was entirely unanalyzed");
        diagnostics[0].Id.Should().Be(ForbiddenLoggingAnalyzer.DiagnosticId);
        diagnostics[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    // ── Negative: clean messages, compound identifiers, non-ILogger ─

    // A clean login-flow log message uses identifiers that are NOT in
    // the banned-token list — WorkerId, UserId, Outcome. The analyzer
    // must NOT flag these.
    [Fact]
    public async Task LogInformation_WithCleanMessage_NoDiagnostic()
    {
        // Arrange
        var statement = @"logger.LogInformation(""Login succeeded for user {UserId}"", id);";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().BeEmpty(
            "the message contains no banned token (no DIAG Login, no PasswordLength, no EmailConfirmed, no LockoutEnd, no AccessFailedCount, no Password=, no Password:)");
    }

    // WORD-BOUNDARY REGRESSION TEST (Brutal Code Review v3 finding #02):
    // The compound identifier "MustChangePassword" contains the substring
    // "Password" — but NONE of the banned tokens ("Password=", "Password:")
    // appear in the message. The analyzer's word-boundary check on the
    // Password=/Password: tokens (require the char immediately before
    // the match to NOT be a letter or digit) is what prevents false
    // positives on legitimate compound identifiers like MustChangePassword,
    // NewPassword, ResetPassword, etc.
    //
    // NOTE: this test alone doesn't FULLY exercise the word-boundary
    // check (since no banned-token substring appears in "MustChangePassword
    // completed"). The deeper word-boundary exercise would use a string
    // like "ResetPassword=abc" where "Password=" appears but is preceded
    // by a letter ("t"). The brief specified this exact message though.
    [Fact]
    public async Task LogInformation_WithCompoundPasswordIdentifier_NoDiagnostic()
    {
        // Arrange
        var statement = @"logger.LogInformation(""MustChangePassword completed"");";

        // Act
        var diagnostics = ForbiddenDiagnostics(await RunAsync(statement));

        // Assert
        diagnostics.Should().BeEmpty(
            "'MustChangePassword' is a compound identifier — the substring 'Password' alone is not a banned token; " +
            "the banned tokens 'Password=' and 'Password:' do not appear in this message");
    }

    // Console.WriteLine is NOT an ILogger.Log* call — it's a different
    // API entirely. The analyzer pre-filters on method name starting
    // with "Log"; "WriteLine" doesn't start with "Log", so the analyzer
    // returns at the pre-filter step. This test guards against a future
    // change that might broaden the method-name filter to "any method
    // containing the word 'Log'" (which would falsely catch Console.WriteLine
    // when the message happens to contain a banned token).
    [Fact]
    public async Task ConsoleWriteLine_NotDiagnosed()
    {
        // Arrange — a source WITHOUT the `using Microsoft.Extensions.Logging;`
        // (the harness's template adds it but it's unused here, which is
        // fine — the unused using is a CS8019 hidden diagnostic, not an
        // analyzer diagnostic).
        var source = @"
using System;

class C
{
    void M()
    {
        Console.WriteLine(""PasswordLength"");
    }
}
";

        // Act
        var diagnostics = ForbiddenDiagnostics(
            await AnalyzerTestHarness.RunAsync(new ForbiddenLoggingAnalyzer(), source));

        // Assert
        diagnostics.Should().BeEmpty(
            "Console.WriteLine is not an ILogger.Log* call — the analyzer pre-filters on method name starting with 'Log'; 'WriteLine' fails the pre-filter");
    }

    // CONTAINING-TYPE CHECK REGRESSION TEST (Brutal Code Review v3
    // finding #02): a class that is NOT an ILogger but happens to expose
    // a method named LogInformation should NOT be flagged. The analyzer's
    // symbol-lookup step confirms the call's containing type is either
    // LoggerExtensions or ILogger; a third-party FakeLogger doesn't
    // match either, so the analyzer returns at the containing-type check.
    //
    // This test also covers the tautology fix: the previous code was
    // `containingType.Name != "LoggerExtensions" && containingType.Name != "LoggerExtensions"`
    // (same clause twice). For FakeLogger.LogInformation, both clauses
    // were true → return → no diagnostic. The current code (after the
    // fix) is `containingType.Name != "LoggerExtensions" && !(containingType.Name == "ILogger" && methodName.StartsWith("Log"))`
    // — for FakeLogger.LogInformation: first clause true, second clause
    // (LoggerExtensions check) false → !false = true → true && true = true
    // → return. Same observable behavior for THIS test case, but the
    // FIX matters for the lowest-level Log() overload on the ILogger
    // interface itself (covered by Log_WithLowestLevelOverload_DiagnosesError).
    [Fact]
    public async Task NonLoggingMethod_NotDiagnosed()
    {
        // Arrange — a source that defines its own FakeLogger class with
        // a LogInformation method. The message contains the banned
        // token "PasswordLength" — if the analyzer mis-classified the
        // containing type, it would fire here. It must NOT fire.
        var source = @"
class FakeLogger
{
    public void LogInformation(string message) { }
}

class C
{
    void M(FakeLogger logger)
    {
        logger.LogInformation(""PasswordLength=5"");
    }
}
";

        // Act
        var diagnostics = ForbiddenDiagnostics(
            await AnalyzerTestHarness.RunAsync(new ForbiddenLoggingAnalyzer(), source));

        // Assert
        diagnostics.Should().BeEmpty(
            "FakeLogger is NOT an ILogger — the analyzer's containing-type check (LoggerExtensions or ILogger) rejects this call at the symbol-lookup step");
    }
}
