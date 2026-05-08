using Microsoft.AspNetCore.Builder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options => options.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "3.1" }));

// ─── /api/analyze ─────────────────────────────────────────────────────────────
app.MapPost("/api/analyze",
    async (AnalyzeRequest req) =>
{
    return await AnalyzeProject(req);
});

    app.MapPost("/api/analyze-upload", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();

    var zipFile = form.Files["projectZip"];
    var coverageFile = form.Files["coverageXml"];

    if (zipFile == null)
    {
        return Results.BadRequest(new
        {
            error = "Project ZIP file is required."
        });
    }

    // ─────────────────────────────────────────────
    // TEMP DIRECTORY
    // ─────────────────────────────────────────────

    string tempPath =
        Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString());

    Directory.CreateDirectory(tempPath);

    // ─────────────────────────────────────────────
    // SAVE ZIP
    // ─────────────────────────────────────────────

    string zipPath =
        Path.Combine(tempPath, "project.zip");

    using (var stream = File.Create(zipPath))
    {
        await zipFile.CopyToAsync(stream);
    }

    // ─────────────────────────────────────────────
    // EXTRACT ZIP
    // ─────────────────────────────────────────────

    string extractPath =
        Path.Combine(tempPath, "source");

    Directory.CreateDirectory(extractPath);

    ZipFile.ExtractToDirectory(
        zipPath,
        extractPath);

    // ─────────────────────────────────────────────
    // FIND REAL PROJECT ROOT
    // ─────────────────────────────────────────────

    string projectRoot =
        Directory.GetDirectories(extractPath)
            .FirstOrDefault()
        ?? extractPath;

    // ─────────────────────────────────────────────
    // COVERAGE XML
    // ─────────────────────────────────────────────

    string? coveragePath = null;

    if (coverageFile != null)
    {
        coveragePath =
            Path.Combine(
                tempPath,
                "coverage.cobertura.xml");

        using var coverageStream =
            File.Create(coveragePath);

        await coverageFile.CopyToAsync(
            coverageStream);
    }

    // ─────────────────────────────────────────────
    // CALL EXISTING ANALYZER
    // ─────────────────────────────────────────────

    var fakeRequest =
        new AnalyzeRequest(
            projectRoot,
            null);

    // REUSE EXISTING ENDPOINT LOGIC
    return await AnalyzeProject(
        fakeRequest,
        coveragePath);
});

