using System.Text.Json;
using System.Text.RegularExpressions;
using NightStud.Shared;

namespace NightStud.Server;

public sealed partial class RoomHub
{
    sealed class SharedShopState(ShopScene scene,string encoded){
        public ShopScene Scene=scene;public string Encoded=encoded;
        public readonly string Epoch=Guid.NewGuid().ToString("N");
        public long Revision=1,Presence=1;public DateTime Published=DateTime.UtcNow;
        public long NextArrival;public string Preferred="",Serving="";
        public DateTime NextBell;
        public long CounterRevision;public readonly Dictionary<string,CounterLot> CounterLots=new();
    }
    sealed record Inquiry(string Id,string Owner,string Visitor,string Item,string Kind,int Count,int Flags,int Quote,long Revision,DateTime Expires);
    sealed class VisitorPlace(string host,int slot,long arrival){public readonly string Host=host;public readonly int Slot=slot;public readonly long Arrival=arrival;public string Place="counter",Facing="front";}
    readonly Dictionary<string,SharedShopState> sharedShops=new();
    readonly Dictionary<string,VisitorPlace> visitorPlaces=new();
    readonly Dictionary<string,Inquiry> shopInquiries=new();
    readonly Dictionary<(string,string),DateTime> shopActions=new();
    static readonly Regex ShopKey=new("^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    static readonly HashSet<string> shopArt=LoadShopArt();
    static HashSet<string> LoadShopArt(){
        using var stream=typeof(RoomHub).Assembly.GetManifestResourceStream("NightStud.Server.shop-art.json")!;
        using var doc=JsonDocument.Parse(stream);return doc.RootElement.GetProperty("art").EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
    }
    public static ShopScene ValidateShopScene(string payload){
        Need(payload.Length is >0 and <=96000&&!payload.Contains("$type",StringComparison.OrdinalIgnoreCase),"bad_scene","店铺场景数据超出范围。");
        ShopScene? s;
        try{s=JsonSerializer.Deserialize<ShopScene>(payload,new JsonSerializerOptions(Wire.Json){MaxDepth=8});}
        catch(JsonException){throw new WireFault("bad_scene","店铺场景数据无效。");}
        Need(s!=null&&s.Generation!=null&&ShopKey.IsMatch(s.Generation)&&s.Layers is {Length:>0 and <=96}&&s.Items is {Length:<=192}&&s.Zones is {Length:<=3},"bad_scene","店铺场景数据无效。");
        foreach(var l in s!.Layers){
            Need(l!=null&&l.Art!=null&&shopArt.Contains(l.Art)&&l.X is >=-960 and <=1920&&l.Y is >=-540 and <=1080&&l.W is >0 and <=1920&&l.H is >0 and <=1080&&l.Order is >=-100 and <=100&&l.Color is >=0 and <=4294967295,"bad_scene","店铺布景超出原版素材或位置范围。");
        }
        var zoneIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var z in s.Zones)Need(z!=null&&z.Id is "showcase" or "counter" or "innerCounter"&&zoneIds.Add(z.Id)&&z.X is >=0 and <=960&&z.Y is >=0 and <=540&&z.W is >=8 and <=960&&z.H is >=8 and <=540&&z.X+z.W<=960&&z.Y+z.H<=540,"bad_scene","公开货区的位置无效。");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var i in s.Items){
            Need(i!=null&&i.Id!=null&&ShopKey.IsMatch(i.Id)&&ids.Add(i.Id)&&i.Kind!=null&&ShopCatalogue.All.ContainsKey(i.Kind)&&(!i.CanTrade||GoodsLedger.Catalogue.ContainsKey(i.Kind)&&i.Quote>0)&&i.Zone is "showcase" or "counter" or "innerCounter"&&i.Count is >0 and <=1000&&i.Quote is >=0 and <=1000000&&i.Flags is >=0 and <=7&&i.Orientation is >=0 and <=3&&i.X is >=0 and <=960&&i.Y is >=0 and <=540&&i.W is >=8 and <=512&&i.H is >=8 and <=512,"bad_scene","公开货物的种类、估价或位置无效。");
            if(s.Zones.Length>0){var z=s.Zones.FirstOrDefault(z=>z.Id==i.Zone);Need(z!=null&&i.X-i.W*.5>=z.X-1&&i.Y-i.H*.5>=z.Y-1&&i.X+i.W*.5<=z.X+z.W+1&&i.Y+i.H*.5<=z.Y+z.H+1,"bad_scene","货物超出公开货区。");}
        }
        return s;
    }
    void PublishSharedShop(Session owner,Command c){
        Need(owner.AccountId!=null&&owner.SharedShop&&owner.Privacy.AllowVisits&&owner.RoomCode==null&&!visiting.ContainsKey(owner.Id),"visits_disabled","回到自己的店里并开启串门后可接待访客。");
        Need(c.Name.Length is >0 and <=32&&!c.Name.Any(char.IsControl),"bad_shop","店铺名称无效。");moderation?.Content(c.Name);
        var now=DateTime.UtcNow;
        Need(!sharedShops.TryGetValue(owner.Id,out var old)||(now-old.Published).TotalMilliseconds>=750,"shop_rate","店铺更新过快。");
        var scene=ValidateShopScene(c.Payload);string encoded=Wire.Encode(scene);
        bool first=!shops.ContainsKey(owner.Id),changed=old==null||old.Encoded!=encoded,renamed=!first&&shops[owner.Id].Name!=c.Name;
        if(old!=null&&old.Scene.Generation!=scene.Generation){
            foreach(string id in visiting.Where(p=>p.Value==owner.Id).Select(p=>p.Key).ToArray())if(FindPlayer(id) is {} guest)LeaveVisit(guest);
            DropSharedShop(owner.Id);old=null;
        }
        var state=old??new SharedShopState(scene,encoded);
        if(old!=null&&changed){state.Revision++;state.Scene=scene;state.Encoded=encoded;}
        state.Published=now;sharedShops[owner.Id]=state;shops[owner.Id]=new Shop(c.Name){Updated=now};
        foreach(string id in visiting.Where(v=>v.Value==owner.Id).Select(v=>v.Key))if(!visitorPlaces.ContainsKey(id))PlaceNewVisitor(id,owner.Id);
        if(!scene.DoorOpen)foreach(var p in visitorPlaces.Values.Where(p=>p.Host==owner.Id&&p.Place=="inside")){p.Place="counter";p.Facing="front";state.Presence++;}
        foreach(var q in shopInquiries.Values.Where(q=>q.Owner==owner.Id&&(q.Expires<=now||scene.CounterBusy||!scene.Items.Any(i=>SameInquiryItem(q,i)))).ToArray())
            EndShopInquiry(q.Id,q.Expires<=now?"expired":scene.CounterBusy?"busy":"changed",q.Expires<=now?"这次问价已结束。":scene.CounterBusy?"店主正在接待顾客，请稍后问价。":"这件货已经挪走或有了变化。");
        if(changed||renamed)BroadcastSharedShop(owner.Id);if(first||renamed)BroadcastLobby();
    }
    object SharedShopView(string owner){
        var state=sharedShops[owner];SyncReception(owner,state);PruneCounter(owner,state,DateTime.UtcNow);bool reception=ReceptionEnabled(owner);var queue=ReceptionQueue(owner,state);
        var people=visiting.Where(v=>v.Value==owner).Select(v=>FindPlayer(v.Key)).Prepend(FindPlayer(owner)).Where(s=>s!=null&&Online(s)).Select(s=>{
            var p=visitorPlaces.TryGetValue(s!.Id,out var place)?place:null;
            int position=!reception||s.Id==owner?-1:s.Id==state.Serving?0:Array.IndexOf(queue,s.Id)+(state.Serving.Length>0?0:1);
            return new{id=s.Id,name=s.Name,provider=s.Provider,avatar=s.Avatar,tradeBatch=s.TradeBatches,owner=s.Id==owner,place=s.Id==owner?"owner":p?.Place??"counter",facing=p?.Facing??"front",slot=p?.Slot??-1,serving=reception&&s.Id==state.Serving,queuePosition=position};
        }).OrderBy(p=>p.owner?-1:reception?p.queuePosition:p.slot).ToArray();
        return new{type="shopScene",host=owner,shopName=shops[owner].Name,epoch=state.Epoch,revision=state.Revision,presence=state.Presence,reception,doorbell=FindPlayer(owner)?.ShopBell==true,serving=reception?state.Serving:null,nextVisitor=reception?state.Preferred:null,scene=state.Scene,people,
            counterBatch=FindPlayer(owner)?.CounterBatches==true,counterRevision=state.CounterRevision,counterLots=CounterView(owner,state),kinds=state.Scene.Items.Select(i=>i.Kind).Concat(state.CounterLots.Values.Select(l=>l.Item.Kind)).Distinct().Select(k=>ShopCatalogue.All[k]).ToArray()};
    }
    void BroadcastSharedShop(string owner){
        if(!sharedShops.ContainsKey(owner))return;
        object frame=SharedShopView(owner);
        foreach(var s in sessions.Values.Where(s=>s.SharedShop&&Online(s)&&(s.Id==owner||visiting.TryGetValue(s.Id,out var h)&&h==owner)))s.Client!.Send(frame);
    }
    (string owner,SharedShopState state) SharedContext(Session s){
        Need(s.AccountId!=null&&s.SharedShop&&s.RoomCode==null,"visit_unavailable","先进入店铺再操作。");
        string owner=visiting.TryGetValue(s.Id,out var host)?host:s.Id;
        Need(FindPlayer(owner) is {} person&&Online(person)&&person.RoomCode==null&&person.Privacy.AllowVisits&&sharedShops.ContainsKey(owner),"visit_unavailable","店主已结束接待。");
        return(owner,sharedShops[owner]);
    }
    void ShopActionRate(Session s,string action,double seconds){
        var key=(s.Id,action);var now=DateTime.UtcNow;
        Need(!shopActions.TryGetValue(key,out var previous)||(now-previous).TotalSeconds>=seconds,"shop_action_rate","等对方看完再问。");
        shopActions[key]=now;
    }
    void SharedShopAction(Session s,Command c){
        var (owner,state)=SharedContext(s);
        if(c.Type is "shopCounterPut" or "shopCounterRemove" or "shopCounterKeep"){CounterAction(s,c,owner,state);return;}
        if(c.Type=="shopBell"){RingShopBell(s,c,owner,state);return;}
        if(c.Type=="shopSync"){ShopActionRate(s,"sync",1);s.Client!.Send(SharedShopView(owner));return;}
        if(c.Type=="shopServe"){Need(owner==s.Id,"visit_scope","只有店主可以切换接待访客。");SelectReception(s,c,state);return;}
        if(c.Type=="shopKick"){
            Need(owner==s.Id&&visiting.TryGetValue(c.Target,out var host)&&host==owner,"visit_scope","这位玩家不在你的店里。");
            if(FindPlayer(c.Target) is {} guest)LeaveVisit(guest);return;
        }
        if(c.Type is "shopEnter" or "shopExit" or "shopLook"){
            Need(owner!=s.Id&&visitorPlaces.ContainsKey(s.Id),"visit_scope","先到朋友的柜台外再操作。");
            var p=visitorPlaces[s.Id];
            if(c.Type=="shopEnter")Need(state.Scene.DoorOpen,"door_closed","店主尚未开门，请在柜台外等候。");
            Need(c.SceneRevision==state.Revision,"scene_changed","店铺状态已更新，请再试一次。");
            ShopActionRate(s,"position",.25);
            if(c.Type=="shopEnter"){p.Place="inside";p.Facing="front_three_quarter";}
            else if(c.Type=="shopExit"){p.Place="counter";p.Facing="front";}
            else{Need(VisitLooks.Facing(c.Facing),"bad_facing","人物朝向无效。");p.Facing=c.Facing;}
            state.Presence++;BroadcastSharedShop(owner);Audit("visit_position",s,null,p.Place);return;
        }
        if(c.Type=="shopAnswer"){
            Need(shopInquiries.TryGetValue(c.Inquiry,out var q)&&q.Owner==s.Id&&q.Expires>DateTime.UtcNow,"inquiry_expired","这次询价已结束。");
            EndShopInquiry(c.Inquiry,"declined","店主暂时不卖这件。");return;
        }
        Need(c.SceneRevision==state.Revision,"scene_changed","柜台货物已变，正在更新。");
        var item=state.Scene.Items.FirstOrDefault(i=>i.Id==c.Item);Need(item!=null,"item_changed","这件货已经挪走。");
        if(c.Type=="shopInspect"){
            ShopActionRate(s,"inspect",.25);
            var kind=ShopCatalogue.All[item!.Kind];
            if(visitorPlaces.TryGetValue(s.Id,out var pose)){
                string face=item.Zone=="showcase"?"side_left":pose.Place=="inside"?"back_three_quarter":"front";
                if(pose.Facing!=face){pose.Facing=face;state.Presence++;BroadcastSharedShop(owner);}
            }
            var frame=new{type="shopFocus",host=owner,revision=state.Revision,player=s.Id,name=s.Name,item=item.Id,kind,quote=item.Quote,count=item.Count,flags=item.Flags};
            foreach(var person in sessions.Values.Where(p=>Online(p)&&(p.Id==owner||visiting.TryGetValue(p.Id,out var h)&&h==owner)))person.Client!.Send(frame);
        }else{
            Need(item!.CanTrade,"display_only","这件是店主的陈列品。");
            Need(s.Id!=owner,"inquiry_self","这是你自己的店铺。");Need(!state.Scene.CounterBusy,"counter_busy","店主正在接待顾客，请稍后问价。");ShopActionRate(s,"ask",3);
            ExpireShopInquiries(DateTime.UtcNow);
            Need(shopInquiries.Values.Count(q=>q.Owner==owner)<8&&!shopInquiries.Values.Any(q=>q.Visitor==s.Id&&q.Owner==owner&&q.Item==c.Item),"inquiry_pending","店主正在处理询价。");
            var q=new Inquiry(Guid.NewGuid().ToString("N"),owner,s.Id,item!.Id,item.Kind,item.Count,item.Flags,item.Quote,state.Revision,DateTime.UtcNow.AddSeconds(45));shopInquiries[q.Id]=q;
            if(visitorPlaces.TryGetValue(s.Id,out var speaker)){speaker.Facing="front_three_quarter";state.Presence++;BroadcastSharedShop(owner);}
            FindPlayer(owner)!.Client!.Send(new{type="shopInquiry",host=owner,epoch=state.Epoch,id=q.Id,visitor=s.Id,name=s.Name,item=item.Id,kind=GoodsLedger.Catalogue[item.Kind],quote=item.Quote,expires=45});
            s.Client!.Send(new{type="shopAsked",host=owner,epoch=state.Epoch,id=q.Id,item=item.Id,message=ReceptionEnabled(owner)&&state.Serving!=s.Id?"问价已排队，等店主接待。":"已问价，等店主回话。"});Audit("shop_inquiry",s,null,owner);
        }
    }
    void CheckShopQuote(Session seller,Command c){
        if(c.Inquiry.Length==0)return;
        Need(shopInquiries.TryGetValue(c.Inquiry,out var q)&&q.Owner==seller.Id&&q.Visitor==c.Target&&q.Expires>DateTime.UtcNow&&Related(seller.Id,c.Target,visiting),"inquiry_expired","这次询价已结束。");
        Need(sharedShops.TryGetValue(seller.Id,out var state)&&state.Scene.Items.Any(i=>SameInquiryItem(q!,i)),"item_changed","这件货已经挪走或发生变动。");
        SyncReception(seller.Id,state!);
        Need(!ReceptionEnabled(seller.Id)||state!.Serving==q!.Visitor,"visitor_waiting","请先接待这位访客再报价。");
        var kind=GoodsLedger.Validate(c.Payload);
        using var packet=JsonDocument.Parse(c.Payload);
        Need(q!.Kind==kind.Id&&packet.RootElement.GetProperty("nodes")[0].GetProperty("unitCount").GetInt32()==q.Count,"item_changed","报价的货物与访客所看的不同。");
    }
    static bool SameInquiryItem(Inquiry q,ShopThing i)=>i.CanTrade&&i.Id==q.Item&&i.Kind==q.Kind&&i.Count==q.Count&&i.Flags==q.Flags&&i.Quote==q.Quote;
    void EndShopInquiry(string id,string status,string message){
        if(!shopInquiries.Remove(id,out var q))return;
        object answer=new{type="shopAnswer",host=q.Owner,id=q.Id,item=q.Item,status,message};
        FindPlayer(q.Owner)?.Client?.Send(answer);FindPlayer(q.Visitor)?.Client?.Send(answer);
    }
    void ExpireShopInquiries(DateTime now){foreach(var q in shopInquiries.Values.Where(q=>q.Expires<=now).ToArray())EndShopInquiry(q.Id,"expired","这次问价已结束。");}
    void PlaceNewVisitor(string id,string host){
        var used=visitorPlaces.Values.Where(p=>p.Host==host).Select(p=>p.Slot).ToHashSet();
        int slot=Enumerable.Range(0,4).FirstOrDefault(n=>!used.Contains(n));long arrival=sharedShops.TryGetValue(host,out var shop)?++shop.NextArrival:0;visitorPlaces[id]=new VisitorPlace(host,slot,arrival);
        if(sharedShops.TryGetValue(host,out var state))state.Presence++;
    }
    void SetAppearance(Session s,Command c){
        Need(s.AccountId!=null&&accounts!=null,"account_required","登录账号后可选择玩家形象。");
        Need(VisitLooks.Style(c.Avatar),"bad_appearance","请选择列表里的玩家形象。");
        accounts!.SetAppearance(s.AccountId!,c.Avatar);s.Avatar=c.Avatar;
        string host=visiting.TryGetValue(s.Id,out var h)?h:s.Id;
        if(sharedShops.TryGetValue(host,out var state)){state.Presence++;BroadcastSharedShop(host);}
        Audit("appearance_changed",s);BroadcastLobby();
    }
    void DropSharedShop(string owner){
        sharedShops.Remove(owner);
        foreach(var q in shopInquiries.Values.Where(q=>q.Owner==owner).ToArray())EndShopInquiry(q.Id,"closed","店主已结束接待。");
        foreach(var k in shopActions.Keys.Where(k=>k.Item1==owner).ToArray())shopActions.Remove(k);
    }
    void ClearVisitorActions(string id){
        if(visitorPlaces.Remove(id,out var p)&&sharedShops.TryGetValue(p.Host,out var state))state.Presence++;
        foreach(var shop in sharedShops.Values)foreach(var lot in shop.CounterLots.Values.Where(l=>l.Visitor==id).ToArray())if(shop.CounterLots.Remove(lot.Item.Id))shop.CounterRevision++;
        foreach(var q in shopInquiries.Values.Where(q=>q.Visitor==id).ToArray())EndShopInquiry(q.Id,"left","对方已离店，这次问价结束。");
        foreach(var k in shopActions.Keys.Where(k=>k.Item1==id).ToArray())shopActions.Remove(k);
    }
    void EndShopPresence(Session s){
        LeaveVisit(s);shops.Remove(s.Id);DropSharedShop(s.Id);
        foreach(string id in visiting.Where(v=>v.Value==s.Id).Select(v=>v.Key).ToArray())if(FindPlayer(id) is {} guest)LeaveVisit(guest);
    }
}
