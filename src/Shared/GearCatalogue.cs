#nullable enable
using System;
using System.Linq;

namespace NightStud.Shared {
    public sealed class GearSpec {
        public readonly string Key,Icon,Name,English,Description,EnglishDescription;
        public readonly int Price,Tier;
        public string Identifier=>"night_stud.gear."+Key;
        public GearSpec(string key,string icon,string name,string english,int price,int tier,string description,string englishDescription){Key=key;Icon=icon;Name=name;English=english;Price=price;Tier=tier;Description=description;EnglishDescription=englishDescription;}
    }
    public static class GearCatalogue {
        public static readonly GearSpec[] All={
            new GearSpec("phaseLens","gear_lens","虹膜片","Iris Lens",80,1,"看一张底牌。每片可用一次。","Read one hidden card. Single use."),
            new GearSpec("exchangeSleeve","gear_sleeve","布袖口","Card Sleeve",120,2,"替换指定的手牌。每份可用一次。","Replace a selected card. Single use."),
            new GearSpec("whiteNoise","gear_jammer","四眼扣","Signal Button",180,3,"压制低阶装置。每枚可用一次。","Suppress lower-tier devices. Single use."),
            new GearSpec("dualSleeve","gear_selector","双扣衬袖","Twin Sleeve",260,4,"从两张候选牌中择优换入。每份可用一次。","Choose a replacement from two cards. Single use."),
            new GearSpec("tintFilter","gear_filter","茶色镜","Tinted Lens",45,1,"只能分辨底牌红黑。每副可用一次。","Reveal a hidden card's color. Single use."),
            new GearSpec("seamPickup","gear_pickup","听线针","Signal Needle",150,2,"监听本手余下的可疑动作。每根可用一次。","Detect suspicious signals for this hand. Single use."),
            new GearSpec("deckReader","gear_reader","走时表","Deck Watch",290,3,"看当时牌堆顶的一张牌。每块可用一次。","Read the next card in the deck. Single use."),
            new GearSpec("signalThimble","gear_veil","补衣顶针","Signal Thimble",360,3,"梭哈中遮断三级及以下的底牌读取。每枚可用一次。","Block hidden-card scans up to tier III in Stud. Single use."),
            new GearSpec("threeNeedleWatch","gear_widereader","三针表","Three-Hand Watch",720,4,"看当时牌堆顶的三张牌。每块可用一次。","Read the next three cards in the deck. Single use."),
            new GearSpec("pairClip","gear_pairclip","对纹夹","Pair Clip",480,4,"寻找能与其余手牌配对的牌，替换指定牌。每枚可用一次。","Replace a card with a matching rank from the deck. Single use.")
        };
        public static GearSpec? Find(string key)=>All.FirstOrDefault(g=>g.Key==key||g.Identifier==key);
        public static bool Contains(string id)=>All.Any(g=>g.Identifier==id);
    }
}