// ─── /api/feedback ────────────────────────────────────────────────────────────
app.MapPost("/api/feedback", (FeedbackRequest req) =>
{
    var metric = new MethodMetric
    {
        FullName = req.FullName,
        MethodName = req.MethodName,
        Complexity = req.Complexity,
        UsageCount = req.UsageCount,
        CoverageRate = req.CoverageRate,
        RiskScore = req.RiskScore,
        Status = req.Status,
        IsEstimatedRisk = req.CoverageRate <= 0,
        Severity = RiskCalculator.ResolveSeverity(req.RiskScore)
    };
    return Results.Ok(new { feedback = FeedbackEngine.Generate(metric) });
});

    static async Task<IResult> AnalyzeProject(
    AnalyzeRequest req,
    string? externalCoveragePath = null)
{
    if (string.IsNullOrWhiteSpace(req.ProjectPath)
        || !Directory.Exists(req.ProjectPath))
    {
        return Results.BadRequest(new
        {
            error = "A valid project path must be provided."
        });
    }

    // STEP 1: Collect .cs files
    var csFiles = Directory.GetFiles(
        req.ProjectPath,
        "*.cs",
        SearchOption.AllDirectories)
        .Where(f =>
            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            &&
            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToList();

    if (csFiles.Count == 0)
    {
        return Results.Ok(
            new AnalyzeResponse(
                new List<MethodMetric>(),
                SummaryBuilder.Build(
                    new List<MethodMetric>(),
                    false,
                    false)));
    }

    // STEP 2: Build Roslyn Compilation
    var syntaxTrees = csFiles
        .Select(f => CSharpSyntaxTree.ParseText(
            File.ReadAllText(f),
            path: f,
            options: CSharpParseOptions.Default))
        .ToList();

    var references = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a =>
            !a.IsDynamic &&
            !string.IsNullOrWhiteSpace(a.Location))
        .Select(a =>
            MetadataReference.CreateFromFile(a.Location))
        .Cast<MetadataReference>()
        .ToList();

    var compilation = CSharpCompilation.Create(
        assemblyName: "RiskAnalysis",
        syntaxTrees: syntaxTrees,
        references: references,
        options: new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary));

    // STEP 3: Analyze
    var allMetrics = new List<MethodMetric>();

    foreach (var tree in syntaxTrees)
    {
        var semanticModel =
            compilation.GetSemanticModel(tree);

        var analyzer =
            new MethodAnalyzer(
                compilation,
                semanticModel,
                tree,
                req.ProjectPath);

        analyzer.Visit(tree.GetRoot());

        allMetrics.AddRange(analyzer.Results);
    }

    // STEP 4: COVERAGE PATH
    string? coverageFile = externalCoveragePath;
    if (coverageFile != null &&
    File.Exists(coverageFile))
{
    using var fs = new FileStream(
        coverageFile,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite);

    var doc = XDocument.Load(fs);

    var classRates =
        new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);

    foreach (var cls in doc.Descendants("class"))
    {
        string clsName =
            cls.Attribute("name")?.Value ?? "";

        string rateStr =
            cls.Attribute("line-rate")?.Value ?? "0";

        if (!double.TryParse(
                rateStr,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double rate))
            continue;

        string baseName =
            clsName.Contains('/')
                ? clsName.Split('/')[0]
                : clsName;

        baseName = baseName.ToLower();

        if (!classRates.ContainsKey(baseName))
            classRates[baseName] = rate;
        else
            classRates[baseName] =
                Math.Max(
                    classRates[baseName],
                    rate);
    }

    var methodRates =
        doc.Descendants("method")
            .Select(m => new
            {
                ClassName =
                    (m.Parent?.Parent
                        ?.Attribute("name")
                        ?.Value ?? "")
                    .ToLower(),

                MethodName =
                    (m.Attribute("name")
                        ?.Value ?? "")
                    .ToLower(),

                Rate =
                    m.Attribute("line-rate")
                        ?.Value ?? "0"
            })
            .ToList();

    foreach (var m in allMetrics)
    {
        string lowerMethod =
            m.MethodName.ToLower();

        string lowerClass =
            m.FullName
                .Split('.')
                .Reverse()
                .Skip(1)
                .FirstOrDefault()
                ?.ToLower() ?? "";

        string lowerFull =
            m.FullName.ToLower();

        var methodMatch =
            methodRates.FirstOrDefault(x =>
                x.ClassName.Contains(lowerClass)
                &&
                (
                    x.MethodName == lowerMethod
                    ||
                    x.ClassName.Contains(
                        "<" + lowerMethod + ">")
                ));

        if (methodMatch != null &&
            double.TryParse(
                methodMatch.Rate,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double mRate))
        {
            m.CoverageRate =
                Math.Round(mRate * 100, 2);

            continue;
        }

        var classKey =
            classRates.Keys.FirstOrDefault(k =>
                k.Contains(lowerClass)
                ||
                lowerFull.Contains(k));

        if (classKey != null)
        {
            m.CoverageRate =
                Math.Round(
                    classRates[classKey] * 100,
                    2);
        }
    }
}

    // Existing risk/status logic
    foreach (var m in allMetrics)
    {
        m.IsEstimatedRisk =
            coverageFile == null;

        m.RiskScore =
            RiskCalculator.ComputeRiskScore(
                m.Complexity,
                m.UsageCount,
                m.CoverageRate);

        m.Severity =
            RiskCalculator.ResolveSeverity(
                m.RiskScore);

        m.Status =
            StatusResolver.Resolve(m);

        m.Feedback =
            FeedbackEngine.Generate(m);
    }

    var finalMethods = allMetrics
        .GroupBy(x => x.FullName)
        .Select(g => g.First())
        .OrderByDescending(x => x.RiskScore)
        .ToList();

    var summary =
        SummaryBuilder.Build(
            finalMethods,
            true,
            coverageFile != null);

    return Results.Ok(
        new AnalyzeResponse(
            finalMethods,
            summary));
}
    

app.Run("http://localhost:5003");

// ─────────────────────────────────────────────────────────────────────────────
// MODELS
// ─────────────────────────────────────────────────────────────────────────────

public record AnalyzeRequest(string ProjectPath, string? TestProjectPath);

public record FeedbackRequest(
    string FullName,
    string MethodName,
    int Complexity,
    int UsageCount,
    double CoverageRate,
    double RiskScore,
    string Status);

public record AnalyzeResponse(List<MethodMetric> Methods, AnalyzeSummary Summary);

