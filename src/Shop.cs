// Shop.cs - crumbs and the things they buy. Mothball earns crumbs when you focus (time blocks), when you use it, and when
// you pet it; the shop sells toys (each unlocks something new it does on its own, like the jump rope), wearables, and toast
// (it has thought about toast its whole life). Balances live in the brain (.state\brain.json). C# 5 only.
using System;
using System.Collections.Generic;
using System.Linq;
// (AssistantTools' shop tool is at the bottom)

namespace Bestie
{
    public sealed class ShopItem
    {
        public string Id, Name, Kind, Slot, Blurb;   // Kind: toy | wear | snack. Slot (wear): head | face
        public int Price;
    }

    public static class Shop
    {
        public const string Currency = "crumbs";

        // prices: a 90-minute focus block earns ~23 crumbs; the jump rope is ~1.5 blocks, the crown ~5
        public static readonly ShopItem[] Items =
        {
            new ShopItem { Id = "jumprope", Name = "jump rope", Kind = "toy", Price = 30, Blurb = "for cardio. the building inspector in me approves." },
            new ShopItem { Id = "book", Name = "tiny book", Kind = "toy", Price = 55, Blurb = "i'll read in the corners. mostly the pictures." },
            new ShopItem { Id = "toast", Name = "toast", Kind = "snack", Price = 15, Blurb = "i have never had toast. this could change everything." },
            new ShopItem { Id = "partyhat", Name = "party hat", Kind = "wear", Slot = "head", Price = 20, Blurb = "every day is a party if you're brave enough." },
            new ShopItem { Id = "beanie", Name = "beanie", Kind = "wear", Slot = "head", Price = 35, Blurb = "warm. a little mysterious." },
            new ShopItem { Id = "bow", Name = "bow", Kind = "wear", Slot = "head", Price = 25, Blurb = "classy. the tenants will talk." },
            new ShopItem { Id = "shades", Name = "sunglasses", Kind = "wear", Slot = "face", Price = 45, Blurb = "for when the monitor is too bright. or i'm too cool." },
            new ShopItem { Id = "crown", Name = "crown", Kind = "wear", Slot = "head", Price = 120, Blurb = "superintendent of the whole building. officially." },
        };

        public static ShopItem Find(string idOrName)
        {
            string w = Norm(idOrName);
            if (w.Length == 0) return null;
            return Items.FirstOrDefault(i => Norm(i.Id) == w || Norm(i.Name) == w)
                ?? Items.FirstOrDefault(i => Norm(i.Name).Contains(w) || w.Contains(Norm(i.Name)) || (w == "glasses" && i.Id == "shades") || (w == "hat" && i.Id == "partyhat"));
        }

        static string Norm(string s) { return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()); }
    }

    internal sealed partial class AssistantTools
    {
        void AddShopTools(List<object> t)
        {
            t.Add(Tool("shop", "your shop (you earn crumbs when they focus, use you and pet you): check crumbs and what's for sale, buy something for "
                + "yourself, or put on / take off something you own. toys: jump rope, tiny book. wearables: party hat, beanie, bow, crown, sunglasses. "
                + "snack: toast. buy only when they say to.", R("action"),
                PE("action", "what to do (open: show the shop window)", new[] { "status", "open", "buy", "wear", "take_off" }),
                P("item", "string", "the item, as they said it ('the crown', 'sunglasses', 'jump rope')")));
        }

        AssistantToolResult ShopTool(string name, Dictionary<string, object> a)
        {
            if (name != "shop") return null;
            string action = Json.Str(a, "action") ?? "status";
            ShopItem item = Shop.Find(Json.Str(a, "item") ?? "");
            if (action != "status" && action != "open" && item == null && action != "take_off") return new AssistantToolResult("there's no " + (Json.Str(a, "item") ?? "item") + " in the shop. it sells: " + string.Join(", ", Shop.Items.Select(i => i.Name).ToArray()) + ".");
            if (DryRun) return new AssistantToolResult("done.");
            string result = module.OnUi<string>(delegate
            {
                switch (action)
                {
                    case "open":
                        if (Crumbs.OpenShop == null) return "the shop window isn't available.";
                        Crumbs.OpenShop();
                        return "opened the shop window (" + Crumbs.Balance + " crumbs).";
                    case "buy":
                        string p = Crumbs.Buy(item);
                        return p ?? "bought the " + item.Name + " for " + item.Price + " crumbs (" + Crumbs.Balance + " left)" + (item.Kind == "wear" ? "; it's on you now." : item.Kind == "snack" ? "; you're eating it right now." : "; you'll use it on your own.");
                    case "wear":
                        if (Crumbs.WornIn(item.Slot ?? "") == item.Id) return "you're already wearing the " + item.Name + ".";
                        return Crumbs.ToggleWear(item) ?? "you put on the " + item.Name + ".";
                    case "take_off":
                        if (item == null) { Crumbs.TakeOffAll(); return "you took everything off."; }
                        if (Crumbs.WornIn(item.Slot ?? "") != item.Id) return "you're not wearing the " + item.Name + ".";
                        return Crumbs.ToggleWear(item) ?? "you took off the " + item.Name + ".";
                    default:
                        return (Crumbs.Summary() ?? "") + " for sale: " + string.Join(", ", Shop.Items.Select(i => i.Name + " " + i.Price).ToArray()) + " crumbs.";
                }
            }, "the shop didn't answer.");
            return new AssistantToolResult(result + " say it in one short line, in character.", action == "status" ? null : "shop: " + action.Replace('_', ' ') + (item != null ? " " + item.Name : ""), null);
        }
    }
}
