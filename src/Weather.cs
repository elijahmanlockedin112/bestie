// Weather.cs - real weather for the radio (and anything else): where they are from Windows Location (asked on this computer,
// not a web service), conditions from Open-Meteo, and active watches/warnings from the US National Weather Service. No keys.
// Cached for a few minutes; any thread. C# 5 only.
using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal sealed class WeatherAlert
    {
        public string Id, Event, Headline, Severity, Area;
        public DateTime Expires;
        /// <summary>Warnings mean it's happening or about to (tornado / severe thunderstorm / flash flood warning): interrupt.</summary>
        public bool IsWarning { get { return (Event ?? "").EndsWith("Warning", StringComparison.OrdinalIgnoreCase); } }
    }

    internal sealed class WeatherReport
    {
        public double TempF, FeelsF, WindMph, HighF, LowF;
        public int Code, RainChanceSoon;
        public string Sky;
        public List<WeatherAlert> Alerts = new List<WeatherAlert>();
        public DateTime At;

        /// <summary>One line for a script: "70°F, heavy rain, wind 18 mph; high 74, low 63; 90% chance of rain in the next 3 hours. ALERTS: ...".</summary>
        public string Line()
        {
            var sb = new StringBuilder();
            sb.Append((int)Math.Round(TempF)).Append("°F");
            if (Math.Abs(FeelsF - TempF) >= 4) sb.Append(" (feels like ").Append((int)Math.Round(FeelsF)).Append(')');
            sb.Append(", ").Append(Sky).Append(", wind ").Append((int)Math.Round(WindMph)).Append(" mph");
            sb.Append("; today's high ").Append((int)Math.Round(HighF)).Append(", low ").Append((int)Math.Round(LowF));
            sb.Append("; ").Append(RainChanceSoon).Append("% chance of rain in the next 3 hours");
            if (Alerts.Count > 0)
                sb.Append(". ACTIVE NWS ALERTS: ").Append(string.Join("; ", Alerts.Select(a => a.Event + " until " + a.Expires.ToString("h:mm tt ddd", CultureInfo.InvariantCulture).ToLowerInvariant()).ToArray()));
            return sb.ToString();
        }
    }

    internal static class Weather
    {
        static readonly object gate = new object();
        static double lat = double.NaN, lon = double.NaN;
        static DateTime locatedAt = DateTime.MinValue;
        static WeatherReport last;

        static Weather() { try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { } }

        /// <summary>Where this computer is, from Windows Location (re-asked every few hours). False if location is off.</summary>
        public static bool Locate(out double latitude, out double longitude)
        {
            lock (gate)
            {
                if (!double.IsNaN(lat) && (DateTime.UtcNow - locatedAt).TotalHours < 4) { latitude = lat; longitude = lon; return true; }
            }
            latitude = longitude = double.NaN;
            try
            {
                using (var w = new GeoCoordinateWatcher(GeoPositionAccuracy.Default))
                {
                    if (!w.TryStart(false, TimeSpan.FromSeconds(10))) return false;
                    var until = DateTime.UtcNow.AddSeconds(12);
                    while (DateTime.UtcNow < until && (w.Status != GeoPositionStatus.Ready || w.Position.Location.IsUnknown)) Thread.Sleep(250);
                    GeoCoordinate c = w.Position.Location;
                    w.Stop();
                    if (c.IsUnknown) return false;
                    lock (gate) { lat = Math.Round(c.Latitude, 2); lon = Math.Round(c.Longitude, 2); locatedAt = DateTime.UtcNow; latitude = lat; longitude = lon; }
                    return true;
                }
            }
            catch (Exception ex) { DebugLog.Write("weather: location: " + ex.Message); return false; }
        }

        /// <summary>Current conditions and alerts (cached ~8 minutes). Null if location is off or the services don't answer.</summary>
        public static WeatherReport Now()
        {
            lock (gate) if (last != null && (DateTime.UtcNow - last.At).TotalMinutes < 8) return last;
            double la, lo;
            if (!Locate(out la, out lo)) return null;
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m"
                    + "&hourly=precipitation_probability&daily=temperature_2m_max,temperature_2m_min&temperature_unit=fahrenheit&wind_speed_unit=mph&timezone=auto&forecast_days=1", la, lo);
                var d = Json.Obj(Get(url, null));
                var cur = Json.Obj(d, "current");
                var r = new WeatherReport
                {
                    TempF = Json.Num(cur, "temperature_2m", 0), FeelsF = Json.Num(cur, "apparent_temperature", 0), WindMph = Json.Num(cur, "wind_speed_10m", 0),
                    Code = (int)Json.Num(cur, "weather_code", 0), At = DateTime.UtcNow,
                };
                r.Sky = Sky(r.Code);
                var daily = Json.Obj(d, "daily");
                r.HighF = Json.Arr(daily, "temperature_2m_max").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).FirstOrDefault();
                r.LowF = Json.Arr(daily, "temperature_2m_min").Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).FirstOrDefault();
                var hourly = Json.Obj(d, "hourly");
                var times = Json.Arr(hourly, "time").Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
                var probs = Json.Arr(hourly, "precipitation_probability").Select(x => x == null ? 0 : Convert.ToInt32(x, CultureInfo.InvariantCulture)).ToList();
                int h = DateTime.Now.Hour;
                r.RainChanceSoon = Enumerable.Range(h, 3).Where(i => i < probs.Count).Select(i => probs[i]).DefaultIfEmpty(0).Max();
                r.Alerts = Alerts(la, lo);
                lock (gate) last = r;
                return r;
            }
            catch (Exception ex) { DebugLog.Write("weather: " + ex.Message); return null; }
        }

        /// <summary>Active NWS watches and warnings for this spot (US only; empty elsewhere or on error). Not cached.</summary>
        public static List<WeatherAlert> Alerts(double la, double lo)
        {
            var list = new List<WeatherAlert>();
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture, "https://api.weather.gov/alerts/active?point={0},{1}", la, lo);
                foreach (var f in Json.Arr(Json.Obj(Get(url, "application/geo+json")), "features").OfType<Dictionary<string, object>>())
                {
                    var p = Json.Obj(f, "properties");
                    DateTime exp;
                    if (!DateTime.TryParse(Json.Str(p, "expires") ?? Json.Str(p, "ends") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out exp)) exp = DateTime.Now.AddHours(1);
                    list.Add(new WeatherAlert
                    {
                        Id = Json.Str(p, "id") ?? Json.Str(f, "id"), Event = Json.Str(p, "event") ?? "alert", Headline = Json.Str(p, "headline") ?? "",
                        Severity = Json.Str(p, "severity") ?? "", Area = Json.Str(p, "areaDesc") ?? "", Expires = exp.ToLocalTime(),
                    });
                }
            }
            catch (Exception ex) { DebugLog.Write("weather: alerts: " + ex.Message); }
            return list;
        }

        public static List<WeatherAlert> AlertsHere()
        {
            double la, lo;
            return Locate(out la, out lo) ? Alerts(la, lo) : new List<WeatherAlert>();
        }

        static string Get(string url, string accept)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            req.UserAgent = "(Bestie desktop assistant, personal use)";   // the weather service asks every app to say who it is
            if (accept != null) req.Accept = accept;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        /// <summary>WMO weather codes in words.</summary>
        public static string Sky(int code)
        {
            if (code == 0) return "clear";
            if (code <= 2) return "partly cloudy";
            if (code == 3) return "overcast";
            if (code == 45 || code == 48) return "foggy";
            if (code >= 51 && code <= 57) return "drizzle";
            if (code == 61 || code == 80) return "light rain";
            if (code == 63 || code == 81) return "rain";
            if (code == 65 || code == 82) return "heavy rain";
            if (code == 66 || code == 67) return "freezing rain";
            if (code >= 71 && code <= 77) return "snow";
            if (code == 85 || code == 86) return "snow showers";
            if (code == 95) return "thunderstorms";
            if (code >= 96) return "thunderstorms with hail";
            return "mixed weather";
        }
    }
}