public class AnalyzeSummary
{
    public int TotalMethods { get; set; }
    public int TestedMethods { get; set; }
    public int UntestedMethods { get; set; }
    public int UnusedMethods { get; set; }
    public int HighRiskCount { get; set; }
    public int MediumRiskCount { get; set; }
    public int LowRiskCount { get; set; }
    public double AverageRiskScore { get; set; }
    public double AverageCoverage { get; set; }
    public bool TestsRan { get; set; }
    public bool CoverageAvailable { get; set; }
}

public class MethodMetric
{
    public string FullName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Complexity { get; set; }
    public int UsageCount { get; set; }
    public double CoverageRate { get; set; }
    public string Status { get; set; } = "";
    public double RiskScore { get; set; }
    public string Feedback { get; set; } = "";

    // Semantic — set by MethodAnalyzer
    public bool IsInterfaceImplementation { get; set; }
    public bool IsOverride { get; set; }
    public string InterfaceName { get; set; } = "";
    public bool IsEstimatedRisk { get; set; }
    public string Severity { get; set; } = "";
}

// ─────────────────────────────────────────────────────────────────────────────
// SEMANTIC USAGE COUNTER
// ─────────────────────────────────────────────────────────────────────────────

public static class SemanticUsageCounter
{
    /// <summary>
    /// Counts how many times <paramref name="targetSymbol"/> is called across
    /// the entire compilation, following interface implementations and overrides.
    /// </summary>
    public static int Count(IMethodSymbol targetSymbol, CSharpCompilation compilation)
    {
        // Resolve to the root symbol so interface dispatch and overrides are caught.
        // e.g. if targetSymbol overrides a base method, we also count calls to the base.
        var canonicalSymbols = ResolveSymbolFamily(targetSymbol);

        int count = 0;

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            // ── Regular invocations: obj.Method(...)  ──────────────────────
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(invocation);
                var symbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                if (symbol == null) continue;

                if (MatchesFamily(symbol, canonicalSymbols))
                    count++;
            }

            // ── Method group conversions: Action a = obj.Method  ───────────
            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(memberAccess);
                var symbol = info.Symbol as IMethodSymbol;
                if (symbol == null) continue;

                // Only count if parent is NOT an invocation (would double-count)
                if (memberAccess.Parent is InvocationExpressionSyntax) continue;

