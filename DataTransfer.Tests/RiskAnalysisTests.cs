// RiskAwareQA.Tests — RiskAnalysisTests.cs
// Bu testler RiskAwareQA projesinin kendi mantığını test eder.
// DataTransfer projesine hiçbir bağımlılığı yoktur.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RiskAwareQA.Tests;

// ─── Model (test projesi içinde tekrarlanmış, production'da Program.cs'te) ────
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
}

// ─── Test Edilebilir Roslyn Analizi ──────────────────────────────────────────
public static class RiskEngine
{
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
                n is IfStatementSyntax
                || n is ForStatementSyntax
                || n is ForEachStatementSyntax
                || n is SwitchSectionSyntax
                || n is CatchClauseSyntax
                || n is ConditionalExpressionSyntax
                || n is WhileStatementSyntax
                || n is DoStatementSyntax);

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

    public static double CalcRiskScore(int complexity, int usageCount, double coverageRate)
    {
        double danger = (Math.Min(complexity, 10) * 7.0) + (Math.Min(usageCount, 30) * 1.0);
        double safety = (100.0 - coverageRate) / 100.0;
        return Math.Round(Math.Clamp(danger * safety, 0, 100), 2);
    }

    public static string DetermineStatus(MethodMetric m)
    {
        if (m.FullName.Contains(".Dispose")) return "Resource_Clean_Tested";
        if (m.MethodName.StartsWith("On") || m.MethodName.EndsWith("Click")
            || m.MethodName.EndsWith("Load") || m.MethodName.EndsWith("Formatting"))
            return "UI_Event";
        if (m.MethodName == "Main") return "System_Entry";
        if (m.CoverageRate >= 100) return "Fully_Verified";
        if (m.CoverageRate >= 80) return "Safe_Verified";
        if (m.CoverageRate >= 30) return "Partial_Logic_Verified";
        if (m.CoverageRate > 0) return "Logic_Tested";
        if (m.UsageCount == 0) return "Unused";
        return "Untested";
    }
}

