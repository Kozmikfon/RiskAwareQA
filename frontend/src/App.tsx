import { useState } from "react";
import {
  Bar,
  BarChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

type Method = {
  FullName: string;
  MethodName: string;
  FilePath: string;
  Complexity: number;
  UsageCount: number;
  CoverageRate: number;
  Status: string;
  RiskScore: number;
};

type MethodWithCategory = Method & { category: string };

function getCategory(risk: number | undefined) {
  const r = risk ?? 0;
  if (r >= 75) return "Critical";
  if (r >= 50) return "High";
  if (r >= 25) return "Medium";
  return "Low";
}

function getBadgeColor(cat: string) {
  if (cat === "Critical") return "bg-red-600";
  if (cat === "High") return "bg-orange-500";
  if (cat === "Medium") return "bg-yellow-400 text-gray-800";
  return "bg-green-500";
}

function App() {
  const [projectPath, setProjectPath] = useState("");
  const [data, setData] = useState<MethodWithCategory[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [search, setSearch] = useState("");
  const [analyzed, setAnalyzed] = useState(false);

  const analyze = async () => {
    if (!projectPath.trim()) {
      setError("Lütfen bir klasör yolu girin.");
      return;
    }
    setLoading(true);
    setError("");
    setData([]);

    try {
      const res = await fetch(`/api/analyze`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ projectPath: projectPath.trim() }),
      });

      if (!res.ok) {
        const err = await res.json();
        throw new Error(err.error || "Analiz başarısız.");
      }

      const methods: Method[] = await res.json();
      const withCat = methods.map((m) => ({
        ...m,
        category: getCategory(m.RiskScore),
      }));
      setData(withCat);
      setAnalyzed(true);
    } catch (e: any) {
      setError(
        e.message || "Backend'e bağlanılamadı. dotnet run çalışıyor mu?",
      );
    } finally {
      setLoading(false);
    }
  };

  const filtered = data.filter((m) =>
    (m.FullName ?? "").toLowerCase().includes(search.toLowerCase()),
  );

  const chartData = data.slice(0, 10).map((m, i) => ({
    id: i,
    label: m.MethodName ?? "",
    fullName: m.FullName ?? "",
    risk: m.RiskScore ?? 0,
  }));

  const CustomTooltip = ({ active, payload }: any) => {
    if (active && payload?.length) {
      const item = payload[0].payload;
      return (
        <div className="bg-white p-3 shadow-lg rounded-lg border text-sm max-w-xs">
          <p className="font-semibold break-words text-gray-800">
            {item.fullName}
          </p>
          <p className="text-red-500 mt-1">Risk: {item.risk.toFixed(1)}</p>
        </div>
      );
    }
    return null;
  };

  const critical = data.filter((m) => m.category === "Critical").length;
  const high = data.filter((m) => m.category === "High").length;
  const safe = data.filter((m) => m.RiskScore === 0).length;

  return (
    <div className="min-h-screen bg-gray-100 p-6">
      <h1 className="text-4xl font-bold text-center mb-2">🔥 RiskAwareQA</h1>
      <p className="text-center text-gray-500 mb-8">
        Herhangi bir C# projesini seç → otomatik risk analizi
      </p>

      {/* PROJE SEÇİMİ */}
      <div className="max-w-3xl mx-auto bg-white rounded-2xl shadow p-6 mb-6">
        <label className="block text-sm font-semibold text-gray-700 mb-2">
          📁 Analiz edilecek proje klasörü
        </label>
        <div className="flex gap-3">
          <input
            type="text"
            className="flex-1 p-3 rounded-xl border border-gray-300 shadow-sm text-sm font-mono"
            placeholder="Örn: C:\Projects\BenimProjem  veya  /home/user/myapp"
            value={projectPath}
            onChange={(e) => setProjectPath(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && analyze()}
          />
          <button
            onClick={analyze}
            disabled={loading}
            className="px-6 py-3 bg-blue-600 hover:bg-blue-700 text-white font-semibold rounded-xl shadow disabled:opacity-50 transition"
          >
            {loading ? "Analiz ediliyor..." : "Analiz Et"}
          </button>
        </div>

        {error && (
          <div className="mt-3 p-3 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
            ⚠️ {error}
          </div>
        )}
      </div>

      {/* SONUÇLAR */}
      {analyzed && data.length > 0 && (
        <>
          {/* ÖZET KARTLAR */}
          <div className="max-w-6xl mx-auto grid grid-cols-4 gap-4 mb-6">
            <div className="bg-white rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-gray-800">
                {data.length}
              </div>
              <div className="text-sm text-gray-500 mt-1">Toplam Metot</div>
            </div>
            <div className="bg-red-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-red-600">{critical}</div>
              <div className="text-sm text-gray-500 mt-1">Kritik Risk</div>
            </div>
            <div className="bg-orange-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-orange-500">{high}</div>
              <div className="text-sm text-gray-500 mt-1">Yüksek Risk</div>
            </div>
            <div className="bg-green-50 rounded-2xl shadow p-5 text-center">
              <div className="text-3xl font-bold text-green-600">{safe}</div>
              <div className="text-sm text-gray-500 mt-1">Risksiz</div>
            </div>
          </div>

          {/* CHART */}
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
                <YAxis />
                <Tooltip content={<CustomTooltip />} />
                <Bar dataKey="risk" fill="#ef4444" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>

          {/* TABLO */}
          <div className="bg-white shadow-xl rounded-2xl p-6 max-w-6xl mx-auto">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-xl font-bold">📋 Metot Listesi</h2>
              <input
                type="text"
                placeholder="🔍 Metot ara..."
                className="p-2 px-4 rounded-xl border shadow-sm text-sm w-64"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>

            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="bg-gray-800 text-white">
                  <th className="p-3 text-left rounded-tl-lg">Metot</th>
                  <th className="p-3 text-left">Dosya</th>
                  <th className="p-3 text-center">Karmaşıklık</th>
                  <th className="p-3 text-center">Kullanım</th>
                  <th className="p-3 text-center">Kapsama</th>
                  <th className="p-3 text-center">Durum</th>
                  <th className="p-3 text-center rounded-tr-lg">Risk</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((m, i) => (
                  <tr key={i} className="border-b hover:bg-gray-50 transition">
                    <td className="p-3 text-gray-800 font-medium">
                      {m.MethodName}
                      <div className="text-xs text-gray-400 font-normal">
                        {m.FullName}
                      </div>
                    </td>
                    <td
                      className="p-3 text-gray-500 text-xs font-mono max-w-[180px] truncate"
                      title={m.FilePath}
                    >
                      {m.FilePath}
                    </td>
                    <td className="p-3 text-center font-semibold">
                      {m.Complexity}
                    </td>
                    <td className="p-3 text-center">{m.UsageCount}</td>
                    <td className="p-3 text-center">{m.CoverageRate}%</td>
                    <td className="p-3 text-center text-xs text-gray-600">
                      {m.Status}
                    </td>
                    <td className="p-3 text-center">
                      <span
                        className={`text-white px-3 py-1 rounded-full text-xs font-semibold ${getBadgeColor(m.category)}`}
                      >
                        {m.category} ({(m.RiskScore ?? 0).toFixed(0)})
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {analyzed && data.length === 0 && !loading && (
        <div className="max-w-xl mx-auto text-center text-gray-500 mt-12">
          Bu klasörde analiz edilecek metot bulunamadı.
        </div>
      )}
    </div>
  );
}

export default App;
