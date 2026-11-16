#nullable enable
using System;
using System.Linq;

namespace NightStud.Shared {
    public sealed class GearSpec {
        public readonly string Key,Icon,Name,English,Description,EnglishDescription,Effect;
        public readonly int Price,Tier,Uses,ChargeCapacity;
        public readonly bool Repairable,RequiresProperty;
        public bool Rechargeable=>ChargeCapacity>0;
        public bool Durable=>Uses>1;
        public string Identifier=>"night_stud.gear."+Key;
        public GearSpec(string key,string icon,string name,string english,int price,int tier,string description,string englishDescription,int uses=1,bool repairable=false,int chargeCapacity=0,bool requiresProperty=false,string effect=""){Key=key;Icon=icon;Name=name;English=english;Price=price;Tier=tier;Description=description;EnglishDescription=englishDescription;Uses=uses;Repairable=repairable;ChargeCapacity=chargeCapacity;RequiresProperty=requiresProperty;Effect=effect.Length>0?effect:key;}
    }
    public static class GearCatalogue {
        public const string RemainingTag="night_stud.gear.remaining.v1";
        public const string ChargeTag="night_stud.gear.charge.v1";
        public static readonly GearSpec[] All={
            new GearSpec("phaseLens","gear_lens","虹膜片","Iris Lens",80,1,"看一张底牌。每片可用一次。","Read one hidden card. Single use."),
            new GearSpec("exchangeSleeve","gear_sleeve","布袖口","Card Sleeve",120,2,"替换指定的手牌。","Replace a selected card.",8),
            new GearSpec("whiteNoise","gear_jammer","四眼扣","Signal Button",180,3,"压制低阶装置。","Suppress lower-tier devices.",8),
            new GearSpec("dualSleeve","gear_selector","双扣衬袖","Twin Sleeve",260,4,"从两张候选牌中择优换入。","Choose a replacement from two cards.",6),
            new GearSpec("tintFilter","gear_filter","茶色镜","Tinted Lens",45,1,"只能分辨底牌红黑。","Reveal a hidden card's color.",8),
            new GearSpec("seamPickup","gear_pickup","听线针","Signal Needle",150,2,"监听本手余下的可疑动作。每根可用一次。","Detect suspicious signals for this hand. Single use."),
            new GearSpec("deckReader","gear_reader","走时表","Deck Watch",290,3,"看当时牌堆顶的一张牌。","Read the next card in the deck.",10),
            new GearSpec("signalThimble","gear_veil","补衣顶针","Signal Thimble",360,3,"梭哈中遮断三级及以下的底牌读取。","Block hidden-card scans up to tier III in Stud.",8),
            new GearSpec("threeNeedleWatch","gear_widereader","三针表","Three-Hand Watch",720,4,"看当时牌堆顶的三张牌。","Read the next three cards in the deck.",12),
            new GearSpec("pairClip","gear_pairclip","对纹夹","Pair Clip",480,4,"寻找能与其余手牌配对的牌，替换指定牌。","Replace a card with a matching rank from the deck.",6),
            new GearSpec("durableLens","gear_durablelens","耐用透视镜","Durable Reader",600,2,"看一张底牌。损坏后可用电子零件修补，工具会保留。","Read one hidden card. Repair it with an electronic component when worn out; the tool is kept.",12,true,0,false,"phaseLens"),
            new GearSpec("chargedLens","gear_chargelens","充能透视镜","Powered Reader",1800,3,"看一张底牌。每次消耗 1 电量和 1 耐久。用原版能量电池充电，电子零件修补耐久。","Read one hidden card. Each use costs 1 charge and 1 durability. Recharge with a native energy battery and repair with an electronic component.",60,true,24,true,"phaseLens")
        };
        public static GearSpec? Find(string key)=>All.FirstOrDefault(g=>g.Key==key||g.Identifier==key);
        public static bool Contains(string id)=>All.Any(g=>g.Identifier==id);
    }
}
