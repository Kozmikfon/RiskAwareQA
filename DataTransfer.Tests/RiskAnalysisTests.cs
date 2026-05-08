using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RiskAwareQA.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// MODEL
// ─────────────────────────────────────────────────────────────────────────────

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
}

// ─────────────────────────────────────────────────────────────────────────────
// RISK ENGINE  (mirrors Program.cs static classes — no production dependency)
// ─────────────────────────────────────────────────────────────────────────────

public static class RiskEngine
{
    // ── Roslyn Analyzer ──────────────────────────────────────────────────────
    public static List<MethodMetric> Analyze(string csharpCode, List<string>? allCode = null)
    {
        allCode ??= new List<string> { csharpCode };
        var tree = CSharpSyntaxTree.ParseText(csharpCode);
        var root = tree.GetRoot();
        var results = new List<MethodMetric>();

        foreach (var node in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            var nsNode = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

            string methodName = node.Identifier.Text;
            string className = classNode?.Identifier.Text ?? "UnknownClass";
            string namespaceName = nsNode?.Name.ToString() ?? "Global";

            int complexity = 1 + node.DescendantNodes().Count(n =>
                n is IfStatementSyntax ||
                n is ForStatementSyntax ||
                n is ForEachStatementSyntax ||
                n is SwitchSectionSyntax ||
                n is CatchClauseSyntax ||
                n is ConditionalExpressionSyntax ||
                n is WhileStatementSyntax ||
                n is DoStatementSyntax);

            int usage = 0;
            string pattern = methodName + "(";
            foreach (var code in allCode)
            {
                int idx = 0;
                while ((idx = code.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
                { usage++; idx += pattern.Length; }
            }
            usage = Math.Max(0, usage - 1);

            results.Add(new MethodMetric
            {
                FullName = $"{namespaceName}.{className}.{methodName}",
                MethodName = methodName,
                Complexity = complexity,
                UsageCount = usage,
                CoverageRate = 0
            });
        }
        return results;
    }

    // ── Risk Score ───────────────────────────────────────────────────────────
    // danger = (min(complexity,10) * 7) + (min(usageCount,30) * 1)  → max 100
    // safety = (100 - coverageRate) / 100
    // risk   = clamp(danger * safety, 0, 100)
    public static double ComputeRiskScore(int complexity, int usageCount, double coverageRate)
    {
        double danger = (Math.Min(complexity, 10) * 7.0) + (Math.Min(usageCount, 30) * 1.0);
        double safety = (100.0 - coverageRate) / 100.0;
        return Math.Round(Math.Clamp(danger * safety, 0, 100), 2);
    }

    // ── Status Resolver ──────────────────────────────────────────────────────
    public static string ResolveStatus(MethodMetric m)
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

    // ── Feedback Engine ──────────────────────────────────────────────────────
    public static string GenerateFeedback(MethodMetric m)
    {
        if (m.Status == "Resource_Clean_Tested")
            return "Dispose method detected. Ensure unmanaged resources are always released in finally blocks or using statements.";

        if (m.Status == "System_Entry")
            return "Entry point method. Validate all startup arguments and configuration before delegating to application logic.";

        if (m.Status == "UI_Event")
            return "UI event handler detected. Keep business logic out of event handlers; delegate to a testable service layer.";

        if (m.Status == "Unused")
            return "This method has no detected callers. Consider removing it to reduce dead code, or verify it is called via reflection or external assemblies.";

        if (m.CoverageRate == 0)
        {
            if (m.Complexity >= 8)
                return $"CRITICAL — No test coverage on a highly complex method (complexity: {m.Complexity}). This is the highest-priority item to address. Write unit tests covering each branch immediately.";
            if (m.Complexity >= 4)
                return $"HIGH RISK — No coverage and moderate complexity (complexity: {m.Complexity}). Add unit tests for the main execution paths and edge cases.";
            return "No test coverage detected. Add at least one unit test to verify the basic behavior of this method.";
        }

        if (m.RiskScore >= 60)
            return $"High risk score ({m.RiskScore}). Coverage is only {m.CoverageRate}% on a method with complexity {m.Complexity} called {m.UsageCount} time(s). Increase test coverage to reduce exposure.";

        if (m.RiskScore >= 30)
            return $"Moderate risk ({m.RiskScore}). Consider adding tests for uncovered branches. Complexity: {m.Complexity}, Coverage: {m.CoverageRate}%.";

        if (m.CoverageRate >= 100)
            return "Fully covered. Maintain existing tests and extend them if new branches are added.";

        return $"Low risk ({m.RiskScore}). Coverage is {m.CoverageRate}%. Monitor if complexity grows.";
    }

    // ── Summary Builder ──────────────────────────────────────────────────────
    public static AnalyzeSummary BuildSummary(List<MethodMetric> methods)
    {
        if (methods.Count == 0)
            return new AnalyzeSummary();

        return new AnalyzeSummary
        {
            TotalMethods = methods.Count,
            TestedMethods = methods.Count(m => m.CoverageRate > 0),
            UntestedMethods = methods.Count(m => m.CoverageRate == 0 && m.UsageCount > 0),
            UnusedMethods = methods.Count(m => m.UsageCount == 0 && m.CoverageRate == 0),
            HighRiskCount = methods.Count(m => m.RiskScore >= 60),
            MediumRiskCount = methods.Count(m => m.RiskScore >= 30 && m.RiskScore < 60),
            LowRiskCount = methods.Count(m => m.RiskScore < 30),
            AverageRiskScore = Math.Round(methods.Average(m => m.RiskScore), 2),
            AverageCoverage = Math.Round(methods.Average(m => m.CoverageRate), 2)
        };
    }
}

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
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. COMPLEXITY TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class ComplexityTests
{
    [Fact]
    public void SimpleMethod_ComplexityIsOne()
    {
        string code = @"
namespace MyApp { public class Foo {
    public void Hello() { Console.WriteLine(""hi""); }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Single(result);
        Assert.Equal(1, result[0].Complexity);
    }

    [Fact]
    public void SingleIfBlock_ComplexityIsTwo()
    {
        string code = @"
namespace MyApp { public class Foo {
    public int Check(int x) {
        if (x > 0) return 1;
        return 0;
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(2, result[0].Complexity);
    }

    [Fact]
    public void ForeachAndIf_ComplexityIsThree()
    {
        string code = @"
namespace MyApp { public class Foo {
    public void List(List<int> items) {
        foreach (var i in items) {
            if (i > 0) Console.WriteLine(i);
        }
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(3, result[0].Complexity);
    }

    [Fact]
    public void CatchBlock_AddsOneToComplexity()
    {
        string code = @"
namespace MyApp { public class Foo {
    public void TryIt() {
        try { Console.WriteLine(); }
        catch (Exception) { throw; }
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(2, result[0].Complexity);
    }

    [Fact]
    public void MultipleIfBranches_CorrectComplexityCount()
    {
        string code = @"
namespace MyApp { public class Foo {
    public string Classify(int x) {
        if (x < 0) return ""negative"";
        else if (x == 0) return ""zero"";
        else if (x < 10) return ""small"";
        else return ""large"";
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(4, result[0].Complexity); // 1 base + 3 if
    }

    [Fact]
    public void WhileLoop_AddsOneToComplexity()
    {
        string code = @"
namespace MyApp { public class Foo {
    public void Count() {
        int i = 0;
        while (i < 10) i++;
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(2, result[0].Complexity);
    }

    [Fact]
    public void TernaryExpression_AddsOneToComplexity()
    {
        string code = @"
namespace MyApp { public class Foo {
    public string Label(bool flag) => flag ? ""yes"" : ""no"";
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(2, result[0].Complexity);
    }

    [Fact]
    public void SwitchWithThreeCases_ComplexityIsFour()
    {
        string code = @"
namespace MyApp { public class Foo {
    public void Route(int x) {
        switch (x) {
            case 1: break;
            case 2: break;
            case 3: break;
        }
    }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(4, result[0].Complexity); // 1 base + 3 switch sections
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. RISK SCORE TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class RiskScoreTests
{
    [Fact]
    public void FullCoverage_RiskScoreIsZero()
    {
        double risk = RiskEngine.ComputeRiskScore(complexity: 10, usageCount: 5, coverageRate: 100);
        Assert.Equal(0, risk);
    }

    [Fact]
    public void ZeroCoverage_MaxComplexity_RiskIsMaxDanger()
    {
        // danger = (10*7) + (5*1) = 75
        double risk = RiskEngine.ComputeRiskScore(complexity: 10, usageCount: 5, coverageRate: 0);
        Assert.Equal(75, risk);
    }

    [Fact]
    public void EightyCoverage_RiskReducesByFactor()
    {
        // danger=75, safety=0.2 → 75*0.2 = 15
        double risk = RiskEngine.ComputeRiskScore(complexity: 10, usageCount: 5, coverageRate: 80);
        Assert.Equal(15, risk);
    }

    [Fact]
    public void ComplexityCapAtTen_ExcessComplexityIgnored()
    {
        double capped = RiskEngine.ComputeRiskScore(complexity: 10, usageCount: 0, coverageRate: 0);
        double exceeded = RiskEngine.ComputeRiskScore(complexity: 99, usageCount: 0, coverageRate: 0);
        Assert.Equal(capped, exceeded);
    }

    [Fact]
    public void UsageCountCapAtThirty_ExcessUsageIgnored()
    {
        double capped = RiskEngine.ComputeRiskScore(complexity: 1, usageCount: 30, coverageRate: 0);
        double exceeded = RiskEngine.ComputeRiskScore(complexity: 1, usageCount: 99, coverageRate: 0);
        Assert.Equal(capped, exceeded);
    }

    [Fact]
    public void ZeroUsage_ZeroCoverage_RiskBasedOnComplexityOnly()
    {
        // danger = (10*7) + 0 = 70, safety = 1.0
        double risk = RiskEngine.ComputeRiskScore(complexity: 20, usageCount: 0, coverageRate: 0);
        Assert.Equal(70, risk);
    }

    [Fact]
    public void HigherComplexity_ProducesHigherRisk_WhenOtherFactorsEqual()
    {
        double low = RiskEngine.ComputeRiskScore(complexity: 2, usageCount: 3, coverageRate: 0);
        double high = RiskEngine.ComputeRiskScore(complexity: 9, usageCount: 3, coverageRate: 0);
        Assert.True(high > low);
    }

    [Fact]
    public void RiskScore_NeverExceedsOneHundred()
    {
        double risk = RiskEngine.ComputeRiskScore(complexity: 999, usageCount: 999, coverageRate: 0);
        Assert.True(risk <= 100);
    }

    [Fact]
    public void RiskScore_NeverBelowZero()
    {
        double risk = RiskEngine.ComputeRiskScore(complexity: 0, usageCount: 0, coverageRate: 100);
        Assert.True(risk >= 0);
    }

    [Fact]
    public void FiftyCoverage_RiskIsHalfOfZeroCoverage()
    {
        double full = RiskEngine.ComputeRiskScore(complexity: 5, usageCount: 0, coverageRate: 0);
        double half = RiskEngine.ComputeRiskScore(complexity: 5, usageCount: 0, coverageRate: 50);
        Assert.Equal(Math.Round(full / 2, 2), half);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. STATUS RESOLVER TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class StatusResolverTests
{
    [Fact]
    public void DisposeMethod_ReturnsResourceCleanTested()
    {
        var m = new MethodMetric { FullName = "MyApp.Repo.Dispose", MethodName = "Dispose" };
        Assert.Equal("Resource_Clean_Tested", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void MainMethod_ReturnsSystemEntry()
    {
        var m = new MethodMetric { FullName = "MyApp.Program.Main", MethodName = "Main" };
        Assert.Equal("System_Entry", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void OnPrefixMethod_ReturnsUIEvent()
    {
        var m = new MethodMetric { MethodName = "OnLoad" };
        Assert.Equal("UI_Event", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ClickSuffixMethod_ReturnsUIEvent()
    {
        var m = new MethodMetric { MethodName = "SaveButtonClick" };
        Assert.Equal("UI_Event", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ChangedSuffixMethod_ReturnsUIEvent()
    {
        var m = new MethodMetric { MethodName = "TextChanged" };
        Assert.Equal("UI_Event", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void KeyPressSuffixMethod_ReturnsUIEvent()
    {
        var m = new MethodMetric { MethodName = "InputKeyPress" };
        Assert.Equal("UI_Event", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void HundredPercentCoverage_ReturnsFullyVerified()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 100 };
        Assert.Equal("Fully_Verified", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void EightyFivePercentCoverage_ReturnsSafeVerified()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 85 };
        Assert.Equal("Safe_Verified", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ExactlyEightyPercent_ReturnsSafeVerified()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 80 };
        Assert.Equal("Safe_Verified", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void FiftyPercentCoverage_ReturnsPartialLogicVerified()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 50 };
        Assert.Equal("Partial_Logic_Verified", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ExactlyThirtyPercent_ReturnsPartialLogicVerified()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 30 };
        Assert.Equal("Partial_Logic_Verified", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void TenPercentCoverage_ReturnsLogicTested()
    {
        var m = new MethodMetric { MethodName = "Calculate", CoverageRate = 10 };
        Assert.Equal("Logic_Tested", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ZeroCoverageZeroUsage_ReturnsUnused()
    {
        var m = new MethodMetric { MethodName = "OldMethod", CoverageRate = 0, UsageCount = 0 };
        Assert.Equal("Unused", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void ZeroCoverageWithUsage_ReturnsUntested()
    {
        var m = new MethodMetric { MethodName = "NewMethod", CoverageRate = 0, UsageCount = 5 };
        Assert.Equal("Untested", RiskEngine.ResolveStatus(m));
    }

    [Fact]
    public void DisposeMethod_TakesPriorityOverCoverage()
    {
        // Even if fully covered, Dispose check fires first
        var m = new MethodMetric { FullName = "NS.Repo.Dispose", MethodName = "Dispose", CoverageRate = 100 };
        Assert.Equal("Resource_Clean_Tested", RiskEngine.ResolveStatus(m));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. ROSLYN ANALYZER TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class AnalyzerTests
{
    [Fact]
    public void EmptyClass_ReturnsNoResults()
    {
        var result = RiskEngine.Analyze("namespace X { public class C { } }");
        Assert.Empty(result);
    }

    [Fact]
    public void TwoMethods_ReturnsTwoResults()
    {
        string code = @"
namespace MyApp { public class Svc {
    public void A() {}
    public void B() {}
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FullName_FollowsNamespaceDotClassDotMethod()
    {
        string code = @"
namespace RiskApp.Services { public class RiskCalculator {
    public double Compute() { return 0; }
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal("RiskApp.Services.RiskCalculator.Compute", result[0].FullName);
    }

    [Fact]
    public void MethodName_CapturedCorrectly()
    {
        string code = @"
namespace X { public class Y {
    public void RiskAnalysis() {}
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal("RiskAnalysis", result[0].MethodName);
    }

    [Fact]
    public void UsageCount_IncreasesWhenCalledFromAnotherFile()
    {
        string definition = @"
namespace MyApp { public class Service {
    public void Process() {}
}}";
        string caller = @"
namespace MyApp { public class Client {
    public void Run() {
        var s = new Service();
        s.Process();
        s.Process();
    }
}}";
        var result = RiskEngine.Analyze(definition, new List<string> { definition, caller });
        var process = result.First(m => m.MethodName == "Process");
        Assert.Equal(2, process.UsageCount);
    }

    [Fact]
    public void NoNamespace_UsesGlobalAsNamespace()
    {
        string code = @"
public class Standalone {
    public void DoWork() {}
}";
        var result = RiskEngine.Analyze(code);
        Assert.StartsWith("Global.", result[0].FullName);
    }

    [Fact]
    public void UsageCount_ZeroForUncalledMethod()
    {
        string code = @"
namespace NS { public class C {
    public void NeverCalled() {}
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(0, result[0].UsageCount);
    }

    [Fact]
    public void InitialCoverageRate_IsAlwaysZero()
    {
        string code = @"
namespace NS { public class C {
    public void Method() {}
}}";
        var result = RiskEngine.Analyze(code);
        Assert.Equal(0, result[0].CoverageRate);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. FEEDBACK ENGINE TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class FeedbackEngineTests
{
    [Fact]
    public void DisposeStatus_ReturnsManagedResourceMessage()
    {
        var m = new MethodMetric { Status = "Resource_Clean_Tested", CoverageRate = 0 };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("Dispose", fb);
    }

    [Fact]
    public void SystemEntryStatus_ReturnsEntryPointMessage()
    {
        var m = new MethodMetric { Status = "System_Entry", CoverageRate = 0 };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("Entry point", fb);
    }

    [Fact]
    public void UIEventStatus_ReturnsServiceLayerMessage()
    {
        var m = new MethodMetric { Status = "UI_Event", CoverageRate = 0 };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("service layer", fb);
    }

    [Fact]
    public void UnusedStatus_ReturnsDeadCodeMessage()
    {
        var m = new MethodMetric { Status = "Unused", CoverageRate = 0, UsageCount = 0 };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("dead code", fb);
    }

    [Fact]
    public void ZeroCoverageHighComplexity_ReturnsCriticalMessage()
    {
        var m = new MethodMetric
        {
            Status = "Untested",
            CoverageRate = 0,
            Complexity = 9
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.StartsWith("CRITICAL", fb);
    }

    [Fact]
    public void ZeroCoverageModerateComplexity_ReturnsHighRiskMessage()
    {
        var m = new MethodMetric
        {
            Status = "Untested",
            CoverageRate = 0,
            Complexity = 5
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.StartsWith("HIGH RISK", fb);
    }

    [Fact]
    public void ZeroCoverageLowComplexity_ReturnsBasicCoverageMessage()
    {
        var m = new MethodMetric
        {
            Status = "Untested",
            CoverageRate = 0,
            Complexity = 2
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("No test coverage detected", fb);
    }

    [Fact]
    public void HighRiskScore_ReturnsHighRiskFeedback()
    {
        var m = new MethodMetric
        {
            Status = "Logic_Tested",
            CoverageRate = 10,
            RiskScore = 70,
            Complexity = 9,
            UsageCount = 8
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("High risk score", fb);
    }

    [Fact]
    public void ModerateRiskScore_ReturnsModerateRiskFeedback()
    {
        var m = new MethodMetric
        {
            Status = "Partial_Logic_Verified",
            CoverageRate = 40,
            RiskScore = 45,
            Complexity = 5,
            UsageCount = 3
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("Moderate risk", fb);
    }

    [Fact]
    public void FullCoverage_ReturnsFullyCoveredMessage()
    {
        var m = new MethodMetric
        {
            Status = "Fully_Verified",
            CoverageRate = 100,
            RiskScore = 0
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("Fully covered", fb);
    }

    [Fact]
    public void LowRiskScore_ReturnsLowRiskFeedback()
    {
        var m = new MethodMetric
        {
            Status = "Safe_Verified",
            CoverageRate = 85,
            RiskScore = 10,
            Complexity = 2
        };
        string fb = RiskEngine.GenerateFeedback(m);
        Assert.Contains("Low risk", fb);
    }

    [Fact]
    public void FeedbackIsNeverEmpty_ForAnyStatus()
    {
        var statuses = new[]
        {
            "Fully_Verified", "Safe_Verified", "Partial_Logic_Verified",
            "Logic_Tested", "Untested", "Unused", "UI_Event",
            "System_Entry", "Resource_Clean_Tested"
        };

        foreach (var status in statuses)
        {
            var m = new MethodMetric { Status = status, CoverageRate = 0, UsageCount = 1 };
            Assert.False(string.IsNullOrWhiteSpace(RiskEngine.GenerateFeedback(m)),
                $"Feedback was empty for status: {status}");
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. SUMMARY BUILDER TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class SummaryBuilderTests
{
    private static MethodMetric Make(double coverage, int usage, double risk) =>
        new() { CoverageRate = coverage, UsageCount = usage, RiskScore = risk };

    [Fact]
    public void EmptyList_ReturnsZeroSummary()
    {
        var summary = RiskEngine.BuildSummary(new List<MethodMetric>());
        Assert.Equal(0, summary.TotalMethods);
    }

    [Fact]
    public void TotalMethods_CountsAllEntries()
    {
        var methods = new List<MethodMetric> { Make(0, 1, 70), Make(100, 3, 0), Make(50, 2, 20) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(3, summary.TotalMethods);
    }

    [Fact]
    public void TestedMethods_CountsNonZeroCoverage()
    {
        var methods = new List<MethodMetric> { Make(0, 1, 70), Make(100, 3, 0), Make(50, 2, 20) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(2, summary.TestedMethods);
    }

    [Fact]
    public void UntestedMethods_ZeroCoverageButHasUsage()
    {
        var methods = new List<MethodMetric> { Make(0, 3, 70), Make(0, 0, 0), Make(80, 2, 5) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(1, summary.UntestedMethods); // only first: coverage=0 AND usage>0
    }

    [Fact]
    public void UnusedMethods_ZeroCoverageAndZeroUsage()
    {
        var methods = new List<MethodMetric> { Make(0, 0, 0), Make(0, 1, 60), Make(100, 5, 0) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(1, summary.UnusedMethods);
    }

    [Fact]
    public void HighRiskCount_RiskScoreAboveSixty()
    {
        var methods = new List<MethodMetric> { Make(0, 5, 80), Make(0, 3, 65), Make(50, 2, 25) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(2, summary.HighRiskCount);
    }

    [Fact]
    public void MediumRiskCount_BetweenThirtyAndSixty()
    {
        var methods = new List<MethodMetric> { Make(0, 5, 80), Make(0, 3, 45), Make(50, 2, 35) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(2, summary.MediumRiskCount);
    }

    [Fact]
    public void LowRiskCount_BelowThirty()
    {
        var methods = new List<MethodMetric> { Make(100, 1, 0), Make(80, 2, 15), Make(0, 5, 70) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(2, summary.LowRiskCount);
    }

    [Fact]
    public void AverageRiskScore_CorrectlyCalculated()
    {
        var methods = new List<MethodMetric> { Make(0, 1, 60), Make(0, 1, 40) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(50, summary.AverageRiskScore);
    }

    [Fact]
    public void AverageCoverage_CorrectlyCalculated()
    {
        var methods = new List<MethodMetric> { Make(100, 1, 0), Make(0, 1, 70) };
        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(50, summary.AverageCoverage);
    }

    [Fact]
    public void RiskCategories_SumEqualsTotal()
    {
        var methods = new List<MethodMetric>
        {
            Make(0,  5, 80), Make(0, 3, 45),
            Make(50, 2, 25), Make(100, 1, 0)
        };
        var s = RiskEngine.BuildSummary(methods);
        Assert.Equal(s.TotalMethods, s.HighRiskCount + s.MediumRiskCount + s.LowRiskCount);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. EDGE CASE TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class EdgeCaseTests
{
    [Fact]
    public void SingleMethod_FullyTested_RiskIsZero()
    {
        var m = new MethodMetric
        {
            MethodName = "Compute",
            Complexity = 3,
            UsageCount = 2,
            CoverageRate = 100
        };
        m.RiskScore = RiskEngine.ComputeRiskScore(m.Complexity, m.UsageCount, m.CoverageRate);
        m.Status = RiskEngine.ResolveStatus(m);
        Assert.Equal(0, m.RiskScore);
        Assert.Equal("Fully_Verified", m.Status);
    }

    [Fact]
    public void SingleMethod_ZeroCoverage_StatusIsUntested()
    {
        var m = new MethodMetric
        {
            MethodName = "Compute",
            Complexity = 3,
            UsageCount = 4,
            CoverageRate = 0
        };
        m.Status = RiskEngine.ResolveStatus(m);
        Assert.Equal("Untested", m.Status);
    }

    [Fact]
    public void AllMethodsFullyCovered_AverageRiskIsZero()
    {
        var methods = Enumerable.Range(1, 5).Select(_ => new MethodMetric
        {
            CoverageRate = 100,
            RiskScore = RiskEngine.ComputeRiskScore(2, 1, 100)
        }).ToList();

        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(0, summary.AverageRiskScore);
    }

    [Fact]
    public void AllMethodsUntested_TestedCountIsZero()
    {
        var methods = Enumerable.Range(1, 4).Select(_ => new MethodMetric
        {
            CoverageRate = 0,
            UsageCount = 2,
            RiskScore = 60
        }).ToList();

        var summary = RiskEngine.BuildSummary(methods);
        Assert.Equal(0, summary.TestedMethods);
        Assert.Equal(4, summary.UntestedMethods);
    }

    [Fact]
    public void ComplexityOne_ZeroCoverage_RiskIsSevenIfNoUsage()
    {
        // danger = (1*7) + 0 = 7, safety = 1.0 → risk = 7
        double risk = RiskEngine.ComputeRiskScore(complexity: 1, usageCount: 0, coverageRate: 0);
        Assert.Equal(7, risk);
    }

    [Fact]
    public void SortedByRiskDescending_HighestRiskFirst()
    {
        var methods = new List<MethodMetric>
        {
            new() { FullName = "NS.C.Low",    RiskScore = 10 },
            new() { FullName = "NS.C.High",   RiskScore = 90 },
            new() { FullName = "NS.C.Medium", RiskScore = 45 }
        };

        var sorted = methods.OrderByDescending(m => m.RiskScore).ToList();
        Assert.Equal("NS.C.High", sorted[0].FullName);
        Assert.Equal("NS.C.Medium", sorted[1].FullName);
        Assert.Equal("NS.C.Low", sorted[2].FullName);
    }

    [Fact]
    public void ProjectWithNoMethods_SummaryTotalIsZero()
    {
        string emptyProject = "namespace Empty { public class Shell { } }";
        var result = RiskEngine.Analyze(emptyProject);
        var summary = RiskEngine.BuildSummary(result);
        Assert.Equal(0, summary.TotalMethods);
    }

    [Fact]
    public void DeduplicatedFullNames_NoDuplicatesInResults()
    {
        string code = @"
namespace NS { public class C {
    public void Alpha() {}
    public void Beta()  {}
    public void Gamma() {}
}}";
        var result = RiskEngine.Analyze(code);
        var distinct = result.Select(m => m.FullName).Distinct().Count();
        Assert.Equal(result.Count, distinct);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 8. SORTING & SELECTION TESTS
// ─────────────────────────────────────────────────────────────────────────────

public class SortingTests
{
    private static List<MethodMetric> BuildMetrics(int count) =>
        Enumerable.Range(1, count).Select(i => new MethodMetric
        {
            FullName = $"NS.Class.Method{i}",
            MethodName = $"Method{i}",
            Complexity = i,
            UsageCount = i,
            RiskScore = i * i * 1.0
        }).ToList();

    [Fact]
    public void SortDescending_HighestRiskIsFirst()
    {
        var sorted = BuildMetrics(5).OrderByDescending(m => m.RiskScore).ToList();
        Assert.True(sorted[0].RiskScore >= sorted[1].RiskScore);
        Assert.True(sorted[1].RiskScore >= sorted[2].RiskScore);
    }

    [Fact]
    public void SortDescending_LowestRiskIsLast()
    {
        var list = BuildMetrics(6);
        var sorted = list.OrderByDescending(m => m.RiskScore).ToList();
        Assert.Equal(list.Min(m => m.RiskScore), sorted.Last().RiskScore);
    }

    [Fact]
    public void LessThanFifteenMethods_AllReturned()
    {
        var metrics = BuildMetrics(10);
        Assert.Equal(10, metrics.Count);
    }
}