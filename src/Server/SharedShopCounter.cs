using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightStud.Shared;

namespace NightStud.Server;

public sealed partial class RoomHub
{
    sealed class CounterLot(string visitor,ShopThing item,string payload,int nodes){
        public readonly string Visitor=visitor,Hash=CounterHash(payload),Payload=payload;public readonly ShopThing Item=item;
        public readonly int Nodes=nodes;
        public DateTime Expires=DateTime.UtcNow.AddSeconds(120);
    }
    static string CounterHash(string payload)=>Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    static bool CounterOverlap(ShopThing a,ShopThing b)=>a.Zone==b.Zone&&a.X-a.W*.5<b.X+b.W*.5&&a.X+a.W*.5>b.X-b.W*.5&&a.Y-a.H*.5<b.Y+b.H*.5&&a.Y+a.H*.5>b.Y-b.H*.5;
    bool BatchCounter(Session guest,string owner)=>guest.CounterBatches&&FindPlayer(owner)?.CounterBatches==true;
    object[] CounterView(string owner,SharedShopState state)=>state.CounterLots.Values.Select(l=>new{id=l.Item.Id,visitor=l.Visitor,name=FindPlayer(l.Visitor)?.Name??"访客",hash=l.Hash,item=l.Item}).ToArray();
    void PruneCounter(string owner,SharedShopState state,DateTime now){
        foreach(var lot in state.CounterLots.Values.ToArray()){
            bool active=visiting.TryGetValue(lot.Visitor,out var host)&&host==owner&&FindPlayer(lot.Visitor) is {} s&&Online(s);
            if(!active||lot.Expires<=now||state.Scene.CounterBusy||!state.Scene.CounterStaging||ReceptionEnabled(owner)&&state.Serving!=lot.Visitor||state.Scene.Items.Any(i=>CounterOverlap(lot.Item,i)))
                if(state.CounterLots.Remove(lot.Item.Id))state.CounterRevision++;
        }
    }
    void CounterAction(Session guest,Command c,string owner,SharedShopState state){
        Need(guest.Id!=owner&&visiting.TryGetValue(guest.Id,out var host)&&host==owner,"visit_scope","请先到朋友的柜台外。");
        Need(state.Scene.CounterStaging,"client_update","店主更新模组后可使用柜台上货。");
        Need(c.ShopEpoch==state.Epoch,"scene_changed","已经换了店铺，请重新上货。");
        if(c.Type=="shopCounterRemove"){
            var ids=c.CounterLots??new[]{c.CounterLot};Need(ids.Length is >0 and <=8&&ids.Distinct().Count()==ids.Length&&ids.All(id=>id!=null&&ShopKey.IsMatch(id)),"bad_counter","请选择要取回的货物。");
            var own=state.CounterLots.Values.Where(l=>l.Visitor==guest.Id).ToArray();
            var selected=own.Where(l=>ids.Contains(l.Item.Id)).ToArray();
            Need(ids.Length==1||selected.Length==ids.Length,"counter_changed","柜台清单已变，正在更新。");
            if(selected.Length==0){
                Need(own.Length==0||BatchCounter(guest,owner),"counter_changed","柜台上的货已变，正在更新。");guest.Client!.Send(SharedShopView(owner));return;
            }
            Need(c.CounterRevision==state.CounterRevision,"counter_changed","柜台上的货已变，正在更新。");
            foreach(var lot in selected)state.CounterLots.Remove(lot.Item.Id);state.CounterRevision++;BroadcastSharedShop(owner);Audit("counter_withdrawn",guest);return;
        }
        Need(!state.Scene.CounterBusy,"counter_busy","店主正在接待顾客，请稍后上货。");
        SyncReception(owner,state);Need(!ReceptionEnabled(owner)||state.Serving==guest.Id,"visitor_waiting","等店主接待你，再把货放上柜台。");
        if(c.Type=="shopCounterKeep"){
            var ids=c.CounterLots??new[]{c.CounterLot};Need(ids.Length is >0 and <=8&&ids.Distinct().Count()==ids.Length&&ids.All(id=>id!=null&&ShopKey.IsMatch(id)),"bad_counter","柜台清单无效。");
            var held=state.CounterLots.Values.Where(l=>l.Visitor==guest.Id&&ids.Contains(l.Item.Id)).ToArray();Need(held.Length==ids.Length,"counter_changed","柜台上的货已取回。");
            ShopActionRate(guest,"counter_keep",10);foreach(var lot in held)lot.Expires=DateTime.UtcNow.AddSeconds(120);return;
        }
        Need(c.SceneRevision==state.Revision&&c.CounterRevision==state.CounterRevision,"counter_changed","柜台有变化，请重新放置。");
        Need(c.CounterItem!=null&&c.Price is >0 and <=1000000,"bad_counter","请选择有估价的货物。");
        var item=c.CounterItem!;Need(item.Zone=="counter"&&item.Id!=null&&item.Id==c.CounterLot&&ShopKey.IsMatch(item.Id)&&item.CanTrade&&item.Quote==c.Price&&c.Payload is {Length:>1 and <=60000},"bad_counter","柜台货物数据无效。");
        var kind=GoodsLedger.Validate(c.Payload);using var doc=JsonDocument.Parse(c.Payload);var root=doc.RootElement.GetProperty("nodes")[0];
        Need(item.Kind==kind.Id&&item.Count==root.GetProperty("unitCount").GetInt32(),"bad_counter","货物与上桌清单不一致。");
        // Validate the actual native shape dimensions against the owner's grid.
        var zone=state.Scene.Zones.FirstOrDefault(z=>z.Id=="counter");Need(zone!=null&&zone.W%18==0&&zone.W/18 is >=8 and <=32,"bad_counter","柜台格位尚未同步。");
        string shapeKey=root.TryGetProperty("modified",out var modified)&&modified.ValueKind==JsonValueKind.String?"modified":"shape";
        Need(root.TryGetProperty(shapeKey,out var shapeValue)&&shapeValue.ValueKind==JsonValueKind.String,"bad_counter","货物缺少形状。");
        using var shape=JsonDocument.Parse(shapeValue.GetString()!);var sh=shape.RootElement;
        bool Int(string key,out int value){value=0;return sh.TryGetProperty("<"+key+">k__BackingField",out var n)&&n.TryGetInt32(out value);}
        int width=0,height=0,orientation=0;Need(Int("width",out width)&&Int("height",out height)&&Int("orientation",out orientation)&&width is >0 and <=64&&height is >0 and <=64&&orientation is >=0 and <=3,"bad_counter","货物形状超出范围。");
        Need(sh.TryGetProperty("<flipped>k__BackingField",out var flipped)&&flipped.ValueKind is JsonValueKind.True or JsonValueKind.False,"bad_counter","货物朝向无效。");
        int grid=zone!.W/18;Need(item.W==(orientation%2==0?width:height)*grid&&item.H==(orientation%2==0?height:width)*grid&&item.Orientation==orientation&&item.Flipped==flipped.GetBoolean(),"bad_counter","货物占用的格位不符。");
        ValidateShopScene(Wire.Encode(new ShopScene{Generation=state.Scene.Generation,Layers=state.Scene.Layers,Zones=[zone],Items=[item]}));
        Need((item.X-zone.X-item.W/2)%grid==0&&(item.Y-zone.Y-item.H/2)%grid==0,"bad_counter","请把货放在柜台格位内。");
        bool batch=BatchCounter(guest,owner);
        Need(!state.CounterLots.TryGetValue(item.Id,out var same)||same.Visitor==guest.Id,"bad_counter","货物编号已使用。");
        Need(!batch||same!=null||state.CounterLots.Values.Count(l=>l.Visitor==guest.Id)<8,"counter_capacity","柜台最多摆 8 件货物。");
        int nodes=doc.RootElement.GetProperty("nodes").GetArrayLength();Need(!batch||nodes+state.CounterLots.Values.Where(l=>l.Visitor==guest.Id&&l.Item.Id!=item.Id).Sum(l=>l.Nodes)<=64,"counter_capacity","这批货包含的物品较多，请分开交易。");
        Need(!state.Scene.Items.Any(i=>CounterOverlap(item,i))&&!state.CounterLots.Values.Any(l=>(batch?l.Item.Id!=item.Id:l.Visitor!=guest.Id)&&CounterOverlap(item,l.Item)),"counter_occupied","这块柜台已经有货了。");
        ShopActionRate(guest,"counter_put",.5);
        int used=sharedShops.Values.Sum(s=>s.CounterLots.Values.Sum(l=>l.Payload.Length));int previousSize=batch?(same?.Payload.Length??0):state.CounterLots.Values.Where(l=>l.Visitor==guest.Id).Sum(l=>l.Payload.Length);
        Need(used-previousSize+c.Payload.Length<=2000000,"counter_capacity","柜台暂满，等一会儿再上货。");
        if(!batch)foreach(var old in state.CounterLots.Values.Where(l=>l.Visitor==guest.Id).ToArray())state.CounterLots.Remove(old.Item.Id);
        state.CounterLots[item.Id]=new CounterLot(guest.Id,item,c.Payload,nodes);state.CounterRevision++;BroadcastSharedShop(owner);Audit("counter_staged",guest);
    }
    void CheckCounterOffer(Session seller,Command c){
        if(c.CounterLots!=null){CheckBatchCounterOffer(seller,c);return;}
        if(string.IsNullOrEmpty(c.CounterLot))return;
        Need(visiting.TryGetValue(seller.Id,out var host)&&host==c.Target&&sharedShops.TryGetValue(host,out var state)&&state.CounterLots.TryGetValue(c.CounterLot,out var lot)&&lot.Visitor==seller.Id&&lot.Expires>DateTime.UtcNow&&lot.Hash==CounterHash(c.Payload),"counter_changed","货物已变，请取回后重新上桌。");
    }
    void ResolveCounterPayload(Session seller,Command c){
        if(string.IsNullOrEmpty(c.CounterLot)||!string.IsNullOrEmpty(c.Payload))return;
        CounterLot? lot=null;Need(visiting.TryGetValue(seller.Id,out var host)&&host==c.Target&&sharedShops.TryGetValue(host,out var state)&&state.CounterLots.TryGetValue(c.CounterLot,out lot)&&lot.Visitor==seller.Id&&lot.Expires>DateTime.UtcNow&&lot.Hash==c.CounterHash,"counter_changed","货物已变，请取回后重新上桌。");
        c.Payload=lot!.Payload;
    }
    void ClearCounterAfterOffer(Session seller){
        if(visiting.TryGetValue(seller.Id,out var host)&&sharedShops.TryGetValue(host,out var state)){
            var own=state.CounterLots.Values.Where(l=>l.Visitor==seller.Id).ToArray();foreach(var lot in own)state.CounterLots.Remove(lot.Item.Id);
            if(own.Length>0){state.CounterRevision++;BroadcastSharedShop(host);}
        }
    }
    void CheckBatchCounterOffer(Session seller,Command c){
        var ids=c.CounterLots!;Need(ids.Length is >0 and <=8&&ids.Distinct().Count()==ids.Length&&ids.All(id=>id!=null&&ShopKey.IsMatch(id))&&c.CounterHashes?.Length==ids.Length&&string.IsNullOrEmpty(c.CounterLot),"bad_counter","柜台清单无效。");
        Need(visiting.TryGetValue(seller.Id,out var host)&&host==c.Target&&BatchCounter(seller,host)&&sharedShops.TryGetValue(host,out _),"counter_changed","请重新查看柜台上的货。");
        var state=sharedShops[host!];var parcel=ShopTradePayload.Parse(c.Payload);var parts=parcel.Items;
        Need(c.ShopEpoch==state.Epoch&&c.CounterRevision==state.CounterRevision&&parts.Length==ids.Length&&state.CounterLots.Values.Count(l=>l.Visitor==seller.Id)==ids.Length,"counter_changed","柜台上的货已变，请重新报价。");
        for(int i=0;i<ids.Length;i++)Need(state.CounterLots.TryGetValue(ids[i],out var lot)&&lot.Visitor==seller.Id&&lot.Expires>DateTime.UtcNow&&lot.Hash==c.CounterHashes![i]&&lot.Hash==CounterHash(parts[i])&&(parcel.Layout==null||Wire.Encode(lot.Item)==Wire.Encode(parcel.Layout[i])),"counter_changed","货物清单与柜台不符，请重新报价。");
    }
    void ExpireCounters(DateTime now){
        foreach(var pair in sharedShops){long revision=pair.Value.CounterRevision;SyncReception(pair.Key,pair.Value);PruneCounter(pair.Key,pair.Value,now);if(revision!=pair.Value.CounterRevision)BroadcastSharedShop(pair.Key);}
    }
}