                if (MatchesFamily(symbol, canonicalSymbols))
                    count++;
            }
        }

        // Subtract 1 for the definition site itself (declaration is not a call)
        return Math.Max(0, count - 1);
    }

    // Builds the full symbol family: the method itself + any interface members it implements
    private static HashSet<IMethodSymbol> ResolveSymbolFamily(IMethodSymbol method)
    {
        var family = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default)
        {
            method.OriginalDefinition
        };

        // Walk the override chain upward
        var current = method;
        while (current.OverriddenMethod != null)
        {
            family.Add(current.OverriddenMethod.OriginalDefinition);
            current = current.OverriddenMethod;
        }

        // Add explicit interface implementations
        foreach (var iface in method.ExplicitInterfaceImplementations)
            family.Add(iface.OriginalDefinition);

        // Add implicit interface implementations
        // (e.g. class implements IService.Process via a public Process() method)
        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = method.ContainingType.FindImplementationForInterfaceMember(ifaceMember) as IMethodSymbol;
                if (impl != null &&
                    SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, method.OriginalDefinition))
                {
                    family.Add(ifaceMember.OriginalDefinition);
                }
            }
        }

        return family;
    }

    private static bool MatchesFamily(IMethodSymbol candidate, HashSet<IMethodSymbol> family)
    {
        var orig = candidate.OriginalDefinition;

        if (family.Contains(orig)) return true;

        // Also check if candidate is an override of something in the family
        var cur = candidate;
        while (cur.OverriddenMethod != null)
        {
            if (family.Contains(cur.OverriddenMethod.OriginalDefinition)) return true;
            cur = cur.OverriddenMethod;
        }

        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// METHOD ANALYZER  (Roslyn SyntaxWalker — semantic-aware)
// ─────────────────────────────────────────────────────────────────────────────

public class MethodAnalyzer : CSharpSyntaxWalker
{
    private readonly CSharpCompilation _compilation;
    private readonly SemanticModel _semanticModel;
    private readonly SyntaxTree _tree;
    private readonly string _projectRoot;

    public List<MethodMetric> Results { get; } = new();

    public MethodAnalyzer(
        CSharpCompilation compilation,
        SemanticModel semanticModel,
        SyntaxTree tree,
        string projectRoot)
    {
        _compilation = compilation;
        _semanticModel = semanticModel;
        _tree = tree;
        _projectRoot = projectRoot;
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var nsNode = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        string methodName = node.Identifier.Text;
        string className = classNode?.Identifier.Text ?? "UnknownClass";
        string namespaceName = nsNode?.Name.ToString() ?? "Global";
        string fullName = $"{namespaceName}.{className}.{methodName}";

        // ── Cyclomatic Complexity (syntactic — fast) ──────────────────────
        int complexity = 1 + node.DescendantNodes().Count(n =>
            n is IfStatementSyntax ||
            n is ForStatementSyntax ||
            n is ForEachStatementSyntax ||
            n is SwitchSectionSyntax ||
            n is CatchClauseSyntax ||
            n is ConditionalExpressionSyntax ||
            n is WhileStatementSyntax ||
            n is DoStatementSyntax);

        // ── Semantic: get IMethodSymbol ───────────────────────────────────
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;

        // ── Usage Count (semantic — accurate) ────────────────────────────
        int usageCount = 0;
        bool isInterfaceImpl = false;
        bool isOverride = false;
        string interfaceName = "";

        if (methodSymbol != null)
        {
            usageCount = SemanticUsageCounter.Count(methodSymbol, _compilation);

            isOverride = methodSymbol.IsOverride;

            // Check interface implementation
            foreach (var iface in methodSymbol.ContainingType.AllInterfaces)
            {
                foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var impl = methodSymbol.ContainingType
                        .FindImplementationForInterfaceMember(ifaceMember) as IMethodSymbol;
                    if (impl != null &&
                        SymbolEqualityComparer.Default.Equals(
                            impl.OriginalDefinition,
                            methodSymbol.OriginalDefinition))
                    {
                        isInterfaceImpl = true;
                        interfaceName = iface.Name;
                        break;
                    }
                }
                if (isInterfaceImpl) break;
            }
        }
        else
        {
            // Fallback to string matching if semantic model fails
            // (e.g. unresolved references, partial compilation)
            string pattern = methodName + "(";
            foreach (var tree in _compilation.SyntaxTrees)
            {
                var text = tree.ToString();
                int idx = 0;
                while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
                { usageCount++; idx += pattern.Length; }
            }
            usageCount = Math.Max(0, usageCount - 1);
        }

        string filePath = _tree.FilePath;

        Results.Add(new MethodMetric
        {
            FullName = fullName,
            MethodName = methodName,
            FilePath = Path.GetRelativePath(_projectRoot, filePath),
            Complexity = complexity,
            UsageCount = usageCount,
            CoverageRate = 0,
            Status = "",
            RiskScore = 0,
            IsInterfaceImplementation = isInterfaceImpl,
            IsOverride = isOverride,
            InterfaceName = interfaceName
        });

        base.VisitMethodDeclaration(node);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RISK CALCULATOR
// ─────────────────────────────────────────────────────────────────────────────

public static class RiskCalculator
{
    public static double ComputeRiskScore(
        int complexity,
        int usageCount,
        double coverageRate)
    {
        // ─────────────────────────────────────────────
        // 1. Normalize complexity
        // ─────────────────────────────────────────────
        double complexityScore =
            Math.Log2(complexity + 1);

        // ─────────────────────────────────────────────
        // 2. Normalize usage count
        // ─────────────────────────────────────────────
        double usageScore =
            Math.Log2(usageCount + 1);

        // ─────────────────────────────────────────────
        // 3. Weighted danger score
        // Complexity is more important
        // ─────────────────────────────────────────────
        double dangerScore =
            (complexityScore * 0.65)
            +
            (usageScore * 0.35);

        bool hasCoverage =
            coverageRate > 0;

        double finalRisk;

        if (hasCoverage)
        {
            // Coverage exists
            // Higher coverage lowers risk

            double coverageMultiplier =
                1 - (coverageRate / 100.0);

            finalRisk =
                dangerScore *
                coverageMultiplier *
                25;
        }
        else
        {
            // No coverage
            // Estimated risk from static analysis only

            finalRisk =
                dangerScore *
                18;
        }

        return Math.Round(
            Math.Clamp(finalRisk, 0, 100),
            2);
    }

    public static string ResolveSeverity(double riskScore)
    {
        if (riskScore >= 80)
            return "Extreme";

        if (riskScore >= 60)
            return "Critical";

        if (riskScore >= 40)
            return "High";

        if (riskScore >= 20)
            return "Medium";

        return "Low";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// STATUS RESOLVER
// ─────────────────────────────────────────────────────────────────────────────

public static class StatusResolver
{
    public static string Resolve(MethodMetric m)
    {
        if (m.FullName.Contains(".Dispose")) return "Resource_Clean_Tested";
        if (m.MethodName == "Main") return "System_Entry";
        if (m.MethodName.StartsWith("On") || m.MethodName.EndsWith("Click")
         || m.MethodName.EndsWith("Load") || m.MethodName.EndsWith("Formatting")
         || m.MethodName.EndsWith("Changed") || m.MethodName.EndsWith("KeyPress"))
            return "UI_Event";
        if (m.CoverageRate >= 100) return "Fully_Verified";
        if (m.CoverageRate >= 80) return "Safe_Verified";
        if (m.CoverageRate >= 30) return "Partial_Logic_Verified";
        if (m.CoverageRate > 0) return "Logic_Tested";
        if (m.UsageCount == 0) return "Unused";
        return "Untested";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FEEDBACK ENGINE
// ─────────────────────────────────────────────────────────────────────────────

public static class FeedbackEngine
{
    public static string Generate(MethodMetric m)
    {
        if (m.Status == "Resource_Clean_Tested")
            return "Dispose method detected. Ensure unmanaged resources are always released in finally blocks or using statements.";

        if (m.Status == "System_Entry")
            return "Entry point method. Validate all startup arguments and configuration before delegating to application logic.";

        if (m.Status == "UI_Event")
            return "UI event handler detected. Keep business logic out of event handlers; delegate to a testable service layer.";

        if (m.Status == "Unused")
            return "This method has no detected callers. Consider removing it to reduce dead code, or verify it is called via reflection or external assemblies.";

        // Interface / override context enriches feedback
        string context = "";
        if (m.IsInterfaceImplementation)
            context = $" This method implements {m.InterfaceName} — callers may be dispatched via the interface, so actual usage could be higher than counted.";
        else if (m.IsOverride)
            context = " This method is an override — callers invoking the base type also reach this implementation.";

        if (m.IsEstimatedRisk)
        {
            if (m.Complexity >= 8)
                return $"CRITICAL — No test coverage on a highly complex method (complexity: {m.Complexity}).{context} Write unit tests covering each branch immediately.";
            if (m.Complexity >= 4)
                return $"HIGH RISK — No coverage and moderate complexity (complexity: {m.Complexity}).{context} Add unit tests for the main execution paths and edge cases.";
            return $"No test coverage detected.{context} Add at least one unit test to verify the basic behavior of this method.";
        }

        if (m.RiskScore >= 60)
            return $"High risk score ({m.RiskScore}). Coverage is only {m.CoverageRate}% on a method with complexity {m.Complexity} called {m.UsageCount} time(s).{context} Increase test coverage to reduce exposure.";

        if (m.RiskScore >= 30)
            return $"Moderate risk ({m.RiskScore}). Consider adding tests for uncovered branches. Complexity: {m.Complexity}, Coverage: {m.CoverageRate}%.{context}";

        if (m.CoverageRate >= 100)
            return $"Fully covered. Maintain existing tests and extend them if new branches are added.{context}";

        return $"Low risk ({m.RiskScore}). Coverage is {m.CoverageRate}%. Monitor if complexity grows.{context}";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SUMMARY BUILDER
// ─────────────────────────────────────────────────────────────────────────────

public static class SummaryBuilder
{
    public static AnalyzeSummary Build(List<MethodMetric> methods, bool testsRan, bool coverageAvailable)
    {
        if (methods.Count == 0)
            return new AnalyzeSummary { TestsRan = testsRan, CoverageAvailable = coverageAvailable };

        return new AnalyzeSummary
        {
            TotalMethods = methods.Count,
            TestedMethods = methods.Count(m => m.CoverageRate > 0),
            UntestedMethods = methods.Count(m => m.IsEstimatedRisk && m.UsageCount > 0),
            UnusedMethods = methods.Count(m =>
                m.UsageCount == 0 &&
                m.IsEstimatedRisk),
            HighRiskCount = methods.Count(m => m.RiskScore >= 60),
            MediumRiskCount = methods.Count(m =>
                m.RiskScore >= 30 &&
                m.RiskScore < 60),
            LowRiskCount = methods.Count(m =>
                m.RiskScore < 30),
            AverageRiskScore = Math.Round(methods.Average(m => m.RiskScore), 2),
            AverageCoverage = Math.Round(methods.Average(m => m.CoverageRate), 2),
            TestsRan = testsRan,
            CoverageAvailable = coverageAvailable
        };
    }
}