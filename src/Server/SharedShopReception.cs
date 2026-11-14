namespace NightStud.Server;

public sealed partial class RoomHub
{
    bool ReceptionEnabled(string owner)=>FindPlayer(owner)?.ShopReception==true;
    string[] ArrivalQueue(string owner)=>visitorPlaces.Where(p=>p.Value.Host==owner&&
        visiting.TryGetValue(p.Key,out var host)&&host==owner&&FindPlayer(p.Key) is {} s&&Online(s))
        .OrderBy(p=>p.Value.Arrival).Select(p=>p.Key).ToArray();
    void SyncReception(string owner,SharedShopState state){
        string preferred="",serving="";
        if(ReceptionEnabled(owner)){
            var queue=ArrivalQueue(owner);
            preferred=queue.Contains(state.Preferred)?state.Preferred:queue.FirstOrDefault()??"";
            serving=state.Scene.CounterBusy?"":preferred;
        }
        if(state.Preferred==preferred&&state.Serving==serving)return;
        bool changed=state.Serving!=serving;state.Preferred=preferred;state.Serving=serving;state.Presence++;
        if(changed&&serving.Length>0&&visitorPlaces.TryGetValue(serving,out var speaker))speaker.Facing="front_three_quarter";
    }
    string[] ReceptionQueue(string owner,SharedShopState state)=>ArrivalQueue(owner)
        .OrderBy(id=>id==state.Preferred?0:1).ToArray();
    void CheckTradeReception(string seller,string buyer){
        string owner,visitor;
        if(visiting.TryGetValue(seller,out var host)&&host==buyer){owner=buyer;visitor=seller;}
        else if(visiting.TryGetValue(buyer,out host)&&host==seller){owner=seller;visitor=buyer;}
        else return;
        if(!ReceptionEnabled(owner)||!sharedShops.TryGetValue(owner,out var state))return;
        SyncReception(owner,state);
        Need(!state.Scene.CounterBusy,"counter_busy","店主正在接待顾客，请稍后交易。");
        Need(state.Serving==visitor,"visitor_waiting","请先接待这位访客再成交。");
    }
    void SelectReception(Session owner,Command c,SharedShopState state){
        Need(owner.ShopReception,"client_update","更新配套客户端后可切换接待访客。");
        Need(c.ShopEpoch==state.Epoch&&c.SceneRevision==state.Revision&&c.ShopPresence==state.Presence,
            "scene_changed","访客名单已更新，正在同步。");
        Need(visiting.TryGetValue(c.Target,out var host)&&host==owner.Id&&ArrivalQueue(owner.Id).Contains(c.Target),
            "visit_scope","这位访客已离店。");
        ShopActionRate(owner,"serve",.3);
        bool changed=state.Preferred!=c.Target;long before=state.Presence;
        state.Preferred=c.Target;SyncReception(owner.Id,state);if(changed&&state.Presence==before)state.Presence++;
        BroadcastSharedShop(owner.Id);Audit("visit_served",owner,null,state.Scene.CounterBusy?"queued":"counter");
    }
}
