using System;
using System.Linq;

namespace Bestie
{
    /// <summary>Things the creature says. {plank} is the nickname of a nearby window, {screen} the current screen,
    /// {name} the creature, {smell} what the current window smells like.</summary>
    public static class Lines
    {
        public static readonly string[] Musings = {
            "hm.", "i am small.", "what if windows are just big birds.", "the arrow is up to something.",
            "i've decided to be a little guy today.", "i should get a hat.", "every pixel is a tiny room.",
            "thinking about toast.", "who put all these rectangles here.", "i live here now.", "do you ever just.",
            "i counted the pixels. lost count.", "the edge of the world is mine.", "is it still today?",
            "i could go for a crumb.", "i'm not lost. i'm exploring.", "i wonder what {plank} is thinking about.",
            "{plank} looks comfy.", "if i sit still maybe i'm furniture.", "brb, existing.", "big day of doing nothing.",
            "i have one (1) plan.", "the fans are singing again.", "i'm the smallest window.", "what's a folder.",
            "i think the taskbar is shy.", "{screen} is nice this time of day.", "somewhere a tab is loading.",
            "i'm not procrastinating. i'm curating.", "one day i'll climb to the top of everything.", "what do clicks taste like."
        };
        public static readonly string[] NightMusings = { "it's dark in the computer.", "shh. the windows are sleeping.", "why are we awake.", "night shift.", "the pixels are dimmer at night. i think." };
        public static readonly string[] MorningMusings = { "morning. i think.", "the screen is warm again.", "new day. same rectangles." };

        public static readonly string[] NewPlank = { "a new plank! i'll call it {plank}.", "ooh. {plank}.", "this is {plank} now. i've decided.", "hello, {plank}." };
        public static readonly string[] PickedUp = { "whoa", "hi!", "where are we going", "put me down or don't. i'm not your boss.", "i can see my house", "wheeeee", "hello up here" };
        public static readonly string[] HeldLong = { "this is my life now.", "i live up here now i guess.", "my legs are just decoration now.", "are we there yet" };
        public static readonly string[] Petted = { "hehe", "♥", "more.", "that's the spot.", "i'm purring. i don't know how.", "ok ok ok", "i accept this." };
        public static readonly string[] NapStart = { "nap time.", "just resting my eyes.", "don't let me sleep more than 3 hours.", "five more minutes. starting now." };
        public static readonly string[] Wake = { "*yawn*", "what year is it.", "i dreamed i was a window.", "i'm up. mostly.", "how long was i out." };
        public static readonly string[] Dance = { "♪", "look at me go.", "no one is watching. perfect.", "i call this one 'the rectangle'." };
        public static readonly string[] Sniff = { "sniff sniff.", "smells like {smell}.", "{plank} smells like {smell}.", "hm. {smell}." };
        public static readonly string[] Peek = { "it's a long way down.", "hello down there.", "hm. far.", "i see the floor. it sees me." };
        public static readonly string[] ClimbStart = { "going up.", "i am a mountain goat.", "don't watch. or do." };
        public static readonly string[] Ceiling = { "i'm upside down. everything's fine.", "the ceiling is nice. who knew.", "blood rushing to my everything." };
        public static readonly string[] Patrol = { "all clear.", "nothing to report.", "the perimeter is secure.", "no intruders. except the arrow." };
        public static readonly string[] Explore = { "what's over here.", "adventure time.", "i've never been over there.", "going on a trip." };
        public static readonly string[] Songs = { "♪ la la la ♪", "♪ i am small ♪ the screen is tall ♪", "♪ {plank}, {plank}, you're my favorite plank ♪",
            "♪ doo doo doo ♪", "♪ this is a song about {screen} ♪", "♪ hmm hmm hmmmm ♪" };
        public static readonly string[] Exercise = { "for my health.", "feel the burn.", "one more. no. done." };
        public static readonly string[] Sunbathe = { "closest to the sun.", "warm up here.", "don't disturb. basking." };
        public static readonly string[] Visit = { "hi, arrow.", "what are you pointing at.", "hello big arrow.", "i came to see you." };
        public static readonly string[] Welcome = { "oh! you're back.", "hi. i didn't miss you. (i did)", "welcome back. i guarded everything.", "you're back! nothing happened. probably." };
        public static readonly string[] Dropped = { "betrayed by {plank}.", "{plank} left without me.", "rude.", "i was standing on that.", "hey!" };
        public static readonly string[] HardLanding = { "oof.", "i meant to do that.", "ta-da.", "ow. but also ta-da." };
        public static readonly string[] Unreachable = { "can't get there. i accept this.", "too high. i'll just think about it.", "hm. no way up." };
        public static readonly string[] Bonk = { "bonk.", "ow. a wall.", "found the edge. with my face." };

        public static string Smell(string app)
        {
            if (app == null) return "floor";
            switch (app)
            {
                case "chrome": case "msedge": case "firefox": case "brave": case "opera": case "arc": case "vivaldi": return Rng.Pick("tabs", "cookies. the internet kind.", "a thousand websites");
                case "explorer": return Rng.Pick("folders", "old downloads");
                case "code": case "devenv": case "cursor": case "notepad++": case "sublime_text": case "zed": return Rng.Pick("semicolons", "someone else's code");
                case "windowsterminal": case "cmd": case "powershell": case "pwsh": return Rng.Pick("hot rocks", "commands");
                case "spotify": return "vibes";
                case "discord": case "slack": case "teams": case "ms-teams": case "telegram": case "whatsapp": return "gossip";
                case "notepad": return "paper";
                case "claude": return "a clever cousin";
                default: return Rng.Pick("warm plastic", "pixels", "nothing. suspicious.", "dust", "new window smell");
            }
        }

        public static string Fill(string line, Creature c, string app)
        {
            if (line.Contains("{plank}"))
            {
                string a = app;
                if (a == null && c.Support != null) a = c.Support.App;
                if (a == null)
                {
                    var known = c.World.Wins.Where(w => c.Brain.Apps.ContainsKey(w.App)).Select(w => w.App).ToList();
                    if (known.Count > 0) a = Rng.Pick(known);
                }
                if (a == null) return null;
                line = line.Replace("{plank}", c.Brain.Nick(a));
            }
            if (line.Contains("{screen}"))
            {
                var m = c.CurrentMon;
                ScreenMemory sm;
                line = line.Replace("{screen}", m != null && c.Brain.Screens.TryGetValue(m.Key, out sm) ? sm.Nick : "this screen");
            }
            if (line.Contains("{smell}")) line = line.Replace("{smell}", Smell(app ?? (c.Support != null ? c.Support.App : null)));
            if (line.Contains("{name}")) line = line.Replace("{name}", c.Brain.Name);
            return line;
        }

        /// <summary>Pick a line from the pool that makes sense right now (e.g. skip plank lines when there are no planks).</summary>
        public static string FillPick(string[] pool, Creature c, string app)
        {
            for (int i = 0; i < 8; i++)
            {
                string s = Fill(Rng.Pick(pool), c, app);
                if (s != null) return s;
            }
            return Fill(pool.FirstOrDefault(l => !l.Contains("{plank}")) ?? "hm.", c, app);
        }

        public static string Number(int n)
        {
            string[] w = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };
            return n < w.Length ? w[n] : n.ToString();
        }
    }
}
