namespace NightStud.Server;

public sealed partial class RoomHub {
    readonly MarketLedger? market=accounts==null?null:new(accounts);
    void MarketCommand(Session session,Command command){
        Need(session.AccountId!=null&&market!=null&&cash!=null&&session.StoreStakes,"account_required","登录服务器账号后可使用玩家市场。");
        if(command.Type=="marketBrowse"){
            var rows=market!.Browse(session.Id,command.SaveId,command.Direction,command.Choice??0);
            session.Client!.Send(new{type="market",request=command.Type,saveId=command.SaveId,tab=command.Direction,page=command.Choice??0,more=rows.Length>6,
                available=cash!.Balance(session.Id,command.SaveId),items=rows.Take(6).Select(r=>new{r.Id,r.Seller,r.Buyer,r.Label,r.Kind,r.Price,r.State,r.Created,r.Expires,r.Delivered,name=contacts?.Identity(r.Seller)?.Name??"玩家",number=phones?.Number(r.Seller)??""})});return;
        }
        if(command.Type=="marketPreview"){
            var row=market!.Preview(command.Trade)!;session.Client!.Send(new{type="marketPreview",listing=new{row.Id,row.Seller,row.Label,row.Kind,row.Price,row.State,row.Payload,row.Expires,name=contacts?.Identity(row.Seller)?.Name??"玩家",number=phones?.Number(row.Seller)??""}});return;
        }
        MarketLedger.Listing? receipt;
        if(command.Type=="marketOffer"){
            Need(command.Payload!=null&&command.Payload.Length<=60000,"bad_lot","物品清单无效。");var parcel=ShopTradePayload.Parse(command.Payload);
            foreach(string label in parcel.Labels.Where(s=>s.Length>0))_ = moderation?.Check(session.Id,label);
            FundsRate(session);receipt=market!.Offer(session.Id,command.SaveId,command.Trade,command.Payload,command.Price);
        }else if(command.Type=="marketBuy"){FundsRate(session);receipt=market!.Buy(session.Id,command.SaveId,command.Trade,command.Price);}
        else if(command.Type=="marketCancel"){FundsRate(session);receipt=market!.Cancel(session.Id,command.SaveId,command.Trade);}
        else if(command.Type=="marketDelivered")receipt=market!.Delivered(session.Id,command.SaveId,command.Trade);
        else {Need(command.Type=="marketStatus","unknown","未知市场操作。");receipt=market!.Status(session.Id,command.SaveId,command.Trade,command.Direction=="seller");}
        session.Client!.Send(new{type="market",request=command.Type,saveId=command.SaveId,receipt,available=cash!.Balance(session.Id,command.SaveId)});
        Audit("market_"+command.Type,session);
    }
}
