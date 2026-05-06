using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Xml.Linq;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// CORS politikası: Frontend bağlantısı için gerekli
builder.Services.AddCors(options => options.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", version = "2.2" }));

app.MapPost("/api/analyze", async (AnalyzeRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.ProjectPath) || !Directory.Exists(req.ProjectPath))
        return Results.BadRequest(new { error = "Geçerli bir proje yolu girilmelidir." });

    // 1. ADIM: Roslyn Analizi
    var csFiles = Directory.GetFiles(req.ProjectPath, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToList();

    var allCode = csFiles.Select(File.ReadAllText).ToList();
    var allMetrics = new List<MethodMetric>();

    foreach (var file in csFiles)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var analyzer = new MethodAnalyzer(allCode, file, req.ProjectPath);
        analyzer.Visit(tree.GetRoot());
        allMetrics.AddRange(analyzer.Results);
    }

    // 2. ADIM: Test Koşturma
    await Task.Run(() => {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test --collect:\"XPlat Code Coverage\" --nologo",
            WorkingDirectory = req.ProjectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        process?.WaitForExit();
    });

    // 3. ADIM: XML Parse ve Eşleştirme
    // 3. ADIM: XML Parse ve Eşleştirme
    // 3. ADIM: XML Parse ve Eşleştirme
    var allTestResults = Directory.GetDirectories(req.ProjectPath, "TestResults", SearchOption.AllDirectories);
    string? coverageFile = null;

    if (allTestResults.Any())
    {
        coverageFile = allTestResults
            .SelectMany(dir => Directory.GetFiles(dir, "coverage.cobertura.xml", SearchOption.AllDirectories))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .FirstOrDefault();
    }

    if (coverageFile != null)
    {
        Console.WriteLine("OKUNAN DOSYA: " + coverageFile);
        using var fs = new FileStream(coverageFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var doc = XDocument.Load(fs);

        // XML'deki tüm metotları düz bir listeye alalım
        var xmlMethods = doc.Descendants("method")
            .Select(m => new {
                // Namespace + ClassName (Örn: MultiShop.Order.Application.Features.Handlers.MyHandler)
                ClassName = m.Parent?.Parent?.Attribute("name")?.Value ?? "",
                // Metot Adı (Signature dahil: Handle(Request req, ...))
                Signature = m.Attribute("name")?.Value ?? "",
                Rate = m.Attribute("line-rate")?.Value ?? "0"
            }).ToList();


        foreach (var m in allMetrics)
        {
            string lowerMethodName = m.MethodName.ToLower();
            string lowerClassName = m.FullName.Split('.').Reverse().Skip(1).FirstOrDefault()?.ToLower() ?? "";

            var match = xmlMethods.FirstOrDefault(x =>
            {
                string xmlClass = x.ClassName.ToLower();
                string xmlSig = x.Signature.ToLower();

                // 1. KURAL: Sınıf isimleri uyuşmalı
                bool isSameClass = xmlClass.Contains(lowerClassName);
                // 2. KURAL: Async metotları yakala (<handle>d__2 gibi)
                bool isAsyncMatch = xmlClass.Contains("<" + lowerMethodName + ">");
                // 3. KURAL: Normal metot veya constructor
                bool isDirectMatch = xmlSig.Contains(lowerMethodName + "(") || xmlSig == ".ctor";

                return isSameClass && (isAsyncMatch || isDirectMatch);
            });

            if (match != null && double.TryParse(match.Rate, NumberStyles.Any, CultureInfo.InvariantCulture, out double rate))
            {
                m.CoverageRate = Math.Round(rate * 100, 2);
            }
        }
    }

    foreach (var m in allMetrics)
    {
        m.Status = DetermineStatus(m);

        // Tehlike Faktörü: Karmaşıklık (max 10) ve Kullanım (max 30)
        double baseDanger = (Math.Min(m.Complexity, 10) * 7.0) + (Math.Min(m.UsageCount, 30) * 1.0);

        // Koruma Faktörü: %100 Test edildiyse risk çarpanı 0 olur.
        double safetyMultiplier = (100.0 - m.CoverageRate) / 100.0;

        // Final Risk (0-100 arası)
        double finalRisk = baseDanger * safetyMultiplier;
        m.RiskScore = Math.Round(Math.Clamp(finalRisk, 0, 100), 2);
    }

    var finalResult = allMetrics.GroupBy(x => x.FullName)
                                .Select(g => g.First())
                                .OrderByDescending(x => x.RiskScore)
                                .ToList();

    return Results.Ok(finalResult);
});

app.Run("http://localhost:5003");

// --- Yardımcı Fonksiyonlar ve Modeller ---

static string DetermineStatus(MethodMetric m)
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

public record AnalyzeRequest(string ProjectPath);

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

public class MethodAnalyzer : CSharpSyntaxWalker
{
    private readonly List<string> _allCode;
    private readonly string _filePath;
    private readonly string _projectRoot;
    public List<MethodMetric> Results { get; } = new();

    public MethodAnalyzer(List<string> allCode, string filePath, string projectRoot)
    {
        _allCode = allCode;
        _filePath = filePath;
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

        int complexity = 1 + node.DescendantNodes().Count(n =>
            n is IfStatementSyntax || n is ForStatementSyntax ||
            n is ForEachStatementSyntax || n is SwitchSectionSyntax ||
            n is CatchClauseSyntax || n is ConditionalExpressionSyntax ||
            n is WhileStatementSyntax || n is DoStatementSyntax);

        int usage = 0;
        string pattern = methodName + "(";
        foreach (var code in _allCode)
        {
            int idx = 0;
            while ((idx = code.IndexOf(pattern, idx, StringComparison.Ordinal)) != -1)
            { usage++; idx += pattern.Length; }
        }
        usage = Math.Max(0, usage - 1);

        Results.Add(new MethodMetric
        {
            FullName = fullName,
            MethodName = methodName,
            FilePath = Path.GetRelativePath(_projectRoot, _filePath),
            Complexity = complexity,
            UsageCount = usage,
            CoverageRate = 0,
            Status = "",
            RiskScore = 0
        });

        base.VisitMethodDeclaration(node);
    }
}