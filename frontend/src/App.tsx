import { useMemo, useState } from "react";
import {
  Bar,
  BarChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";


type Method = {
  fullName?: string;
  methodName?: string;
  filePath?: string;
  complexity?: number;
  usageCount?: number;
  coverageRate?: number;
  status?: string;
  riskScore?: number;
  feedback?: string;
  severity?: string;
  isEstimatedRisk?: boolean;

  FullName?: string;
  MethodName?: string;
  FilePath?: string;
  Complexity?: number;
  UsageCount?: number;
  CoverageRate?: number;
  Status?: string;
  RiskScore?: number;
  Feedback?: string;
  Severity?: string;
  IsEstimatedRisk?: boolean;
};

type Summary = {
  totalMethods?: number;
  testedMethods?: number;
  untestedMethods?: number;
  unusedMethods?: number;
  highRiskCount?: number;
  mediumRiskCount?: number;
  lowRiskCount?: number;
  averageRiskScore?: number;
  averageCoverage?: number;
  testsRan?: boolean;
  coverageAvailable?: boolean;

  TotalMethods?: number;
  TestedMethods?: number;
  UntestedMethods?: number;
  UnusedMethods?: number;
  HighRiskCount?: number;
  MediumRiskCount?: number;
  LowRiskCount?: number;
  AverageRiskScore?: number;
  AverageCoverage?: number;
  TestsRan?: boolean;
  CoverageAvailable?: boolean;
};

type NormalizedMethod = {
  fullName: string;
  methodName: string;
  filePath: string;
  complexity: number;
  usageCount: number;
  coverageRate: number;
  status: string;
  riskScore: number;
  feedback: string;
  severity: string;
  isEstimatedRisk: boolean;
  category: string;
};

function normalizeMethod(m: Method): NormalizedMethod {
  const riskScore = m.riskScore ?? m.RiskScore ?? 0;
  const severity = m.severity ?? m.Severity ?? getCategory(riskScore);

  return {
    fullName: m.fullName ?? m.FullName ?? "",
    methodName: m.methodName ?? m.MethodName ?? "",
    filePath: m.filePath ?? m.FilePath ?? "",
    complexity: m.complexity ?? m.Complexity ?? 0,
    usageCount: m.usageCount ?? m.UsageCount ?? 0,
    coverageRate: m.coverageRate ?? m.CoverageRate ?? 0,
    status: m.status ?? m.Status ?? "",
    riskScore,
    feedback: m.feedback ?? m.Feedback ?? "",
    severity,
    isEstimatedRisk: m.isEstimatedRisk ?? m.IsEstimatedRisk ?? false,
    category: severity || getCategory(riskScore),
  };
}

function getCategory(risk: number) {
  if (risk >= 80) return "Extreme";
  if (risk >= 60) return "Critical";
  if (risk >= 40) return "High";
  if (risk >= 20) return "Medium";
  return "Low";
}

function getBadgeColor(cat: string) {
  if (cat === "Extreme") return "bg-red-700";
  if (cat === "Critical") return "bg-red-600";
  if (cat === "High") return "bg-orange-500";
  if (cat === "Medium") return "bg-yellow-400 text-gray-900";
  return "bg-green-500";
}

function getSummaryValue(summary: Summary | null, camel: keyof Summary, pascal: keyof Summary, fallback = 0) {
  return Number(summary?.[camel] ?? summary?.[pascal] ?? fallback);
}

function App() {
  const [projectPath, setProjectPath] = useState("");
  const [coveragePath, setCoveragePath] = useState("");
  const [data, setData] = useState<NormalizedMethod[]>([]);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [search, setSearch] = useState("");
  const [analyzed, setAnalyzed] = useState(false);

  const analyze = async () => {
    if (!projectPath.trim()) {
      setError("Lütfen analiz edilecek proje klasörü yolunu girin.");
      return;
    }

    setLoading(true);
    setError("");
    setData([]);
    setSummary(null);
    setAnalyzed(false);

    try {
      const res = await fetch("http://localhost:5003/api/analyze", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          projectPath: projectPath.trim(),
          testProjectPath: coveragePath.trim() || null,
        }),
      });

      if (!res.ok) {
        let message = "Analiz başarısız.";
        try {
          const err = await res.json();
          message = err.error || message;
        } catch {
          message = await res.text();
        }
        throw new Error(message);
      }

      const result = await res.json();

      const methodsRaw: Method[] = Array.isArray(result)
        ? result
        : result.methods ?? result.Methods ?? [];

      const summaryRaw: Summary | null = Array.isArray(result)
        ? null
        : result.summary ?? result.Summary ?? null;

      const methods = methodsRaw
        .map(normalizeMethod)
        .sort((a, b) => b.riskScore - a.riskScore);

      setData(methods);
      setSummary(summaryRaw);
      setAnalyzed(true);
    } catch (e: any) {
      setError(e.message || "Backend'e bağlanılamadı. dotnet run çalışıyor mu?");
    } finally {
      setLoading(false);
    }
  };

  const filtered = useMemo(() => {
    const term = search.toLowerCase();

    return data.filter((m) =>
      `${m.fullName} ${m.methodName} ${m.filePath} ${m.status}`
        .toLowerCase()
        .includes(term),
    );
  }, [data, search]);

  const chartData = data.slice(0, 10).map((m, i) => ({
    id: i,
    label: m.methodName,
    fullName: m.fullName,
    risk: m.riskScore,
  }));

  const totalMethods =
    getSummaryValue(summary, "totalMethods", "TotalMethods", data.length) || data.length;

  const highRisk =
    getSummaryValue(summary, "highRiskCount", "HighRiskCount") ||
    data.filter((m) => m.riskScore >= 60).length;

  const mediumRisk =
    getSummaryValue(summary, "mediumRiskCount", "MediumRiskCount") ||
    data.filter((m) => m.riskScore >= 30 && m.riskScore < 60).length;

  const lowRisk =
    getSummaryValue(summary, "lowRiskCount", "LowRiskCount") ||
    data.filter((m) => m.riskScore < 30).length;

  const averageRisk =
    getSummaryValue(
      summary,
      "averageRiskScore",
      "AverageRiskScore",
      data.length
        ? data.reduce((sum, m) => sum + m.riskScore, 0) / data.length
        : 0,
    );

  const averageCoverage =
    getSummaryValue(
      summary,
      "averageCoverage",
      "AverageCoverage",
      data.length
        ? data.reduce((sum, m) => sum + m.coverageRate, 0) / data.length
        : 0,
    );

  const coverageAvailable =
    Boolean(summary?.coverageAvailable ?? summary?.CoverageAvailable ?? coveragePath.trim());

  const CustomTooltip = ({ active, payload }: any) => {
    if (active && payload?.length) {
      const item = payload[0].payload;
      return (
        <div className="bg-white p-3 shadow-lg rounded-lg border text-sm max-w-xs">
          <p className="font-semibold break-words text-gray-800">{item.fullName}</p>
          <p className="text-red-500 mt-1">Risk: {item.risk.toFixed(2)}</p>
        </div>
      );
    }

    return null;
  };

  return (
    <div className="min-h-screen bg-gray-100 p-6">
      <h1 className="text-4xl font-bold text-center mb-2">🔥 RiskAwareQA</h1>
      <p className="text-center text-gray-500 mb-8">
        C# projeleri için usage, complexity ve coverage tabanlı risk analizi
      </p>

      <div className="max-w-4xl mx-auto bg-white rounded-2xl shadow p-6 mb-6">
        <h2 className="text-xl font-bold mb-4">📥 Veri Girişi</h2>

        <div className="grid grid-cols-1 gap-4">
          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">
              📁 Proje klasör yolu <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              className="w-full p-3 rounded-xl border border-gray-300 shadow-sm text-sm font-mono"
              placeholder="Örn: C:\SchoolProject\RiskAwareQA\backend"
              value={projectPath}
              onChange={(e) => setProjectPath(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && analyze()}
            />
          </div>

          <div>
            <label className="block text-sm font-semibold text-gray-700 mb-2">
              📄 Coverage / test project yolu <span className="text-gray-400">(opsiyonel)</span>
            </label>
            <input
              type="text"
              className="w-full p-3 rounded-xl border border-gray-300 shadow-sm text-sm font-mono"
              placeholder="Boş bırakılabilir. Örn: C:\SchoolProject\RiskAwareQA\backend.Tests"
              value={coveragePath}
              onChange={(e) => setCoveragePath(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && analyze()}
            />
            <p className="text-xs text-gray-500 mt-2">
              Coverage bilgisi verilmezse sistem sadece usage ve complexity üzerinden tahmini risk üretir.
            </p>
          </div>

          <button
            onClick={analyze}
            disabled={loading}
            className="w-full md:w-48 px-6 py-3 bg-blue-600 hover:bg-blue-700 text-white font-semibold rounded-xl shadow disabled:opacity-50 transition"
          >
            {loading ? "Yükleniyor..." : "Analiz Et"}
          </button>
        </div>

        {error && (
          <div className="mt-4 p-3 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
            ⚠️ {error}
          </div>
        )}
      </div>

      {loading && (
        <div className="max-w-4xl mx-auto bg-white rounded-2xl shadow p-8 text-center mb-6">
          <div className="text-2xl font-bold text-blue-600 mb-2">Yükleniyor...</div>
          <p className="text-gray-500">
            Kodlar analiz ediliyor, usage ve complexity hesaplanıyor.
          </p>
        </div>
      )}

      {analyzed && data.length > 0 && (
        <>
          <div className="max-w-6xl mx-auto mb-4">
            <div
              className={`p-4 rounded-2xl border text-sm ${
                coverageAvailable
                  ? "bg-green-50 border-green-200 text-green-700"
                  : "bg-yellow-50 border-yellow-200 text-yellow-800"
              }`}
            >
              {coverageAvailable
                ? "✅ Coverage bilgisi kullanılarak risk analizi yapıldı."
                : "⚠️ Coverage bilgisi olmadan analiz yapıldı. Riskler sadece usage ve complexity üzerinden tahmini hesaplandı."}
            </div>
          </div>

          <div className="max-w-6xl mx-auto grid grid-cols-2 md:grid-cols-6 gap-4 mb-6">
            <div className="bg-white rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-gray-800">{totalMethods}</div>
              <div className="text-sm text-gray-500 mt-1">Toplam Metot</div>
            </div>

            <div className="bg-red-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-red-600">{highRisk}</div>
              <div className="text-sm text-gray-500 mt-1">Yüksek Risk</div>
            </div>

            <div className="bg-yellow-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-yellow-600">{mediumRisk}</div>
              <div className="text-sm text-gray-500 mt-1">Orta Risk</div>
            </div>

            <div className="bg-green-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-green-600">{lowRisk}</div>
              <div className="text-sm text-gray-500 mt-1">Düşük Risk</div>
            </div>

            <div className="bg-white rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-gray-800">
                {averageRisk.toFixed(1)}
              </div>
              <div className="text-sm text-gray-500 mt-1">Ort. Risk</div>
            </div>

            <div className="bg-white rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-gray-800">
                {averageCoverage.toFixed(1)}%
              </div>
              <div className="text-sm text-gray-500 mt-1">Ort. Coverage</div>
            </div>
          </div>

          <div className="max-w-6xl mx-auto mb-6 bg-white p-6 rounded-2xl shadow">
            <h2 className="text-xl font-bold mb-4">📊 En Riskli 10 Metot</h2>
            <ResponsiveContainer width="100%" height={300}>
              <BarChart data={chartData}>
                <XAxis
                  dataKey="label"
                  interval={0}
                  angle={-15}
                  textAnchor="end"
                  height={70}
                />
                <YAxis domain={[0, 100]} />
                <Tooltip content={<CustomTooltip />} />
                <Bar dataKey="risk" fill="#ef4444" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>

          <div className="bg-white shadow-xl rounded-2xl p-6 max-w-6xl mx-auto overflow-x-auto">
            <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-3 mb-4">
              <h2 className="text-xl font-bold">📋 Risk Sıralaması</h2>
              <input
                type="text"
                placeholder="🔍 Metot, dosya veya durum ara..."
                className="p-2 px-4 rounded-xl border shadow-sm text-sm w-full md:w-80"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>

            <table className="w-full border-collapse text-sm min-w-[1100px]">
              <thead>
                <tr className="bg-gray-800 text-white">
                  <th className="p-3 text-left rounded-tl-lg">Metot</th>
                  <th className="p-3 text-left">Dosya</th>
                  <th className="p-3 text-center">Complexity</th>
                  <th className="p-3 text-center">Usage</th>
                  <th className="p-3 text-center">Coverage</th>
                  <th className="p-3 text-center">Durum</th>
                  <th className="p-3 text-center">Tip</th>
                  <th className="p-3 text-center rounded-tr-lg">Risk</th>
                </tr>
              </thead>

              <tbody>
                {filtered.map((m, i) => (
                  <tr key={`${m.fullName}-${i}`} className="border-b hover:bg-gray-50 transition">
                    <td className="p-3 text-gray-800 font-medium">
                      {m.methodName}
                      <div className="text-xs text-gray-400 font-normal break-all">
                        {m.fullName}
                      </div>
                    </td>

                    <td
                      className="p-3 text-gray-500 text-xs font-mono max-w-[220px] truncate"
                      title={m.filePath}
                    >
                      {m.filePath}
                    </td>

                    <td className="p-3 text-center font-semibold">{m.complexity}</td>
                    <td className="p-3 text-center">{m.usageCount}</td>
                    <td className="p-3 text-center">{m.coverageRate.toFixed(1)}%</td>

                    <td className="p-3 text-center text-xs text-gray-600">
                      {m.status}
                    </td>

                    <td className="p-3 text-center">
                      <span
                        className={`px-3 py-1 rounded-full text-xs font-semibold ${
                          m.isEstimatedRisk
                            ? "bg-yellow-100 text-yellow-800"
                            : "bg-green-100 text-green-700"
                        }`}
                      >
                        {m.isEstimatedRisk ? "Estimated" : "Verified"}
                      </span>
                    </td>

                    <td className="p-3 text-center">
                      <span
                        className={`text-white px-3 py-1 rounded-full text-xs font-semibold ${getBadgeColor(m.category)}`}
                      >
                        {m.category} ({m.riskScore.toFixed(0)})
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            {filtered.length === 0 && (
              <div className="text-center text-gray-500 p-8">
                Aramaya uygun metot bulunamadı.
              </div>
            )}
          </div>
        </>
      )}

      {analyzed && data.length === 0 && !loading && (
        <div className="max-w-xl mx-auto text-center text-gray-500 mt-12 bg-white rounded-2xl shadow p-8">
          Bu klasörde analiz edilecek metot bulunamadı.
        </div>
      )}
    </div>
  );
}

export default App;