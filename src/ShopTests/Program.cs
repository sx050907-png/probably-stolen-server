using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightStud.Server;
using NightStud.Shared;

int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Bad(Action action,string code){try{action();throw new Exception("accepted "+code);}catch(WireFault e){Check(e.Code==code,code+" got "+e.Code);}}
var root=Path.GetFullPath(args.ElementAtOrDefault(0)??"work/shop060-tests");Directory.CreateDirectory(root);
var fixture=JsonSerializer.Deserialize<ShopScene>(File.ReadAllText(args.ElementAtOrDefault(1)??"work/shop-native-scene.json"),Wire.Json)!;
using var nativeOffer=JsonDocument.Parse(File.ReadAllText(args.ElementAtOrDefault(2)??"work/shop-native-offer.json"));
string payload=nativeOffer.RootElement.GetProperty("payload").GetString()!;
ShopScene Copy()=>JsonSerializer.Deserialize<ShopScene>(Wire.Encode(fixture),Wire.Json)!;
var scene=Copy();scene.Generation=Guid.NewGuid().ToString("N");scene.DoorOpen=false;
string offeredKind=JsonNode.Parse(payload)!["nodes"]![0]!["identifier"]!.GetValue<string>();
string lot=scene.Items.First(i=>i.Kind==offeredKind).Id;
Check(RoomHub.ValidateShopScene(Wire.Encode(scene)).Layers.Length>0,"actual native scene accepted");
void Invalid(Action<ShopScene> change){var s=Copy();change(s);Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(s)),"bad_scene");}
Invalid(s=>s.Generation="../../evil");Invalid(s=>s.Layers=[]);Invalid(s=>s.Layers[0].Art="https://evil.example/a.png");
Invalid(s=>s.Layers[0].Order=999);Invalid(s=>s.Layers[0].Color=-1);Invalid(s=>s.Layers[0].W=1000000);
Invalid(s=>s.Items[0].Kind="lou_locker");Invalid(s=>s.Items[0].Zone="private");Invalid(s=>s.Items[0].Count=0);
Invalid(s=>s.Items[0].Count=1001);Invalid(s=>s.Items[0].Quote=0);Invalid(s=>s.Items[0].Flags=8);
Invalid(s=>s.Items=[s.Items[0],s.Items[0]]);Invalid(s=>s.Items[0].Id="guessed");Invalid(s=>s.Items[0].X=99999);
Invalid(s=>s.Zones=null!);Invalid(s=>s.Zones=[new ShopZone{Id="private",W=50,H=50}]);
Invalid(s=>s.Zones=[new ShopZone{Id="showcase",W=50,H=50},new ShopZone{Id="showcase",W=50,H=50}]);
Invalid(s=>s.Zones=[new ShopZone{Id="showcase",X=950,Y=0,W=50,H=50}]);
Invalid(s=>s.Zones=[new ShopZone{Id="showcase",X=0,Y=0,W=50,H=50}]);
var legacyScene=Copy();legacyScene.Zones=[];Check(RoomHub.ValidateShopScene(Wire.Encode(legacyScene)).Zones.Length==0,"legacy client may omit public-zone rectangles");
foreach(var zone in fixture.Zones){
    var target=new ShopZone{Id=zone.Id,X=75,Y=326,W=360,H=80};
    foreach(var item in fixture.Items.Where(i=>i.Zone==zone.Id)){
        var placed=ShopProjection.Place(item,zone,target);
        Check(Math.Abs(placed.W/placed.H-(double)item.W/item.H)<.00001,"reverse-view projection preserves each good's aspect ratio");
        Check(placed.X-placed.W*.5>=target.X-1&&placed.Y-placed.H*.5>=target.Y-1&&placed.X+placed.W*.5<=target.X+target.W+1&&placed.Y+placed.H*.5<=target.Y+target.H+1,"reverse-view goods remain inside public surface");
    }
}
Bad(()=>RoomHub.ValidateShopScene("null"),"bad_scene");Bad(()=>RoomHub.ValidateShopScene("{"),"bad_scene");
Bad(()=>RoomHub.ValidateShopScene(new string(' ',48001)),"bad_scene");
Bad(()=>RoomHub.ValidateShopScene("{\"$type\":\"System.Diagnostics.Process\"}"),"bad_scene");
var accountPath=Path.Combine(root,Guid.NewGuid().ToString("N"));var accounts=new Accounts(accountPath);
var hub=new RoomHub(3,null,accounts);
async Task<Peer> Make(string name,bool shared=true,bool reception=false){var id=await accounts.Register(name,"test-password-long-9402",name);return new Peer(hub,hub.LoginAccount(id),shared,reception);}
await using var owner=await Make("owner");await using var visitor=await Make("visitor");await using var outsider=await Make("outsider");
await using var legacy=await Make("legacy",false);await using var anonymous=new Peer(hub,hub.Login("anonymous"),true);
async Task Ok(Peer p,string type,Action<Command>? change=null){var a=await p.Command(type,change);Check(a.GetProperty("ok").GetBoolean(),type+": "+a);}
async Task No(Peer p,string type,string error,Action<Command>? change=null){var a=await p.Command(type,change);Check(!a.GetProperty("ok").GetBoolean()&&a.GetProperty("error").GetString()==error,type+": expected "+error+", got "+a);}
async Task Publish(Peer p,ShopScene state,string name="测试当铺"){await Task.Delay(800);await Ok(p,"shopScene",c=>{c.Payload=Wire.Encode(state);c.Name=name;});}
JsonElement Person(Peer p,string id)=>p.Last("shopScene").GetProperty("people").EnumerateArray().Single(v=>v.GetProperty("id").GetString()==id);
long Rev(Peer p)=>p.Last("shopScene").GetProperty("revision").GetInt64();
await No(anonymous,"appearance","account_required",c=>c.Avatar="hood_ash");
await No(owner,"shopScene","visits_disabled",c=>{c.Payload=Wire.Encode(scene);c.Name="店铺";});
await Ok(owner,"privacy",c=>{c.AllowVisits=true;c.AllowInvites=true;});
await Publish(owner,scene);
await No(owner,"shopScene","shop_rate",c=>{c.Payload=Wire.Encode(scene);c.Name="店铺";});
await No(legacy,"visit","client_update",c=>c.Target=owner.Session.Id);
await No(anonymous,"visit","account_required",c=>c.Target=owner.Session.Id);
await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);await owner.Flush();
await No(visitor,"chat","chat_scope",c=>{c.Text="店内私聊";c.Channel="lobby";});
await Ok(visitor,"chat",c=>{c.Text="老板，这件怎么卖？";c.Channel="visit:"+owner.Session.Id;});await owner.Flush();
Check(owner.Last("chat").GetProperty("channel").GetString()=="visit:"+owner.Session.Id,"visit chat remains in its requested shop");
Check(Person(owner,visitor.Session.Id).GetProperty("place").GetString()=="counter","new visitor stays outside counter");
Check(Person(owner,visitor.Session.Id).GetProperty("avatar").GetString()=="hood_ash","server assigns appearance");
await No(visitor,"shopEnter","door_closed",c=>c.SceneRevision=Rev(visitor));
await No(outsider,"shopInspect","visit_unavailable",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});
await No(visitor,"shopKick","visit_scope",c=>c.Target=owner.Session.Id);
await No(owner,"shopEnter","visit_scope",c=>c.SceneRevision=Rev(owner));
scene.DoorOpen=true;await Publish(owner,scene);
await No(visitor,"shopEnter","scene_changed",c=>c.SceneRevision=Rev(visitor)-1);
await Ok(visitor,"shopEnter",c=>c.SceneRevision=Rev(visitor));await owner.Flush();
Check(Person(owner,visitor.Session.Id).GetProperty("place").GetString()=="inside","open door admits visitor");
Check(Person(owner,visitor.Session.Id).GetProperty("facing").GetString()=="front_three_quarter","admitted customer faces shop owner across counter");
await No(visitor,"shopLook","shop_action_rate",c=>{c.SceneRevision=Rev(visitor);c.Facing="back";});await Task.Delay(300);
await No(visitor,"shopLook","bad_facing",c=>{c.SceneRevision=Rev(visitor);c.Facing="http://evil";});await Task.Delay(300);
await Ok(visitor,"shopLook",c=>{c.SceneRevision=Rev(visitor);c.Facing="back";});await owner.Flush();
Check(Person(owner,visitor.Session.Id).GetProperty("facing").GetString()=="back","same authoritative pose reaches host");
scene.DoorOpen=false;await Publish(owner,scene);await visitor.Flush();
Check(Person(visitor,visitor.Session.Id).GetProperty("place").GetString()=="counter","closing door returns guest outside");
Check(Person(visitor,visitor.Session.Id).GetProperty("facing").GetString()=="front","closing door resets counter-facing pose");
await No(visitor,"shopInspect","scene_changed",c=>{c.Item=lot;c.SceneRevision=Rev(visitor)-1;});
await No(visitor,"shopInspect","item_changed",c=>{c.Item=Guid.NewGuid().ToString("N");c.SceneRevision=Rev(visitor);});
await Ok(visitor,"shopInspect",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});await owner.Flush();
Check(owner.Last("shopFocus").GetProperty("item").GetString()==lot,"inspect targets actual public lot");
Check(Person(owner,visitor.Session.Id).GetProperty("facing").GetString()=="side_left","showcase inspection turns guest sideways");
await Ok(visitor,"shopAsk",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});await owner.Flush();
var q=owner.Last("shopInquiry");string inquiry=q.GetProperty("id").GetString()!;
Check(q.GetProperty("visitor").GetString()==visitor.Session.Id,"inquiry bound to visitor identity");
Check(Person(owner,visitor.Session.Id).GetProperty("facing").GetString()=="front_three_quarter","asking price turns speaker back toward owner");
await No(outsider,"shopAnswer","visit_unavailable",c=>c.Inquiry=inquiry);
var wrong=JsonNode.Parse(payload)!;wrong["nodes"]![0]!["unitCount"]=2;
var protectedLot=JsonNode.Parse(payload)!;protectedLot["nodes"]![0]!["identifier"]="lou_locker";
await No(owner,"tradeOffer","protected_lot",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Target=visitor.Session.Id;c.Price=14;c.Payload=protectedLot.ToJsonString();});
await No(owner,"tradeOffer","item_changed",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Inquiry=inquiry;c.Target=visitor.Session.Id;c.Price=14;c.Payload=wrong.ToJsonString();});
string trade=Guid.NewGuid().ToString("N");
await Ok(owner,"tradeOffer",c=>{c.Trade=trade;c.Inquiry=inquiry;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
Check(visitor.Last("trade").GetProperty("state").GetString()=="proposed","native fixture quoted through bound inquiry");
Check(owner.Last("shopAnswer").GetProperty("status").GetString()=="quoted","quoted inquiry closes on host");
await No(owner,"tradeOffer","inquiry_expired",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Inquiry=inquiry;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
await Ok(owner,"tradeOffer",c=>{c.Trade=trade;c.Inquiry=inquiry;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
Check(visitor.Last("trade").GetProperty("id").GetString()==trade,"same saved quote can be recovered after inquiry consumed");
await Ok(visitor,"tradeCancel",c=>c.Trade=trade);
await owner.Flush();Check(owner.Last("trade").GetProperty("state").GetString()=="cancelled","buyer cancellation immediately reaches seller");
scene.Items.Single(i=>i.Id==lot).Quote++;
await Publish(owner,scene);
await No(owner,"tradeOffer","inquiry_expired",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Inquiry=inquiry;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
await Publish(owner,scene,"新店名");await visitor.Flush();
Check(visitor.Last("shopScene").GetProperty("shopName").GetString()=="新店名","rename propagated even when scene unchanged");
await No(owner,"shop","client_update",c=>{c.Name="旧截图";c.Payload="";});
await No(visitor,"appearance","bad_appearance",c=>c.Avatar="../../evil");
await Ok(visitor,"appearance",c=>c.Avatar="hood_olive");await owner.Flush();
Check(Person(owner,visitor.Session.Id).GetProperty("avatar").GetString()=="hood_olive","chosen style reaches other participants");
Check(visitor.Last("lobby").GetProperty("selfAppearance").GetString()=="hood_olive","directory reports own saved style");
Check(new Accounts(accountPath).GetAppearance(visitor.Session.Id)=="hood_olive","appearance survives database reopen");
await using var third=await Make("third");await using var fourth=await Make("fourth");await using var fifth=await Make("fifth");
foreach(var p in new[]{third,fourth,fifth})await Ok(p,"visit",c=>c.Target=owner.Session.Id);
await owner.Flush();var people=owner.Last("shopScene").GetProperty("people").EnumerateArray().Where(p=>!p.GetProperty("owner").GetBoolean()).ToArray();
Check(people.Length==4&&people.Select(p=>p.GetProperty("slot").GetInt32()).Distinct().Count()==4,"four guests have separate stable slots");
await No(outsider,"visit","visit_full",c=>c.Target=owner.Session.Id);
await Ok(owner,"shopKick",c=>c.Target=third.Session.Id);await third.Flush();
Check(third.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"host can remove actual guest");
await No(third,"shopEnter","visit_unavailable",c=>c.SceneRevision=Rev(owner));
await Ok(outsider,"visit",c=>c.Target=owner.Session.Id);await owner.Flush();
Check(Person(owner,outsider.Session.Id).GetProperty("slot").GetInt32()==1,"vacated position reused without stacking");
await Ok(owner,"privacy",c=>c.AllowVisits=false);foreach(var p in new[]{visitor,fourth,fifth,outsider}){await p.Flush();Check(p.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"privacy closes visit for "+p.Session.Name);}
await No(visitor,"shopLook","visit_unavailable",c=>{c.Facing="back";c.SceneRevision=1;});
await No(owner,"chat","chat_scope",c=>{c.Text="继续聊";c.Channel="visit:"+owner.Session.Id;});
await Ok(owner,"privacy",c=>c.AllowVisits=true);await Publish(owner,scene);await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);
scene.Generation=Guid.NewGuid().ToString("N");await Publish(owner,scene);await visitor.Flush();
Check(visitor.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"loading another save ends old visit");
await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);await Ok(owner,"create",c=>c.Public=false);await visitor.Flush();
Check(visitor.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"host starting table releases guests");
await Ok(owner,"leave");await Publish(owner,scene);await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);
await Ok(outsider,"privacy",c=>c.AllowVisits=true);await Publish(outsider,scene);
await Ok(owner,"visit",c=>c.Target=outsider.Session.Id);await visitor.Flush();
Check(visitor.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"host visiting elsewhere ends own reception");
await No(visitor,"visit","visits_disabled",c=>c.Target=owner.Session.Id);
await Ok(owner,"leaveVisit");await Publish(owner,scene);await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);
// Independent concurrent inquiries must not be consumed or cancelled together.
await Ok(third,"visit",c=>c.Target=owner.Session.Id);await owner.Flush();
string secondLot=scene.Items.First(i=>i.Id!=lot).Id;
scene.CounterBusy=true;await Publish(owner,scene);await visitor.Flush();
Check(visitor.Last("shopScene").GetProperty("scene").GetProperty("counterBusy").GetBoolean(),"native business occupancy reaches guest scene");
await No(visitor,"shopAsk","counter_busy",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});
await Ok(visitor,"shopInspect",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});
scene.CounterBusy=false;await Publish(owner,scene);
await Ok(visitor,"shopAsk",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});await owner.Flush();
string askA=owner.Last("shopInquiry").GetProperty("id").GetString()!;
await Ok(third,"shopAsk",c=>{c.Item=secondLot;c.SceneRevision=Rev(third);});await owner.Flush();
string askB=owner.Last("shopInquiry").GetProperty("id").GetString()!;
await No(outsider,"shopAnswer","inquiry_expired",c=>c.Inquiry=askA);
await Ok(owner,"shopAnswer",c=>c.Inquiry=askA);await visitor.Flush();await third.Flush();
Check(visitor.Last("shopAnswer").GetProperty("id").GetString()==askA&&visitor.Last("shopAnswer").GetProperty("status").GetString()=="declined","only asking guest gets declined inquiry");
Check(third.Count("shopAnswer",askA)==0,"other guest does not receive somebody else's answer");
scene.Items.Single(i=>i.Id==secondLot).Quote++;await Publish(owner,scene);await third.Flush();
Check(third.Last("shopAnswer").GetProperty("id").GetString()==askB&&third.Last("shopAnswer").GetProperty("status").GetString()=="changed","changed lot closes its own inquiry");
await No(owner,"tradeOffer","inquiry_expired",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Inquiry=askA;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
await Task.Delay(3100);
await Ok(visitor,"shopAsk",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});await owner.Flush();
string expiring=owner.Last("shopInquiry").GetProperty("id").GetString()!;
typeof(RoomHub).GetMethod("ExpireShopInquiries",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(hub,[DateTime.UtcNow.AddSeconds(46)]);
await owner.Flush();await visitor.Flush();
Check(owner.Last("shopAnswer").GetProperty("id").GetString()==expiring&&owner.Last("shopAnswer").GetProperty("status").GetString()=="expired","expiry closes visible host inquiry without republishing scene");
Check(visitor.Last("shopAnswer").GetProperty("status").GetString()=="expired","expiry releases guest's pending price request");
await Task.Delay(3100);await Ok(visitor,"shopAsk",c=>{c.Item=lot;c.SceneRevision=Rev(visitor);});await owner.Flush();
string interrupted=owner.Last("shopInquiry").GetProperty("id").GetString()!;
scene.CounterBusy=true;await Publish(owner,scene);await visitor.Flush();
Check(visitor.Last("shopAnswer").GetProperty("id").GetString()==interrupted&&visitor.Last("shopAnswer").GetProperty("status").GetString()=="busy","business arrival closes pending inquiry instead of leaving it hanging");
await No(owner,"tradeOffer","inquiry_expired",c=>{c.Trade=Guid.NewGuid().ToString("N");c.Inquiry=interrupted;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
scene.CounterBusy=false;await Publish(owner,scene);
string pending=Guid.NewGuid().ToString("N"),kept=Guid.NewGuid().ToString("N"),finished=Guid.NewGuid().ToString("N");
async Task RawQuote(string id,Peer buyer)=>await Ok(owner,"tradeOffer",c=>{c.Trade=id;c.Target=buyer.Session.Id;c.Price=14;c.Payload=payload;});
await RawQuote(pending,visitor);await RawQuote(kept,third);await RawQuote(finished,visitor);
await Ok(visitor,"tradeAccept",c=>c.Trade=finished);
await No(outsider,"tradeCancel","trade_unavailable",c=>c.Trade=pending);
await Ok(visitor,"leaveVisit");await owner.Flush();await visitor.Flush();
Check(owner.Trade(pending).GetProperty("state").GetString()=="cancelled"&&visitor.Trade(pending).GetProperty("state").GetString()=="cancelled","leaving cancels unfinished quote and notifies both original parties");
Check(owner.Trade(kept).GetProperty("state").GetString()=="proposed","leaving guest does not cancel another guest's trade");
await Ok(visitor,"tradeReceipt",c=>c.Trade=finished);
Check(visitor.Trade(finished).GetProperty("state").GetString()=="committed","completed sale survives leaving shop");
await Ok(visitor,"tradeReceipt",c=>c.Trade=pending);
Check(visitor.Trade(pending).EnumerateObject().Count()==3,"cancelled receipt has no identity or item payload");
await Ok(owner,"tradeOffer",c=>{c.Trade=pending;c.Target=visitor.Session.Id;c.Price=14;c.Payload=payload;});
Check(owner.Trade(pending).GetProperty("state").GetString()=="cancelled","delayed original offer cannot revive cancelled visit sale");
await Ok(visitor,"visit",c=>c.Target=owner.Session.Id);
hub.Logout(owner.Session.Token);await visitor.Flush();
Check(visitor.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"host logout releases guests immediately");
await No(visitor,"shopEnter","visit_unavailable",c=>c.SceneRevision=1);
await ReceptionChecks();
await IncomingTradeChecks();
await AppearanceChecks();
async Task AppearanceChecks(){
    await using var h=await Make("look_owner",reception:true);await using var v=await Make("look_visitor",reception:true);
    var state=Copy();state.Generation=Guid.NewGuid().ToString("N");state.CounterBusy=false;
    await Ok(h,"privacy",c=>c.AllowVisits=true);await Publish(h,state);await Ok(v,"visit",c=>c.Target=h.Session.Id);await h.Flush();
    var frames=new List<object>();
    foreach(string style in VisitLooks.Styles){
        await Task.Delay(270);await Ok(v,"shopLook",c=>{c.SceneRevision=Rev(v);c.Facing="front_three_quarter";});
        await Ok(v,"appearance",c=>c.Avatar=style);await h.Flush();var visitor=h.Last("shopScene").Clone();
        Check(Person(h,v.Session.Id).GetProperty("avatar").GetString()==style,"chosen visitor outfit reaches actual owner scene: "+style);
        Check(new Accounts(accountPath).GetAppearance(v.Session.Id)==style,"visitor outfit persists after DB reopen: "+style);
        await Task.Delay(270);await Ok(v,"shopLook",c=>{c.SceneRevision=Rev(v);c.Facing="side_left";});await h.Flush();var side=h.Last("shopScene").Clone();
        await Ok(h,"appearance",c=>c.Avatar=style);await v.Flush();var owner=v.Last("shopScene").Clone();var lobby=h.Last("lobby").Clone();
        Check(Person(v,h.Session.Id).GetProperty("avatar").GetString()==style,"chosen owner outfit reaches actual visitor scene: "+style);
        Check(lobby.GetProperty("selfAppearance").GetString()==style&&new Accounts(accountPath).GetAppearance(h.Session.Id)==style,"own directory and persisted owner outfit agree: "+style);
        frames.Add(new{style,visitor,side,owner,lobby});
    }
    var fixture=new{host=h.Session.Id,guest=v.Session.Id,frames};File.WriteAllText(Path.Combine(root,"appearance-native.json"),Wire.Encode(fixture));File.WriteAllText(Path.GetFullPath("work/shop-appearance-native.json"),Wire.Encode(fixture));
}
async Task IncomingTradeChecks(){
    await using var h=await Make("trade_host",reception:true);await using var a=await Make("trade_guest",reception:true);await using var b=await Make("trade_waiting",reception:true);
    var state=Copy();state.Generation=Guid.NewGuid().ToString("N");state.CounterBusy=false;
    await Ok(h,"privacy",c=>c.AllowVisits=true);await Publish(h,state);await Ok(a,"visit",c=>c.Target=h.Session.Id);await Ok(b,"visit",c=>c.Target=h.Session.Id);await h.Flush();
    var nativeScene=h.Last("shopScene").Clone();
    string sale=Guid.NewGuid().ToString("N");await Ok(a,"tradeOffer",c=>{c.Trade=sale;c.Target=h.Session.Id;c.Price=31;c.Payload=payload;});await h.Flush();
    var offer=h.Trade(sale).Clone();Check(offer.GetProperty("seller").GetString()==a.Session.Id&&offer.GetProperty("buyer").GetString()==h.Session.Id,"served visitor offers native physical lot to shop owner");
    await Ok(h,"tradeAccept",c=>c.Trade=sale);await a.Flush();var committed=h.Trade(sale).Clone();
    Check(a.Trade(sale).GetProperty("state").GetString()=="committed","owner purchase confirms sale to visitor");
    string pending=Guid.NewGuid().ToString("N");await Ok(b,"tradeOffer",c=>{c.Trade=pending;c.Target=h.Session.Id;c.Price=17;c.Payload=payload;});
    await No(h,"tradeAccept","visitor_waiting",c=>c.Trade=pending);
    await Task.Delay(320);await Ok(h,"shopServe",c=>{var f=h.Last("shopScene");c.Target=b.Session.Id;c.SceneRevision=f.GetProperty("revision").GetInt64();c.ShopPresence=f.GetProperty("presence").GetInt64();c.ShopEpoch=f.GetProperty("epoch").GetString()!;});
    await Ok(h,"tradeAccept",c=>c.Trade=pending);await Ok(h,"tradeAccept",c=>c.Trade=sale);
    Check(h.Trade(sale).GetProperty("state").GetString()=="committed","committed visitor sale can recover after active guest changes");
    string outgoing=Guid.NewGuid().ToString("N");await Ok(h,"tradeOffer",c=>{c.Trade=outgoing;c.Target=a.Session.Id;c.Price=13;c.Payload=payload;});
    await No(a,"tradeAccept","visitor_waiting",c=>c.Trade=outgoing);await Ok(h,"tradeCancel",c=>c.Trade=outgoing);
    state.CounterBusy=true;await Publish(h,state);var busyScene=h.Last("shopScene").Clone();string paused=Guid.NewGuid().ToString("N");await Ok(b,"tradeOffer",c=>{c.Trade=paused;c.Target=h.Session.Id;c.Price=9;c.Payload=payload;});await h.Flush();
    var cancellationOffer=h.Trade(paused).Clone();await No(h,"tradeAccept","counter_busy",c=>c.Trade=paused);
    await Ok(h,"tradeCancel",c=>c.Trade=paused);await b.Flush();var cancelled=h.Trade(paused).Clone();
    Check(b.Trade(paused).GetProperty("state").GetString()=="cancelled","owner decline returns cancellation to visitor seller");
    state.CounterBusy=false;await Publish(h,state);var resumedScene=h.Last("shopScene").Clone();
    var fixture=new{scene=nativeScene,offer,committed,busyScene,resumedScene,cancellationOffer,cancelled};File.WriteAllText(Path.Combine(root,"native-trade-flow.json"),Wire.Encode(fixture));File.WriteAllText(Path.GetFullPath("work/shop-trade-native.json"),Wire.Encode(fixture));
}
async Task ReceptionChecks(){
    await using var h=await Make("reception_host",reception:true);await using var a=await Make("reception_a",reception:true);
    await using var b=await Make("reception_b",reception:true);await using var c=await Make("reception_c",reception:true);
    await using var d=await Make("reception_d",reception:true);await using var other=await Make("reception_other",reception:true);
    var room=Copy();room.Generation=Guid.NewGuid().ToString("N");room.CounterBusy=false;
    string Serving(Peer p)=>p.Last("shopScene").GetProperty("serving").GetString()!;
    long Presence(Peer p)=>p.Last("shopScene").GetProperty("presence").GetInt64();
    void Select(Command cmd,Peer owner,string target){var f=owner.Last("shopScene");cmd.Target=target;cmd.SceneRevision=f.GetProperty("revision").GetInt64();cmd.ShopPresence=f.GetProperty("presence").GetInt64();cmd.ShopEpoch=f.GetProperty("epoch").GetString()!;}
    async Task Serve(Peer guest){await Task.Delay(320);await Ok(h,"shopServe",cmd=>Select(cmd,h,guest.Session.Id));await guest.Flush();}
    await Ok(h,"privacy",cmd=>cmd.AllowVisits=true);await Publish(h,room);
    Check(h.Last("shopScene").GetProperty("reception").GetBoolean(),"host opted into authoritative reception");
    foreach(var p in new[]{a,b,c})await Ok(p,"visit",cmd=>cmd.Target=h.Session.Id);
    await h.Flush();Check(Serving(h)==a.Session.Id&&Serving(c)==a.Session.Id,"oldest visitor serves first for host and every guest");
    Check(Person(b,b.Session.Id).GetProperty("queuePosition").GetInt32()==1&&Person(c,c.Session.Id).GetProperty("queuePosition").GetInt32()==2,"waiting positions ordered by arrival");
    Check(h.Last("shopScene").GetProperty("people").EnumerateArray().Count(p=>p.GetProperty("serving").GetBoolean())==1,"one serving visitor in authoritative scene");
    await No(a,"shopServe","visit_scope",cmd=>Select(cmd,h,a.Session.Id));
    await No(h,"shopServe","scene_changed",cmd=>{Select(cmd,h,b.Session.Id);cmd.ShopEpoch=Guid.NewGuid().ToString("N");});
    await No(h,"shopServe","visit_scope",cmd=>Select(cmd,h,Guid.NewGuid().ToString("N")));
    long stalePresence=Presence(h);await Serve(b);var before=h.Last("shopScene").Clone();
    Check(Serving(a)==b.Session.Id&&Serving(h)==b.Session.Id,"owner switch reaches other guests");
    await No(h,"shopServe","scene_changed",cmd=>{Select(cmd,h,c.Session.Id);cmd.ShopPresence=stalePresence;});
    Check(Serving(h)==b.Session.Id,"delayed old click cannot replace confirmed visitor");
    long same=Presence(h);await Serve(b);Check(Presence(h)==same,"same visitor selection does not advance presence twice");
    string viewed=room.Items.Single(i=>i.Kind==offeredKind).Id;
    await Ok(c,"shopAsk",cmd=>{cmd.Item=viewed;cmd.SceneRevision=Rev(c);});await h.Flush();
    Check(c.Last("shopAsked").GetProperty("message").GetString()=="问价已排队，等店主接待。","queued guest receives waiting status instead of active counter promise");
    string request=h.Last("shopInquiry").GetProperty("id").GetString()!;
    await No(h,"tradeOffer","visitor_waiting",cmd=>{cmd.Trade=Guid.NewGuid().ToString("N");cmd.Inquiry=request;cmd.Target=c.Session.Id;cmd.Price=14;cmd.Payload=payload;});
    before=h.Last("shopScene").Clone();await Serve(c);var selectCommand=h.Commands.Last(cmd=>cmd.Type=="shopServe");var after=h.Last("shopScene").Clone();
    Check(Serving(b)==c.Session.Id&&Person(a,c.Session.Id).GetProperty("serving").GetBoolean(),"served identity and public person status agree");
    string sale=Guid.NewGuid().ToString("N");await Ok(h,"tradeOffer",cmd=>{cmd.Trade=sale;cmd.Inquiry=request;cmd.Target=c.Session.Id;cmd.Price=14;cmd.Payload=payload;});
    Check(c.Trade(sale).GetProperty("state").GetString()=="proposed","confirmed serving visitor can receive bound quote");await Ok(c,"tradeCancel",cmd=>cmd.Trade=sale);
    room.CounterBusy=true;await Publish(h,room);var busy=h.Last("shopScene").Clone();
    Check(Serving(a)==""&&Serving(h)==""&&h.Last("shopScene").GetProperty("nextVisitor").GetString()==c.Session.Id,"native customer reserves whole window, next guest retained");
    Check(h.Last("shopScene").GetProperty("people").EnumerateArray().All(p=>!p.GetProperty("serving").GetBoolean()),"no served guest while native business is active");
    await Serve(a);Check(Serving(h)==""&&h.Last("shopScene").GetProperty("nextVisitor").GetString()==a.Session.Id,"owner may choose next guest without interrupting native business");
    room.CounterBusy=false;await Publish(h,room);var resumed=h.Last("shopScene").Clone();Check(Serving(h)==a.Session.Id,"chosen next guest takes window after native business");
    await Ok(a,"leaveVisit");await b.Flush();Check(Serving(h)==b.Session.Id,"leaving current guest advances oldest remaining guest");
    await Ok(d,"visit",cmd=>cmd.Target=h.Session.Id);await c.Flush();
    Check(Person(h,d.Session.Id).GetProperty("slot").GetInt32()==0&&Serving(h)==b.Session.Id,"reusing empty position does not jump arrival queue");
    Check(Person(h,c.Session.Id).GetProperty("queuePosition").GetInt32()==1&&Person(h,d.Session.Id).GetProperty("queuePosition").GetInt32()==2,"older waiting guest remains ahead of reused position");
    b.Disconnect();hub.Expire();await c.Flush();Check(Serving(h)==c.Session.Id,"disconnected guest releases counter through server expiry");
    await Ok(a,"visit",cmd=>cmd.Target=h.Session.Id);await h.Flush();Check(Serving(h)==c.Session.Id,"returning guest joins queue without displacing current visitor");
    await Ok(other,"privacy",cmd=>cmd.AllowVisits=true);await Publish(other,room);
    await No(other,"shopServe","visit_scope",cmd=>Select(cmd,other,d.Session.Id));
    var epoch=h.Last("shopScene").GetProperty("epoch").GetString();
    await Ok(h,"privacy",cmd=>cmd.AllowVisits=false);await a.Flush();await c.Flush();await d.Flush();
    Check(new[]{a,c,d}.All(p=>p.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null),"ending reception clears all waiting and served guests");
    await Ok(h,"privacy",cmd=>cmd.AllowVisits=true);await Publish(h,room);await Ok(c,"visit",cmd=>cmd.Target=h.Session.Id);
    await No(h,"shopServe","scene_changed",cmd=>{Select(cmd,h,c.Session.Id);cmd.ShopEpoch=epoch!;});
    Check(Serving(h)==c.Session.Id,"old reception epoch cannot alter reopened shop");
    File.WriteAllText(Path.Combine(root,"../shop-reception-native.json"),Wire.Encode(new{before,after,busy,resumed,command=selectCommand}));
}
var success=$"PASS {checks} assertions: actual native scene validation; legacy shared-shop compatibility; authoritative served visitor and arrival queue, owner-only switch, stale presence/epoch rejection, native business exclusivity, queued quote protection, departure/disconnect/revisit fairness; asset/catalogue/geometry/quantity limits; account/privacy/capability scope; closed/open/stale door entry; inquiry isolation and closure; quote retry, two-party cancellation, committed/unrelated sale preservation; appearance and lifecycle cleanup. In-memory WebSocket peers only; no TCP listener, real campaigns or Steam approval.";
File.WriteAllText(Path.Combine(root,"success.txt"),success);Console.WriteLine(success);

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Socket socket=new();readonly Client client;readonly Task pump;readonly CancellationTokenSource stop=new();int commands;public Session Session;
 public Peer(RoomHub hub,Session session,bool shared,bool reception=false){this.hub=hub;Session=session;client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=session.Token,Games=["stud","three","holdem","blackjack","landlord","mahjong","dice"],Capabilities=shared?reception?["sharedShop","shopReception"]:["sharedShop"]:[]},client);}
 public readonly List<Command> Commands=[];
 public void Disconnect()=>hub.Detach(Session,client);
 public JsonElement Last(string kind)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==kind);
 public JsonElement Trade(string id)=>socket.Frames.Last(f=>f.GetProperty("type").GetString()=="trade"&&f.GetProperty("id").GetString()==id);
 public int Count(string kind,string id)=>socket.Frames.Count(f=>f.GetProperty("type").GetString()==kind&&f.TryGetProperty("id",out var value)&&value.GetString()==id);
 public Task Flush()=>Task.Delay(60);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){if(++commands%12==0)await Task.Delay(1050);var cmd=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};var state=Last("state");if(state.ValueKind==JsonValueKind.Object&&state.GetProperty("room").ValueKind==JsonValueKind.Object)cmd.Revision=state.GetProperty("room").GetProperty("revision").GetInt64();configure?.Invoke(cmd);Commands.Add(cmd);hub.Handle(Session,client,cmd);for(int i=0;i<100;i++){await Task.Delay(10);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==cmd.Id){await Flush();return ack;}}throw new Exception("missing ack "+type);}
 public async ValueTask DisposeAsync(){hub.Detach(Session,client);client.Stop();stop.Cancel();await pump;stop.Dispose();}
}
sealed class Socket:WebSocket {
 public readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Frames=new();WebSocketState state=WebSocketState.Open;
 public override WebSocketState State=>state;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;
 public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
 public override Task CloseAsync(WebSocketCloseStatus status,string? text,CancellationToken token){Abort();return Task.CompletedTask;}
 public override Task CloseOutputAsync(WebSocketCloseStatus status,string? text,CancellationToken token)=>CloseAsync(status,text,token);
 public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token)=>throw new NotSupportedException();
 public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool end,CancellationToken token){using var doc=JsonDocument.Parse(buffer.AsMemory());Frames.Enqueue(doc.RootElement.Clone());return Task.CompletedTask;}
}