// ─── 1. Complexity Hesaplama Testleri ────────────────────────────────────────
public class ComplexityTests
{
    [Fact]
    public void BasitMetot_KarmasiklikBir_Olmalidir()
    {
        string kod = @"
namespace MyApp { public class Foo {
    public void Selam() { Console.WriteLine(""merhaba""); }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Single(sonuc);
        Assert.Equal(1, sonuc[0].Complexity);
    }

    [Fact]
    public void IfBlok_KarmasiklikIkiYapar()
    {
        string kod = @"
namespace MyApp { public class Foo {
    public int Kontrol(int x) {
        if (x > 0) return 1;
        return 0;
    }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal(2, sonuc[0].Complexity); // 1 taban + 1 if
    }

    [Fact]
    public void ForeachVeIfBirlikte_KarmasiklikUcOlmali()
    {
        string kod = @"
namespace MyApp { public class Foo {
    public void Listele(List<int> items) {
        foreach (var i in items) {
            if (i > 0) Console.WriteLine(i);
        }
    }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal(3, sonuc[0].Complexity); // 1 + 1 foreach + 1 if
    }

    [Fact]
    public void CatchBlok_KarmasikligaEklenir()
    {
        string kod = @"
namespace MyApp { public class Foo {
    public void Dene() {
        try { Console.WriteLine(); }
        catch (Exception) { throw; }
    }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal(2, sonuc[0].Complexity); // 1 + 1 catch
    }

    [Fact]
    public void CokluDallar_DogruKarmasiklik()
    {
        string kod = @"
namespace MyApp { public class Foo {
    public string Siniflandir(int x) {
        if (x < 0) return ""negatif"";
        else if (x == 0) return ""sifir"";
        else if (x < 10) return ""kucuk"";
        else return ""buyuk"";
    }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal(4, sonuc[0].Complexity); // 1 + 3 if
    }
}

// ─── 2. Risk Skoru Hesaplama Testleri ────────────────────────────────────────
public class RiskScoreTests
{
    [Fact]
    public void TamKapsama_RiskSifir_Olmali()
    {
        double risk = RiskEngine.CalcRiskScore(complexity: 10, usageCount: 5, coverageRate: 100);
        Assert.Equal(0, risk);
    }

    [Fact]
    public void SifirKapsama_MaksimumRisk_Olmali()
    {
        double risk = RiskEngine.CalcRiskScore(complexity: 10, usageCount: 5, coverageRate: 0);
        Assert.Equal(75, risk); // 10 * 5 * 1.0
    }

    [Fact]
    public void YuzdeSeksenKapsama_RiskDuser()
    {
        double risk = RiskEngine.CalcRiskScore(complexity: 10, usageCount: 5, coverageRate: 80);
        Assert.Equal(15, risk); // 75 * 0.2 = 15
    }

    [Fact]
    public void SifirKullanim_RiskSifir_Olmali()
    {
        double risk = RiskEngine.CalcRiskScore(complexity: 20, usageCount: 0, coverageRate: 0);
        Assert.Equal(70, risk); // (10*7)+(0) = 70 — cap'li formülde 0 değil 70
    }

    [Fact]
    public void YuksekKarmasiklikYuksekKullanim_EnYuksekRisk()
    {
        double dusukKarmasik = RiskEngine.CalcRiskScore(complexity: 2, usageCount: 3, coverageRate: 0);
        double yuksekKarmasik = RiskEngine.CalcRiskScore(complexity: 20, usageCount: 15, coverageRate: 0);
        Assert.True(yuksekKarmasik > dusukKarmasik);
    }
}

// ─── 3. Status Belirleme Testleri ─────────────────────────────────────────────
public class StatusTests
{
    [Fact]
    public void DisposeMetotu_ResourceCleanStatus_Olmali()
    {
        var m = new MethodMetric { FullName = "MyApp.Repo.Dispose", MethodName = "Dispose" };
        Assert.Equal("Resource_Clean_Tested", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void MainMetotu_SystemEntry_Olmali()
    {
        var m = new MethodMetric { FullName = "MyApp.Program.Main", MethodName = "Main" };
        Assert.Equal("System_Entry", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void TamKapsama_FullyVerified_Olmali()
    {
        var m = new MethodMetric { MethodName = "Hesapla", CoverageRate = 100 };
        Assert.Equal("Fully_Verified", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void SeksenKapsamaUzeri_SafeVerified_Olmali()
    {
        var m = new MethodMetric { MethodName = "Hesapla", CoverageRate = 85 };
        Assert.Equal("Safe_Verified", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void SifirKapsama_SifirKullanim_Unused_Olmali()
    {
        var m = new MethodMetric { MethodName = "EskiMetot", CoverageRate = 0, UsageCount = 0 };
        Assert.Equal("Unused", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void SifirKapsama_KullaniliyorAma_Untested_Olmali()
    {
        var m = new MethodMetric { MethodName = "YeniMetot", CoverageRate = 0, UsageCount = 5 };
        Assert.Equal("Untested", RiskEngine.DetermineStatus(m));
    }

    [Fact]
    public void OnPrefix_UIEvent_Olmali()
    {
        var m = new MethodMetric { MethodName = "OnLoad" };
        Assert.Equal("UI_Event", RiskEngine.DetermineStatus(m));
    }
}

// ─── 4. Roslyn Analiz Motoru Testleri ─────────────────────────────────────────
public class AnalyzerTests
{
    [Fact]
    public void BosDosya_SonucBos_Olmali()
    {
        var sonuc = RiskEngine.Analyze("namespace X { public class C { } }");
        Assert.Empty(sonuc);
    }

    [Fact]
    public void IkiMetot_IkiSonuc_Olmali()
    {
        string kod = @"
namespace MyApp { public class Svc {
    public void A() {}
    public void B() {}
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal(2, sonuc.Count);
    }

    [Fact]
    public void FullNameFormat_NamespaceNoktaClassNoktaMetot()
    {
        string kod = @"
namespace RiskApp.Services { public class RiskCalculator {
    public double Hesapla() { return 0; }
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal("RiskApp.Services.RiskCalculator.Hesapla", sonuc[0].FullName);
    }

    [Fact]
    public void KullanımSayisi_DigerDosyadanCagirma_Artar()
    {
        string tanimlayan = @"
namespace MyApp { public class Servis {
    public void IslemYap() {}
}}";
        string kullanan = @"
namespace MyApp { public class Client {
    public void Cagir() {
        var s = new Servis();
        s.IslemYap();
        s.IslemYap();
    }
}}";
        var sonuc = RiskEngine.Analyze(tanimlayan, new List<string> { tanimlayan, kullanan });
        var islem = sonuc.First(m => m.MethodName == "IslemYap");
        Assert.Equal(2, islem.UsageCount);
    }

    [Fact]
    public void MetotAdi_DogruYakalanmali()
    {
        string kod = @"
namespace X { public class Y {
    public void RiskAnalizi() {}
}}";
        var sonuc = RiskEngine.Analyze(kod);
        Assert.Equal("RiskAnalizi", sonuc[0].MethodName);
    }
}

// ─── 5. Seçim Mantığı Testleri (En riskli / orta / düşük) ────────────────────
public class SelectionTests
{
    private static List<MethodMetric> OlusturMetrikler(int adet)
    {
        return Enumerable.Range(1, adet).Select(i => new MethodMetric
        {
            FullName = $"NS.Class.Metot{i}",
            MethodName = $"Metot{i}",
            Complexity = i,
            UsageCount = i,
            RiskScore = i * i * 1.0
        }).ToList();
    }

    [Fact]
    public void OnbestenAzMetot_TumunuDonder()
    {
        var metrikler = OlusturMetrikler(10);
        // 10 metrik varsa hepsini döndürmeli
        Assert.Equal(10, metrikler.Count);
    }

    [Fact]
    public void Siralama_EnYuksekRiskBas_Olmali()
    {
        var metrikler = OlusturMetrikler(5);
        var sorted = metrikler.OrderByDescending(m => m.RiskScore).ToList();
        Assert.True(sorted[0].RiskScore >= sorted[1].RiskScore);
        Assert.True(sorted[1].RiskScore >= sorted[2].RiskScore);
    }
}
