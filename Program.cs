// Imports for Persian calendar
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

class Candle
{
    public DateTime Time { get; set; }
    public double Close { get; set; }
}

class Backtester
{
    // Configuration
    public string Symbol = "USDTIRT";
    public string HourlyResolution = "60";
    public string MinuteResolution = "1";
    public int SeedPeriods = 21;

    // EMA calculation seeded by SMA
    public static List<double> EMA(List<double> prices, int period)
    {
        if (prices.Count < period)
            throw new ArgumentException($"Need at least {period} prices to seed EMA.");
        var ema = new List<double>(new double[prices.Count]);
        double seed = prices.Take(period).Average();
        for (int i = 0; i < period; i++) ema[i] = seed;
        double k = 2.0 / (period + 1);
        for (int i = period; i < prices.Count; i++)
            ema[i] = (prices[i] - ema[i - 1]) * k + ema[i - 1];
        return ema;
    }

    // EMA update for each tick
    public static double CalculateEmaFromBase(double baseEma, double price, int period) =>
        (price - baseEma) * (2.0 / (period + 1)) + baseEma;

    // Check for EMA crossover
    public static string CheckCross(double prevFast, double prevSlow, double curFast, double curSlow)
    {
        if (prevFast <= prevSlow && curFast > curSlow) return "BUY";
        if (prevFast >= prevSlow && curFast < curSlow) return "SELL";
        return "NONE";
    }

    public async Task RunAsync(DateTime startShamsi)
    {
        var persian = new PersianCalendar();
        var tehran = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
        var startLocal = persian.ToDateTime(startShamsi.Year, startShamsi.Month, startShamsi.Day,
                                           startShamsi.Hour, startShamsi.Minute, 0, 0);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tehran);
        startUtc = new DateTime(startUtc.Year, startUtc.Month, startUtc.Day,
                                startUtc.Hour, 0, 0, DateTimeKind.Utc);
        var nowUtc = DateTime.UtcNow;

        // Fetch data
        var seedUtc = startUtc.AddHours(-SeedPeriods);
        var hourly = await GetCandlesAsync(Symbol, HourlyResolution, seedUtc, nowUtc);
        var hourlyCloses = hourly.Select(c => c.Close).ToList();
        var emaFastH = EMA(hourlyCloses, 9);
        var emaSlowH = EMA(hourlyCloses, 21);

        var minutes = await GetCandlesAsync(Symbol, MinuteResolution, seedUtc, nowUtc);
        var minuteCloses = minutes.Select(c => c.Close).ToList();
        var emaFastM = EMA(minuteCloses, 9);
        var emaSlowM = EMA(minuteCloses, 21);

        int startIdxH = hourly.FindIndex(c => c.Time >= startUtc);
        Console.WriteLine("Time (Shamsi)   | Hybrid   | HBuy/Hsell Prices | Hourly  | HrPrice | PM Buy/Sell | PM Prices");
        Console.WriteLine("---------------|----------|-------------------|---------|---------|-------------|-----------");

        for (int h = startIdxH; h < hourly.Count - 1; h++)
        {
            var hrStart = hourly[h].Time;
            var hrEnd = hourly[h + 1].Time;

            // Hybrid approach
            double baseF = emaFastH[h], baseS = emaSlowH[h];
            var seg = minutes.Where(m => m.Time >= hrStart && m.Time <= hrEnd).ToList();
            int hBuy = 0, hSell = 0;
            double minBuyH = double.MaxValue, maxSellH = double.MinValue;
            string firstH = "NONE";
            foreach (var m in seg)
            {
                double cf = CalculateEmaFromBase(baseF, m.Close, 9);
                double cs = CalculateEmaFromBase(baseS, m.Close, 21);
                var s = CheckCross(baseF, baseS, cf, cs);
                if (s != "NONE")
                {
                    if (s == "BUY") { hBuy++; minBuyH = Math.Min(minBuyH, m.Close); }
                    else          { hSell++; maxSellH = Math.Max(maxSellH, m.Close); }
                    if (hBuy + hSell == 1) firstH = s;
                }
            }
            var hybridPrices = (hBuy > 0 ? $"B:{minBuyH:F2}" : "B:-") + "/" + (hSell > 0 ? $"S:{maxSellH:F2}" : "S:-");

            // Hourly approach
            string hrSig = CheckCross(emaFastH[h], emaSlowH[h], emaFastH[h + 1], emaSlowH[h + 1]);
            double hrPrice = hourly[h].Close;

            // Pure minute approach
            int idxS = minutes.FindIndex(m => m.Time >= hrStart);
            int idxE = minutes.FindLastIndex(m => m.Time <= hrEnd);
            int pB = 0, pS = 0;
            double minPB = double.MaxValue, maxPS = double.MinValue;
            List<double> pmPrices = new List<double>();
            if (idxS >= 0 && idxE > idxS)
            {
                for (int i = idxS + 1; i <= idxE; i++)
                {
                    var ps = CheckCross(emaFastM[i - 1], emaSlowM[i - 1], emaFastM[i], emaSlowM[i]);
                    if (ps == "BUY") { pB++; minPB = Math.Min(minPB, minutes[i].Close); pmPrices.Add(minutes[i].Close); }
                    if (ps == "SELL"){ pS++; maxPS = Math.Max(maxPS, minutes[i].Close); pmPrices.Add(minutes[i].Close); }
                }
            }
            var pmCount = $"B:{pB}/S:{pS}";
            var pmExt    = (pB>0? $"MinB:{minPB:F2}" : "MinB:-") + "/" + (pS>0? $"MaxS:{maxPS:F2}" : "MaxS:-");

            // Timestamp
            var lt = TimeZoneInfo.ConvertTimeFromUtc(hrStart, tehran);
            string ts = $"{persian.GetYear(lt):0000}/{persian.GetMonth(lt):00}/{persian.GetDayOfMonth(lt):00} {lt.Hour:00}:00";

            Console.WriteLine($"{ts} | {firstH,-8} | {hybridPrices,-17} | {hrSig,-7} | {hrPrice,7:F2} | {pmCount,-11} | {pmExt}");
        }

        Console.WriteLine("✅ Done.");
    }

    private async Task<List<Candle>> GetCandlesAsync(string symbol, string resolution, DateTime from, DateTime to)
    {
        using var client = new HttpClient();
        long f = new DateTimeOffset(from).ToUnixTimeSeconds();
        long t = new DateTimeOffset(to).ToUnixTimeSeconds();
        string url = $"https://api.nobitex.ir/market/udf/history?symbol={symbol}&resolution={resolution}&from={f}&to={t}";
        var resp = await client.GetStringAsync(url);
        var json = JObject.Parse(resp);
        if (json["s"]?.ToString() != "ok") throw new Exception("API failed");
        var ts = json["t"].ToObject<List<long>>();
        var cs = json["c"].ToObject<List<double>>();
        return ts.Select((tt, i) => new Candle { Time = DateTimeOffset.FromUnixTimeSeconds(tt).UtcDateTime, Close = cs[i] }).ToList();
    }
}

class Program
{
    static async Task Main()
    {
        var start = new DateTime(1404, 2, 29, 0, 0, 0);
        await new Backtester().RunAsync(start);
    }
}