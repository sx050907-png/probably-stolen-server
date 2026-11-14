namespace NightStud.Server;

public sealed partial class RoomHub
{
    bool AssetsCompatible(Room room)=>room.Economy=="store"&&room.Seats.All(s=>s!=null&&s.Session.AccountId!=null&&s.Session.AssetBets);
    GoodsLedger.Lot? PendingAsset(Room room)=>room.Economy!="store"||goods==null||room.Game==null?null:goods.Table(room.LedgerId,room.Hands).FirstOrDefault(l=>l.BetMove.Length>0&&l.State=="offered");
    Room GoodsRoom(Session session,Command command,bool live=false){
        var room=Revision(session,command);int seat=OwnSeat(room,session);
        Need(room.Economy=="store"&&seat>=0,"goods_room","请进入信用点房间后押物。");
        Need(!room.CashFault,"cash_settling","正在保存本手结算。");
        Need(room.Seats[seat]!.CashHold.Length>0&&room.Seats[seat]!.SaveId==command.SaveId,"need_fund","先用这份经营存档暂存入桌信用点。");
        if(room.Seats.Length>2||live)Need(AssetsCompatible(room),"asset_clients","桌友更新联机组件后可使用这桌的实物押注。");
        if(!live)Need(room.Game is null||room.Game.Done,"goods_playing","牌局中请选择本轮抵注。");return room;
    }
    void ExpireAsset(Room room,DateTime now){
        var pending=PendingAsset(room);if(pending==null||DateTimeOffset.Parse(pending.Expires)>now)return;
        var row=goods!.RejectBet(pending.Id);room.Revision++;
        var owner=room.Seats[row.Seat]?.Session;if(owner!=null)SendGoods(owner,row.Save,row);
        Broadcast(room);
    }
    void GoodsCommand(Session session,Command command){
        Need(goods!=null&&session.AccountId!=null&&session.StoreStakes,"goods_account","登录账号并更新客户端后可使用实物押注。");
        if(command.Type=="goodsList"){
            session.Client?.Send(new{type="goodsList",saveId=command.SaveId,ids=goods!.Inbox(session.Id,command.SaveId).Select(l=>l.Id).ToArray()});return;
        }
        if(command.Type=="goodsOffer"){
            var previous=goods!.Find(command.Transfer);
            if(previous!=null){Need(previous.Owner==session.Id&&previous.Save==command.SaveId,"goods_scope","这份押品属于其他账号或存档。");SendGoods(session,command.SaveId,previous);return;}
            var room=GoodsRoom(session,command,command.Wager);var game=room.Game;int seat=OwnSeat(room,session);
            GoodsLedger.Lot row;
            if(command.Wager){
                Need(game is{Done:false,SupportsAssetBets:true}&&game.Actor==seat,"asset_phase","轮到你下注时可用实物抵注。");
                Need(room.Seats.All(s=>s!=null&&Online(s.Session)),"peer_offline","桌友掉线，等待重连后继续。");
                Need(PendingAsset(room)==null,"asset_pending","正在核对上一件押品。");
                var plan=game!.QuoteAsset(command.Price);Need(command.Amount==plan.Amount&&command.Move==plan.Move,"asset_changed","抵注金额已变，请重新选择物品。");
                row=goods.Offer(session.Id,command.SaveId,command.Transfer,room.LedgerId,room.Hands,seat,command.Payload,command.Price,plan.Move,plan.Amount,game.Round);
            }else{
                row=goods.Offer(session.Id,command.SaveId,command.Transfer,room.LedgerId,room.Hands+1,seat,command.Payload,command.Price);
                foreach(var s in room.Seats.Where(s=>s!=null))s!.Ready=false;
            }
            room.Revision++;SendGoods(session,command.SaveId,row);Audit("goods_offered",session,room);return;
        }
        if(command.Type=="goodsCancel"){
            var row=goods!.Cancel(session.Id,command.SaveId,command.Transfer);
            if(Current(session) is {} room){foreach(var s in room.Seats.Where(s=>s!=null))s!.Ready=false;room.Revision++;}
            SendGoods(session,command.SaveId,row);return;
        }
        if(command.Type is "goodsApprove" or "goodsReject"){
            var row=goods!.Find(command.Transfer);Need(row!=null,"goods_changed","桌上的押品已改变。");
            var room=GoodsRoom(session,command,row!.BetMove.Length>0);
            Need(row.Room==room.LedgerId&&row.Owner!=session.Id,"goods_changed","桌上的押品已改变。");
            if(row.BetMove.Length>0){
                Need(row.Hand==room.Hands,"goods_changed","这件押品属于其他手牌。");
                if(row.State is "locked" or "claimable" or "delivered")return;
                Need(row.State=="offered"&&PendingAsset(room)?.Id==row.Id,"goods_changed","这次抵注已结束。");
                if(command.Type=="goodsReject"){
                    var returned=goods.RejectBet(row.Id);room.Revision++;SendGoods(room.Seats[row.Seat]!.Session,row.Save,returned);return;
                }
                var game=room.Game;Need(game is{Done:false}&&game.Actor==row.Seat&&game.Round==row.BetRound,"asset_changed","牌局已变，押品会退回。");
                Need(room.Seats.All(s=>s!=null&&Online(s.Session)),"peer_offline","等待桌友重连后继续核对。");
                Need(DateTimeOffset.Parse(row.Expires)>DateTimeOffset.UtcNow,"goods_changed","核对时间已过，押品将退回。");
                var plan=game!.QuoteAsset(row.Quote);Need(plan.Move==row.BetMove&&plan.Amount==row.BetAmount,"asset_changed","抵注金额已变。");
                goods.LockBet(row.Id,session.Id);int paid=game.PlayAsset(row.Seat,row.Quote,row.BetMove,row.BetAmount);
                room.Committed[row.Seat]+=paid;room.Events.Add(new{kind="asset_bet",seat=row.Seat,move=row.BetMove,amount=paid,asset=row.Quote,round=row.BetRound});
                UpdateBalances(room);room.Revision++;room.Touched=DateTime.UtcNow;Audit("asset_bet",session,room);return;
            }
            Need(command.Type=="goodsApprove"&&row.Hand==room.Hands+1,"goods_changed","桌上的押品已改变。");
            goods.Approve(row.Id,session.Id,room.Seats.Where(s=>s!=null).Select(s=>s!.Session.Id).ToArray());room.Revision++;Audit("goods_approved",session,room);return;
        }
        if(command.Type=="goodsDelivered"){goods!.Delivered(command.Transfer,session.Id,command.SaveId);return;}
        Need(command.Type=="goodsReceipt","unknown","未知押品操作。");
        var found=goods!.Find(command.Transfer);
        if(found==null){session.Client?.Send(new{type="goods",id=command.Transfer,saveId=command.SaveId,state="missing"});return;}
        bool owner=found.Owner==session.Id&&found.Save==command.SaveId,recipient=found.Recipient==session.Id&&found.RecipientSave==command.SaveId;
        var current=Current(session);bool opponent=current!=null&&current.Economy=="store"&&current.LedgerId==found.Room&&current.Seats[OwnSeat(current,session)]!.SaveId==command.SaveId&&found.State is "offered" or "accepted" or "locked";
        Need(owner||recipient||opponent,"goods_scope","这份押品与当前账号和存档无关。");SendGoods(session,command.SaveId,found);
    }
    static object GoodsMeta(GoodsLedger.Lot row)=>new{id=row.Id,owner=row.Owner,hand=row.Hand,seat=row.Seat,label=row.Kind.Label,quote=row.Quote,atlas=row.Kind.Atlas,sprite=row.Kind.Sprite,state=row.State,acceptedBy=row.AcceptedBy,wager=row.BetMove.Length>0,move=row.BetMove,amount=row.BetAmount,round=row.BetRound,expires=row.Expires};
    object[] GoodsView(Room room){
        if(room.Economy!="store")return Array.Empty<object>();
        return goods!.Table(room.LedgerId,room.Hands+1).Where(l=>l.State is "offered" or "accepted").Concat(room.Game==null?Enumerable.Empty<GoodsLedger.Lot>():goods.Table(room.LedgerId,room.Hands).Where(l=>room.Game.Done||l.State is "offered" or "accepted" or "locked")).Take(32).Select(GoodsMeta).ToArray();
    }
    void SendGoods(Session session,string save,GoodsLedger.Lot row){
        bool recipient=row.Recipient==session.Id&&row.RecipientSave==save;
        bool owner=row.Owner==session.Id&&row.Save==save;
        bool peer=Current(session) is {} room&&room.LedgerId==row.Room&&room.Seats[OwnSeat(room,session)]?.SaveId==save;
        string state=row.State=="claimable"&&!recipient||row.State=="delivered"&&!recipient?"lost":row.State;
        session.Client?.Send(new{type="goods",id=row.Id,saveId=save,owner=row.Owner,state,payload=recipient||owner||peer&&state is "offered" or "accepted" or "locked"?row.Payload:"",quote=row.Quote,meta=GoodsMeta(row)});
    }
}
