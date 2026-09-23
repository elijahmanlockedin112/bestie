using System;
using System.Linq;

namespace Bestie
{
    public class Song
    {
        public string Artist, Title, Source;
        public string Key { get { return (Artist + " - " + Title).ToLowerInvariant(); } }
        public override string ToString() { return Title + " by " + Artist; }
    }

    /// <summary>Figures out what's playing without any API: Spotify's desktop window title is "Artist - Song"
    /// while it plays (and just "Spotify ..." when paused). Also checks an audible open.spotify.com tab.</summary>
    public static class Music
    {
        public static Song Detect()
        {
            Song found = null;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                // Spotify keeps its (possibly hidden) main window titled with the current song
                string title = Native.TitleOf(h);
                if (title.Length < 4 || title.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase)) return true;
                int dash = title.IndexOf(" - ", StringComparison.Ordinal);
                if (dash <= 0) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (Native.ProcessNameOf(pid) != "spotify") return true;
                found = new Song { Artist = title.Substring(0, dash).Trim(), Title = title.Substring(dash + 3).Trim(), Source = "spotify" };
                return false;
            }, IntPtr.Zero);
            if (found != null) return found;

            var bridge = BrowserBridge.Instance;
            if (bridge == null || !bridge.Connected) return null;
            foreach (var t in bridge.GetTabs())
            {
                if (!t.Audible || t.Url == null || t.Url.IndexOf("open.spotify.com", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // web player: "Song • Artist"
                string title = t.Title ?? "";
                int dot = title.IndexOf(" • ", StringComparison.Ordinal);
                if (dot > 0) return new Song { Title = title.Substring(0, dot).Trim(), Artist = title.Substring(dot + 3).Trim(), Source = "spotify web" };
            }
            return null;
        }

        /// <summary>A stable, completely arbitrary rating out of 7 (it has opinions and sticks to them).</summary>
        public static int Rating(Song s)
        {
            int h = 17;
            foreach (char ch in s.Key) h = h * 31 + ch;
            return 3 + Math.Abs(h % 5);   // 3..7
        }

        static readonly string[] Reactions = {
            "♪ {song}. {rating} out of 7.", "ooh. {artist}.", "the building has music again.", "is this {artist}? the tenants approve.",
            "♪ {song} ♪ i know all the words. i don't.", "turn it up. no. don't. yes.", "{artist} again. you have a type.",
            "this one's a {rating} out of 7. i've decided.", "♪ bopping ♪", "{song}. this is my song now."
        };

        public static string React(Song s, bool heardArtistBefore)
        {
            string line = heardArtistBefore && Rng.Chance(0.35) ? "{artist} again. you have a type." : Rng.Pick(Reactions);
            string title = s.Title.Length > 32 ? s.Title.Substring(0, 30) + "…" : s.Title;
            return line.Replace("{song}", title).Replace("{artist}", s.Artist).Replace("{rating}", Rating(s).ToString());
        }
    }
}
