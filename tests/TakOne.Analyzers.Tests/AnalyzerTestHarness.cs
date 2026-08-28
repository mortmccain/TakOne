using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TakOne.Analyzers.Tests;

/// <summary>
/// Minimal in-process Roslyn analyzer test harness — replicates the
/// "give me a source string + an analyzer, I'll return the diagnostics"
/// contract of <c>Microsoft.CodeAnalysis.CSharp.Analyzer.Testing</c>'s
/// <c>AnalyzerVerifier&lt;T&gt;</c> without that package's hard dependency
/// on Roslyn 1.0.1 (which conflicts with this solution's Roslyn 4.11.0).
/// </summary>
/// <remarks>
/// <para>
/// <b>HOW IT WORKS:</b>
/// <list type="number">
///   <item>Parse the supplied C# source string into a syntax tree.</item>
///   <item>Build a <see cref="CSharpCompilation"/> with that tree + a
///     minimal set of <see cref="MetadataReference"/> objects (the BCL
///     + <c>Microsoft.Extensions.Logging.Abstractions</c>) so the test
///     source can <c>using Microsoft.Extensions.Logging;</c> and call
///     <c>logger.LogInformation(...)</c> etc. — i.e. resolve to the
///     <c>LoggerExtensions.Log*</c> methods the analyzer scans for.</item>
///   <item>Wrap the compilation with the analyzer via
///     <see cref="Compilation.WithAnalyzers(ImmutableArray{DiagnosticAnalyzer})"/>.</item>
///   <item>Run the analyzer and return its diagnostics.</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY NOT INCLUDE A REFERENCE TO TYPED LOGGEREXTENSIONS IN THE
/// HARNESS CLASS:</b> the harness references
/// <see cref="ILogger"/> in the static ctor purely to obtain the
/// <c>Microsoft.Extensions.Logging.Abstractions</c> assembly path via
/// reflection. The actual test source string is responsible for the
/// <c>using Microsoft.Extensions.Logging;</c> directive — the harness
/// only makes the assembly available as a metadata reference to the
/// compiler; it doesn't itself touch the extension methods.
/// </para>
/// </remarks>
internal static class AnalyzerTestHarness
{
    // Resolved once at type-init time. Cached because every test will
    // need the same set of references, and MetadataReference creation
    // does file-stat() on every call.
    private static readonly ImmutableArray<MetadataReference> DefaultReferences = BuildDefaultReferences();

    /// <summary>
    /// Compiles <paramref name="source"/> as the single C# file in a
    /// fresh in-memory assembly, runs <paramref name="analyzer"/> against
    /// that compilation, and returns the diagnostics the analyzer reported.
    /// </summary>
    /// <param name="analyzer">The analyzer instance to run. The harness
    /// constructs a NEW instance per call so test isolation is guaranteed —
    /// no analyzer state survives between tests.</param>
    /// <param name="source">The C# source code to analyze. Must be a
    /// self-contained file (no #r directives, no #load). The harness's
    /// default metadata references cover the BCL +
    /// <c>Microsoft.Extensions.Logging.Abstractions</c>; callers may
    /// pass extra references for tests that need more.</param>
    /// <param name="additionalReferences">Optional extra
    /// <see cref="MetadataReference"/> objects (e.g. for a domain
    /// assembly the test source references). Default is empty — the
    /// 11 analyzer tests don't need any extras because the test source
    /// only uses <c>Microsoft.Extensions.Logging</c> APIs.</param>
    /// <returns>The array of <see cref="Diagnostic"/> objects the analyzer
    /// reported. Compilation diagnostics (CS-errors) are filtered out —
    /// the harness only returns analyzer-reported diagnostics so a
    /// test that asserts "no diagnostic" doesn't trip on a benign
    /// CS8019 (unused using) warning.</returns>
    public static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string source,
        params MetadataReference[] additionalReferences)
    {
        // Parse the source. SourceCodeKind.Regular = treat as a regular
        // .cs file (not a script). The preprocessorSymbols arg is null
        // — none of the tests use #if.
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            options: new CSharpParseOptions(LanguageVersion.CSharp12),
            path: "TestSource.cs");

        var references = additionalReferences.Length == 0
            ? DefaultReferences
            : DefaultReferences.AddRange(additionalReferences.ToImmutableArray());

        var compilation = CSharpCompilation.Create(
            assemblyName: "TakOne.Analyzers.Tests.TestAssembly",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                // Allow unsafe — not needed, but harmless and matches
                // the defaults the analyzer sees in production code.
                allowUnsafe: true,
                // SuppressWarnings: CS1591 (missing XML doc comments)
                // is benign for a test compilation.
                generalDiagnosticOption: ReportDiagnostic.Default));

        var compilationWithAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer));

        // GetAnalyzerDiagnosticsAsync runs the analyzer's Initialize() +
        // the registered syntax-node action against every syntax node
        // in the compilation, returning ONLY analyzer-reported
        // diagnostics (compiler CS-errors are not included).
        var analyzerDiagnostics = await compilationWithAnalyzers
            .GetAnalyzerDiagnosticsAsync();

        return analyzerDiagnostics;
    }

    // ── Reference bootstrap ─────────────────────────────────────────

    // Build the default MetadataReference set for the test compilation
    // by enumerating the TPA list (the runtime's "TRUSTED_PLATFORM_ASSEMBLIES"
    // property — a colon- or semicolon-separated list of framework DLL
    // paths). This is the same list the .NET host uses to bootstrap
    // the runtime; adding each as a MetadataReference gives the test
    // compilation a complete view of the BCL — so the test source string
    // can `using Microsoft.Extensions.Logging;` and call
    // `logger.LogInformation("...")` (which resolves to LoggerExtensions.LogInformation,
    // defined in the Microsoft.Extensions.Logging.Abstractions assembly)
    // plus use Console.WriteLine, DateTimeOffset.Now, Exception, etc.
    //
    // ADDING ALL TPA DLLs is intentionally over-broad — we don't care
    // about minimal-trim builds here, we care about correctness. The
    // analyzer scans the C# source the test passes in; the metadata
    // references just need to let that source compile.
    //
    // On top of TPA we explicitly add `Microsoft.Extensions.Logging.Abstractions`
    // (via typeof(ILogger).Assembly.Location) — under .NET 10, that's
    // already in TPA, but adding it explicitly is belt-and-suspenders
    // for environments where the TPA doesn't include it (e.g. self-contained
    // publishes that didn't include the abstractions assembly).
    private static ImmutableArray<MetadataReference> BuildDefaultReferences()
    {
        // Collect paths first, deduplicate by PATH (not by MetadataReference
        // identity — MetadataReference.CreateFromFile returns a new
        // instance per call so reference-equality dedup never matches).
        var paths = new HashSet<string>(StringComparer.Ordinal);

        // 1) TPA framework assemblies.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            // Path.PathSeparator is ':' on Unix, ';' on Windows.
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    paths.Add(path);
                }
            }
        }

        // 2) Explicit additions for belt-and-suspenders — even if the
        // TPA list didn't include these (it always does in our setup),
        // add them via typeof() so we KNOW they're available to the test
        // compilation.
        paths.Add(typeof(object).Assembly.Location);
        paths.Add(typeof(Console).Assembly.Location);
        paths.Add(typeof(ILogger).Assembly.Location);

        return paths
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }
}
