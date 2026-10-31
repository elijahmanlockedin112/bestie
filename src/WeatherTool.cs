// WeatherTool.cs - the weather tool: Open-Meteo (free, no key; geocoding + forecast), so weather questions get real
// numbers instead of the assistant scraping a text service with a shell command (wttr.in answered empty twice on
// Oct 30). The answer names an icon per day that the chat's weather card draws. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        static Dictionary<string, object> WeatherDef()
        {
            return Tool("weather", "current weather and the next days' forecast for a place (temperature, feels like, sky, rain chance, wind, humidity). "
                + "use it for any weather question; use the place they live (from memory) unless they name another.", R("place"),
                P("place", "string", "city, optionally with state or country, e.g. 'Valdosta, Georgia'"),
                P("days", "integer", "how many days ahead, 1-7 (default 4)"));
        }

        AssistantToolResult WeatherTool(Dictionary<string, object> a)
        {
            string place = (Json.Str(a, "place") ?? "").Trim();
            if (place.Length == 0) return new AssistantToolResult("no place given: ask where, or use where they live from memory.");
            int days = Math.Max(1, Math.Min(7, (int)Json.Num(a, "days", 4)));
            try
            {
                // the town first ("Valdosta, Georgia" -> search "Valdosta", then prefer the result in Georgia)
                string town = place.Split(',')[0].Trim(), region = place.Contains(",") ? place.Substring(place.IndexOf(',') + 1).Trim().ToLowerInvariant() : "";
                Dictionary<string, object> geo = GetJson("https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name=" + Uri.EscapeDataString(town));
                Dictionary<string, object> hit = null;
                foreach (object o in Json.Arr(geo, "results"))
                {
                    var r = o as Dictionary<string, object>;
                    if (r == null) continue;
                    if (hit == null) hit = r;
                    string where = ((Json.Str(r, "admin1") ?? "") + " " + (Json.Str(r, "country") ?? "") + " " + (Json.Str(r, "country_code") ?? "")).ToLowerInvariant();
                    if (region.Length > 0 && where.Contains(region)) { hit = r; break; }
                }
                if (hit == null) return new AssistantToolResult("couldn't find a place called \"" + place + "\".");
                double lat = Json.Num(hit, "latitude", 0), lon = Json.Num(hit, "longitude", 0);
                string name = Json.Str(hit, "name") + (Json.Str(hit, "admin1") != null ? ", " + Json.Str(hit, "admin1") : "");
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&timezone=auto&forecast_days={2}&temperature_unit=fahrenheit&wind_speed_unit=mph" +
                    "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,is_day,precipitation" +
                    "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset", lat, lon, days + 1);
                Dictionary<string, object> f = GetJson(url);
                Dictionary<string, object> cur = Json.Obj(f, "current");
                Dictionary<string, object> day = Json.Obj(f, "daily");
                var sb = new StringBuilder();
                bool isDay = Json.Num(cur, "is_day", 1) > 0.5;
                int code = (int)Json.Num(cur, "weather_code", 0);
                DateTime now;
                string time = Json.Str(cur, "time") ?? "";
                sb.Append("weather for ").Append(name).Append(" (open-meteo)");
                if (DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out now)) sb.Append(", ").Append(now.ToString("ddd h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant());
                sb.Append(":\nnow: ").Append(Deg(Json.Num(cur, "temperature_2m", 0))).Append(", feels ").Append(Deg(Json.Num(cur, "apparent_temperature", 0)))
                  .Append(", ").Append(Sky(code)).Append(" (icon ").Append(Icon(code, isDay)).Append("), humidity ").Append(Math.Round(Json.Num(cur, "relative_humidity_2m", 0))).Append("%")
                  .Append(", wind ").Append(Math.Round(Json.Num(cur, "wind_speed_10m", 0))).Append(" mph ").Append(Compass(Json.Num(cur, "wind_direction_10m", 0))).Append('\n');
                object[] dates = Json.Arr(day, "time"), codes = Json.Arr(day, "weather_code"), hi = Json.Arr(day, "temperature_2m_max"), lo = Json.Arr(day, "temperature_2m_min"), rain = Json.Arr(day, "precipitation_probability_max");
                for (int i = 0; i < dates.Length && i <= days; i++)
                {
                    DateTime d;
                    string label = DateTime.TryParse(Convert.ToString(dates[i], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                        ? (i == 0 ? "today" : d.ToString("ddd", CultureInfo.InvariantCulture).ToLowerInvariant()) : "day " + i;
                    int c = (int)Num(codes, i);
                    sb.Append(label).Append(": ").Append(Deg(Num(hi, i))).Append(" / ").Append(Deg(Num(lo, i))).Append(", ").Append(Sky(c)).Append(" (icon ").Append(Icon(c, true)).Append(")");
                    if (i < rain.Length && rain[i] != null) sb.Append(", ").Append(Math.Round(Num(rain, i))).Append("% rain");
                    sb.Append('\n');
                }
                sb.Append("(show it as a card: a stat for now, tiles for the days; mark the day that matters, e.g. the rainy one.)");
                return new AssistantToolResult(sb.ToString());
            }
            catch (WebException ex) { return new AssistantToolResult("the weather service didn't answer (" + ex.Status + "). say you couldn't get it right now."); }
            catch (Exception ex)
            {
                DebugLog.Write("weather: " + ex.GetType().Name + ": " + ex.Message);
                return new AssistantToolResult("the weather service sent something unreadable. say you couldn't get it right now.");
            }
        }

        static Dictionary<string, object> GetJson(string url)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 12000;
            req.ReadWriteTimeout = 12000;
            req.UserAgent = "Bestie/1.0";
            // gzip only: .NET Framework's deflate reader rejects open-meteo's (zlib-wrapped) deflate ("block length does not match")
            req.AutomaticDecompression = DecompressionMethods.GZip;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return Json.Obj(r.ReadToEnd()) ?? new Dictionary<string, object>();
        }

        static double Num(object[] arr, int i)
        {
            if (arr == null || i >= arr.Length || arr[i] == null) return 0;
            try { return Convert.ToDouble(arr[i], CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static string Deg(double f) { return Math.Round(f).ToString(CultureInfo.InvariantCulture) + "°F"; }

        static string Compass(double deg)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[(int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8];
        }

        /// <summary>WMO weather codes, in words.</summary>
        static string Sky(int c)
        {
            if (c == 0) return "clear";
            if (c == 1) return "mostly clear";
            if (c == 2) return "partly cloudy";
            if (c == 3) return "overcast";
            if (c == 45 || c == 48) return "fog";
            if (c >= 51 && c <= 57) return "drizzle";
            if (c >= 61 && c <= 67) return c >= 65 ? "heavy rain" : "rain";
            if (c >= 71 && c <= 77) return "snow";
            if (c >= 80 && c <= 82) return "showers";
            if (c == 85 || c == 86) return "snow showers";
            if (c >= 95) return "thunderstorms";
            return "mixed";
        }

        /// <summary>The chat card's icon for a WMO code (ChatCards.DrawGlyph).</summary>
        static string Icon(int c, bool day)
        {
            if (c <= 1) return day ? "sun" : "moon";
            if (c <= 48) return "cloud";
            if ((c >= 51 && c <= 67) || (c >= 80 && c <= 82)) return "rain";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "snow";
            if (c >= 95) return "storm";
            return "cloud";
        }
    }
}
