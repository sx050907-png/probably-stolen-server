namespace NightStud.Server;

public sealed partial class RoomHub
{
    readonly Dictionary<string,DateTime> shopBellTimes=new();
    void RingShopBell(Session guest,Command command,string owner,SharedShopState state){
        Need(owner!=guest.Id&&visitorPlaces.TryGetValue(guest.Id,out var place)&&place.Host==owner&&place.Place=="counter",
            "visit_scope","请先到朋友的柜台外。");
        Need(guest.ShopBell&&FindPlayer(owner)?.ShopBell==true,"client_update","双方更新配套联机组件后可请求开门。");
        Need(command.ShopEpoch==state.Epoch&&command.SceneRevision==state.Revision,"scene_changed","店铺状态已更新，请再试一次。");
        Need(!state.Scene.DoorOpen,"door_open","店门已经开了，可以直接进店。");
        var now=DateTime.UtcNow;
        Need(now>=state.NextBell,"shop_bell_wait","已经有人提醒过店主，稍等一会儿。");
        foreach(string expiredGuest in shopBellTimes.Where(p=>(now-p.Value).TotalSeconds>=30).Select(p=>p.Key).ToArray())shopBellTimes.Remove(expiredGuest);
        Need(!shopBellTimes.TryGetValue(guest.Id,out var previous)||(now-previous).TotalSeconds>=30,"shop_action_rate","刚提醒过，稍等一会儿。");
        shopBellTimes[guest.Id]=now;
        state.NextBell=now.AddSeconds(8);
        string id=Guid.NewGuid().ToString("N");
        FindPlayer(owner)!.Client!.Send(new{type="shopBell",id,host=owner,epoch=state.Epoch,visitor=guest.Id,name=guest.Name});
        guest.Client!.Send(new{type="shopBellSent",id,host=owner,epoch=state.Epoch,cooldown=30});
        Audit("shop_door_requested",guest,null,"counter");
    }
}
