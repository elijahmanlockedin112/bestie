// Claude.cs - Bestie's side of the owner's Claude credit: the key, and the spend ledger every Claude call goes
// through. C# 5 only.
//  - The key: ANTHROPIC_API_KEY from the environment, else read at run time from Synaptic's .env (the same key
//    Synaptic and Spark Lab use). It is never logged or written anywhere.
//  - The ledger: one JSON line per call in C:\Users\elija\Synaptic\anthropic-spend.jsonl (ANTHROPIC_SPEND_LEDGER
//    overrides), the same append-only file and row shape as Synaptic's anthropic-client.ts and Spark Lab's
//    claude-ledger.mjs, so Synaptic's /claude-usage.html shows Bestie's spend beside theirs. Failed and refused calls
//    are recorded too. No call is made unless the ledger can be written, and none once this month's ledger total
//    (every app) has reached the monthly cap. The owner: "we need to know every penny that is spent."
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Bestie
{
    internal static class ClaudeKey
    {
        static readonly object gate = new object();
        static string cached = "";
        static string source = "";
        static DateTime readUtc = DateTime.MinValue;

        /// <summary>Synaptic's .env (BESTIE_CLAUDE_ENV overrides).</summary>
        public static string EnvFilePath
        {
            get
            {
                string p = null;
                try { p = Environment.GetEnvironmentVariable("BESTIE_CLAUDE_ENV"); } catch { }
                if (!string.IsNullOrEmpty(p)) return p;
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, @"Downloads\Coding Projects\learning-hub\apps\synaptic\.env");
            }
        }

        /// <summary>The key, or "" if there is none. Re-read at most once a minute (so a key added to the .env is picked up).</summary>
        public static string Get()
        {
            lock (gate)
            {
                if ((DateTime.UtcNow - readUtc).TotalSeconds < 60) return cached;
                readUtc = DateTime.UtcNow;
                string k = null, src = "";
                try
                {
                    k = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                    if (string.IsNullOrEmpty(k)) k = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
                    if (!string.IsNullOrEmpty(k)) src = "env";
                }
                catch { }
                if (string.IsNullOrEmpty(k))
                {
                    k = FromEnvFile(EnvFilePath, "ANTHROPIC_API_KEY");
                    if (!string.IsNullOrEmpty(k)) src = "synaptic .env";
                }
                string fresh = AssistantSettings.CleanKey(k);
                if (fresh.Length > 0 != cached.Length > 0 || src != source)
                    DebugLog.Write("claude: key " + (fresh.Length > 0 ? "found (" + src + ")" : "not found (set ANTHROPIC_API_KEY in " + EnvFilePath + ")"));
                cached = fresh;
                source = src;
                return cached;
            }
        }

        public static bool Has { get { return Get().Length > 0; } }

        static string FromEnvFile(string path, string name)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (line.StartsWith("export ")) line = line.Substring(7).Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line.Substring(0, eq).Trim() != name) continue;
                    string v = line.Substring(eq + 1).Trim();
                    if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0]) v = v.Substring(1, v.Length - 2);
                    else { int hash = v.IndexOf(" #"); if (hash >= 0) v = v.Substring(0, hash).Trim(); }
                    return v;
                }
            }
            catch (Exception ex) { DebugLog.Write("claude: couldn't read " + path + " (" + ex.GetType().Name + ")"); }
            return null;
        }
    }

    internal static class ClaudeLedger
    {
        public const string PriceTableDate = "2026-10-30";
        /// <summary>"bestie" (ANTHROPIC_SPEND_PROCESS overrides, e.g. for a test run).</summary>
        public static string ProcessName
        {
            get
            {
                string p = null;
                try { p = Environment.GetEnvironmentVariable("ANTHROPIC_SPEND_PROCESS"); } catch { }
                return string.IsNullOrEmpty(p) ? "bestie" : p;
            }
        }
        const double DefaultMonthlyCapUsd = 100;   // the owner's Claude credit a month (ANTHROPIC_MONTHLY_CAP_USD overrides)

        sealed class Price
        {
            public double Input, CachedInput, CacheWrite, Output;
            public long LongOver; public Price Long;
        }

        // $ per million tokens, the same table as Synaptic and Spark Lab (dated above). Cache writes (5-minute) are 1.25x
        // input. Haiku 5.5 bills a prompt over 100k tokens at its long-context rates.
        static readonly Dictionary<string, Price> Prices = new Dictionary<string, Price>
        {
            { "claude-opus-5-5", new Price { Input = 4.0, CachedInput = 0.2, CacheWrite = 5.0, Output = 20.0 } },
            { "claude-sonnet-5-5", new Price { Input = 2.0, CachedInput = 0.2, CacheWrite = 2.5, Output = 10.0 } },
            { "claude-haiku-5-5", new Price { Input = 0.1, CachedInput = 0.01, CacheWrite = 0.125, Output = 0.5,
                LongOver = 100000, Long = new Price { Input = 0.5, CachedInput = 0.05, CacheWrite = 0.625, Output = 2.5 } } },
        };

        static readonly object gate = new object();
        static string monthKey;
        static long readTo;
        static double monthTotal;

        public static string FilePath
        {
            get
            {
                string p = null;
                try { p = Environment.GetEnvironmentVariable("ANTHROPIC_SPEND_LEDGER"); } catch { }
                if (!string.IsNullOrEmpty(p)) return p;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Synaptic\anthropic-spend.jsonl");
            }
        }

        public static double MonthlyCapUsd
        {
            get
            {
                double v;
                string e = null;
                try { e = Environment.GetEnvironmentVariable("ANTHROPIC_MONTHLY_CAP_USD"); } catch { }
                return !string.IsNullOrEmpty(e) && double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 ? v : DefaultMonthlyCapUsd;
            }
        }

        public static bool Known(string model) { return model != null && Prices.ContainsKey(model); }

        public static double CostOf(string model, long input, long cacheWrite, long cacheRead, long output)
        {
            Price p;
            if (model == null || !Prices.TryGetValue(model, out p)) return 0;   // an unknown model reads $0: add it above
            if (p.Long != null && input + cacheWrite + cacheRead > p.LongOver) p = p.Long;
            return (input * p.Input + cacheWrite * p.CacheWrite + cacheRead * p.CachedInput + output * p.Output) / 1000000.0;
        }

        /// <summary>Throws (in Bestie's voice) unless the ledger can be written and the month's total plus this call's
        /// likely-high cost stays under the cap.</summary>
        public static void BeforeCall(string model, int maxTokens, int inputChars)
        {
            string path = FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            }
            catch (Exception ex)
            {
                var e = new OpenAIException("i keep a notebook of every claude penny, and i can't write in it right now, so i'm not spending any.",
                    "claude: ledger not writable (" + path + ": " + ex.Message + ")");
                e.Status = 403;
                throw e;
            }
            Price p;
            double estimate = 0;
            if (Prices.TryGetValue(model ?? "", out p))
                estimate = ((inputChars / 3.0) * p.Input + Math.Min(maxTokens, 20000) * p.Output) / 1000000.0;
            double spent = MonthSpent(), cap = MonthlyCapUsd;
            if (spent + estimate > cap)
            {
                var e = new OpenAIException("this month's claude credit is used up ($" + spent.ToString("0.00", CultureInfo.InvariantCulture) + " of $"
                    + cap.ToString("0", CultureInfo.InvariantCulture) + "). i'll be back on the 1st.",
                    "claude: monthly cap reached ($" + spent.ToString("0.0000", CultureInfo.InvariantCulture) + " spent, this call up to $" + estimate.ToString("0.0000", CultureInfo.InvariantCulture) + ")");
                e.Status = 402;
                e.Code = "credit_cap";
                throw e;
            }
        }

        /// <summary>One row for one call. Never throws (a row that can't be written is logged loudly).</summary>
        public static void Record(string feature, string model, string effort, bool ok, string error, string requestId,
            long input, long output, long cacheWrite, long cacheRead)
        {
            var row = new Dictionary<string, object>();
            row["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            row["process"] = ProcessName;
            row["feature"] = string.IsNullOrEmpty(feature) ? "assistant" : feature;
            row["model"] = model ?? "";
            row["effort"] = effort ?? "";
            row["priceTable"] = PriceTableDate;
            row["ok"] = ok;
            if (!string.IsNullOrEmpty(error)) row["error"] = error.Length > 300 ? error.Substring(0, 300) : error;
            if (!string.IsNullOrEmpty(requestId)) row["requestId"] = requestId;
            row["inputTokens"] = input;
            row["outputTokens"] = output;
            row["cacheWriteTokens"] = cacheWrite;
            row["cacheReadTokens"] = cacheRead;
            row["costUsd"] = Math.Round(CostOf(model, input, cacheWrite, cacheRead, output), 6);
            byte[] line = Encoding.UTF8.GetBytes(Json.Write(row) + "\n");
            try
            {
                lock (gate)
                {
                    using (var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(line, 0, line.Length);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("claude: A CALL RAN BUT ITS SPEND COULDN'T BE RECORDED in " + FilePath + ": " + ex.Message + " / " + Encoding.UTF8.GetString(line).Trim());
            }
        }

        /// <summary>This calendar month's (UTC) total in the ledger, every app's rows. Reads only what was appended since last time.</summary>
        public static double MonthSpent()
        {
            string key = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            lock (gate)
            {
                try
                {
                    string path = FilePath;
                    if (!File.Exists(path)) return 0;
                    if (key != monthKey) { monthKey = key; readTo = 0; monthTotal = 0; }
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < readTo) { readTo = 0; monthTotal = 0; }   // the file was replaced: start over
                        fs.Position = readTo;
                        var buf = new byte[fs.Length - readTo];
                        int got = 0;
                        while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
                        if (got == 0) return monthTotal;
                        int lastNl = Array.LastIndexOf(buf, (byte)'\n', got - 1);
                        if (lastNl < 0) return monthTotal;
                        string text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
                        readTo += lastNl + 1;   // a half-written last line is read next time
                        foreach (string l in text.Split('\n'))
                        {
                            if (l.Length < 10 || l.IndexOf("\"ts\":\"" + key, StringComparison.Ordinal) < 0) continue;
                            Dictionary<string, object> r = null;
                            try { r = Json.Obj(l); } catch { }
                            if (r != null) monthTotal += Json.Num(r, "costUsd", 0);
                        }
                    }
                }
                catch (Exception ex) { DebugLog.Write("claude: couldn't read the ledger total: " + ex.Message); }
                return monthTotal;
            }
        }
    }
}
