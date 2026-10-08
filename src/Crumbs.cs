// Crumbs.cs - Mothball's pocket money. Earned by focusing (time blocks: 1 per 5 focused minutes, +5 for finishing a
// block), by using it (+1 a request, up to 15 a day), by petting it (+1, up to 10 a day) and +3 for the first hello of the
// day. Spent in the shop (Shop.cs). Everything happens on the UI thread (Earn from other threads is posted there), because
// the brain is saved from the UI thread every few seconds. C# 5 only.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bestie
{
    public static class Crumbs
    {
        static Brain brain;
        static Action<Action> post;
        static double focusSeconds, blockSeconds;

        /// <summary>Crumbs just landed (amount, why: focus, block, ask, pet, hello). UI thread.</summary>
        public static event Action<int, string> Earned;
        /// <summary>Something was bought, worn or taken off. UI thread.</summary>
        public static event Action Changed;
        /// <summary>It just bought this (the creature reacts: puts it on, eats the toast...). UI thread.</summary>
        public static event Action<ShopItem> Bought;
        /// <summary>Set by the app: opens the shop window. UI thread.</summary>
        public static Action OpenShop;

        /// <summary>For the assistant: what it has and wears, in a line.</summary>
        public static string Summary()
        {
            if (brain == null) return null;
            var owned = brain.Owned.Select(id => Shop.Find(id)).Where(i => i != null).Select(i => i.Name).ToArray();
            var worn = brain.Wearing.Values.Select(id => Shop.Find(id)).Where(i => i != null).Select(i => i.Name).ToArray();
            return "your shop life: " + brain.Crumbs + " crumbs (earned by their focus blocks, using you and petting you)"
                + (owned.Length > 0 ? "; you own " + string.Join(", ", owned) : "; you don't own anything yet")
                + (worn.Length > 0 ? "; wearing " + string.Join(" and ", worn) : "")
                + (brain.ToastsEaten > 0 ? "; you HAVE had toast now (" + brain.ToastsEaten + " slice" + (brain.ToastsEaten == 1 ? "" : "s") + ")" : "") + ". shop tool buys and dresses you.";
        }

        public static readonly Dictionary<string, int> DailyCaps = new Dictionary<string, int> { { "ask", 15 }, { "pet", 10 }, { "hello", 3 } };

        public static void Init(Brain b, Action<Action> uiPoster)
        {
            brain = b; post = uiPoster;
            // a starter tip, once, so there's something to try right away (a party hat, or its first toast)
            if (brain.CrumbsEver == 0 && brain.Owned.Count == 0) { brain.Crumbs = 20; brain.CrumbsEver = 20; }
        }

        public static int Balance { get { return brain != null ? brain.Crumbs : 0; } }

        /// <summary>Any thread.</summary>
        public static void Earn(string why, int amount)
        {
            if (brain == null || amount <= 0) return;
            Action apply = () => Apply(why, amount);
            if (post != null) post(apply); else apply();
        }

        static void Apply(string why, int amount)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (brain.CrumbDay != today) { brain.CrumbDay = today; brain.CrumbsToday.Clear(); }
            int got = 0, cap;
            if (!brain.CrumbsToday.ContainsKey("hello") && why != "hello")
            {
                brain.CrumbsToday["hello"] = 3;   // first crumbs of the day come with a hello bonus
                got += 3;
            }
            brain.CrumbsToday.TryGetValue(why, out cap);
            int allowed = DailyCaps.ContainsKey(why) ? Math.Max(0, Math.Min(amount, DailyCaps[why] - cap)) : amount;
            brain.CrumbsToday[why] = cap + allowed;
            got += allowed;
            if (got <= 0) return;
            brain.Crumbs += got;
            brain.CrumbsEver += got;
            var e = Earned;
            if (e != null) try { e(got, why); } catch (Exception ex) { DebugLog.Write("crumbs: " + ex.Message); }
        }

        /// <summary>UI thread, about every 0.4 s: focus time pays.</summary>
        public static void FocusTick(double dt)
        {
            if (!TimeBlocks.Running || TimeBlocks.Paused || TimeBlocks.Phase != "focus") return;
            focusSeconds += dt;
            blockSeconds += dt;
            while (focusSeconds >= 300) { focusSeconds -= 300; Earn("focus", 1); }
        }

        /// <summary>A focus block ended (finished or skipped): a bonus if they really did most of it.</summary>
        public static void FocusBlockEnded()
        {
            bool real = blockSeconds >= 0.8 * TimeBlocks.FocusMinutes * 60;
            blockSeconds = 0;
            if (real) Earn("block", 5);
        }

        public static bool Owns(string id) { return brain != null && brain.Owned.Contains(id); }

        public static string WornIn(string slot)
        {
            string id;
            return brain != null && brain.Wearing.TryGetValue(slot, out id) ? id : null;
        }

        /// <summary>UI thread. Null when it worked, else why not (in its voice).</summary>
        public static string Buy(ShopItem item)
        {
            if (brain == null || item == null) return "the shop is closed.";
            if (item.Kind != "snack" && brain.Owned.Contains(item.Id)) return "i already have the " + item.Name + ".";
            if (brain.Crumbs < item.Price) return "that's " + item.Price + " crumbs. i have " + brain.Crumbs + ". focus blocks pay best.";
            brain.Crumbs -= item.Price;
            if (item.Kind != "snack") brain.Owned.Add(item.Id);
            if (item.Kind == "wear") brain.Wearing[item.Slot] = item.Id;   // new clothes go straight on
            Journal.Add("i bought " + (item.Kind == "snack" ? "" : "a ") + item.Name + " for " + item.Price + " crumbs.");
            try { brain.Save(); } catch (Exception ex) { DebugLog.Write("crumbs: save: " + ex.Message); }
            Raise();
            var b = Bought;
            if (b != null) try { b(item); } catch (Exception ex) { DebugLog.Write("crumbs: " + ex.Message); }
            return null;
        }

        /// <summary>UI thread. Put on (or, if it's already on, take off) something they own.</summary>
        public static string ToggleWear(ShopItem item)
        {
            if (brain == null || item == null || item.Kind != "wear") return "that's not something i can wear.";
            if (!brain.Owned.Contains(item.Id)) return "i don't have the " + item.Name + " yet.";
            string on;
            if (brain.Wearing.TryGetValue(item.Slot, out on) && on == item.Id) brain.Wearing.Remove(item.Slot);
            else brain.Wearing[item.Slot] = item.Id;
            Raise();
            return null;
        }

        public static void TakeOffAll()
        {
            if (brain == null) return;
            brain.Wearing.Clear();
            Raise();
        }

        static void Raise()
        {
            var c = Changed;
            if (c != null) try { c(); } catch (Exception ex) { DebugLog.Write("crumbs: " + ex.Message); }
        }
    }
}
