namespace NightStud.Server;

public sealed partial class RoomHub
{
    void CashCommand(Session session,Command command){
        Need(cash!=null&&session.AccountId!=null&&session.StoreStakes,"cash_account","登录账号并更新客户端后可使用信用点暂存。");
        StakeLedger.Receipt? receipt=command.Type switch{
            "cashTransfer"=>cash!.Transfer(session.Id,command.SaveId,command.Transfer,command.Direction,command.Amount??0),
            "cashReceipt"=>cash!.Find(session.Id,command.SaveId,command.Transfer),
            "cashCancel"=>cash!.CancelDeposit(session.Id,command.SaveId,command.Transfer,command.Amount??0,command.Direction.Length==0?"in":command.Direction),
            "cashDelivered"=>cash!.Acknowledge(session.Id,command.SaveId,command.Transfer),
            _=>null
        };
        session.Client?.Send(new{type="cash",request=command.Type,transfer=command.Transfer,saveId=command.SaveId,receipt,available=cash!.Balance(session.Id,command.SaveId)});
        Audit("cash_"+command.Type,session,Current(session));
    }
    void FundSeat(Room room,Session session,string save){
        Need(room.Economy=="store"&&cash!=null&&session.AccountId!=null&&session.StoreStakes,"cash_room","这间房使用练习筹码。");
        Need(room.Game is null||room.Game.Done,"playing","这手结束后再调整入桌金额。");
        var index=OwnSeat(room,session);var seat=room.Seats[index]!;
        Need(seat.CashHold.Length==0,"cash_funded","这个座位已经暂存了信用点。");
        var hold=cash!.Reserve(session.Id,save,room.LedgerId,index,room.BuyIn);seat.CashHold=hold.Id;seat.SaveId=save;seat.Balance=hold.Balance;
        foreach(var s in room.Seats.Where(s=>s!=null))s!.Ready=false;
        room.Revision++;room.Touched=DateTime.UtcNow;Audit("cash_table_funded",session,room);
    }
}
