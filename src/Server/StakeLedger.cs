using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace NightStud.Server;

// Native saves own the money outside the server. This ledger owns transfers,
// reserved table balances and receipts after a deposit has been accepted.
public sealed class StakeLedger
{
    readonly Accounts accounts;
    public const int MaxTransfer=1000000;
    public sealed record Receipt(string Id,string Direction,string State,int Amount);
    public sealed record Hold(string Id,string Account,string Save,string Room,int Seat,int Balance,int Rollback,bool InHand);
    public StakeLedger(Accounts accounts){
        this.accounts=accounts;
        using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="""
          CREATE TABLE IF NOT EXISTS cash_wallets(account TEXT NOT NULL,save_id TEXT NOT NULL,balance INTEGER NOT NULL CHECK(balance>=0 AND balance<=1000000000),PRIMARY KEY(account,save_id));
          CREATE TABLE IF NOT EXISTS cash_transfers(id TEXT PRIMARY KEY,account TEXT NOT NULL,save_id TEXT NOT NULL,direction TEXT NOT NULL,amount INTEGER NOT NULL,state TEXT NOT NULL,created TEXT NOT NULL);
          CREATE INDEX IF NOT EXISTS cash_transfer_owner ON cash_transfers(account,save_id);
          CREATE TABLE IF NOT EXISTS cash_holds(id TEXT PRIMARY KEY,account TEXT NOT NULL,save_id TEXT NOT NULL,room TEXT NOT NULL,seat INTEGER NOT NULL,balance INTEGER NOT NULL CHECK(balance>=0),rollback_balance INTEGER NOT NULL CHECK(rollback_balance>=0),in_hand INTEGER NOT NULL DEFAULT 0,UNIQUE(room,seat));
          """;cmd.ExecuteNonQuery();
    }
    static void Need(bool ok,string code,string message){if(!ok)throw new WireFault(code,message);}
    static void Scope(string account,string save){Need(Regex.IsMatch(account,"^[a-f0-9]{32}$")&&Regex.IsMatch(save,"^[a-f0-9]{32}$"),"cash_scope","账号或经营存档标识无效。");}
    static void Id(string id)=>Need(Regex.IsMatch(id,"^[a-f0-9]{32}$"),"cash_id","暂存编号无效。");
    static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object)[] args){var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var p in args)c.Parameters.AddWithValue(p.Item1,p.Item2);return c;}
    static int Wallet(SqliteConnection db,SqliteTransaction? tx,string account,string save){using var c=Cmd(db,tx,"SELECT balance FROM cash_wallets WHERE account=$a AND save_id=$s",("$a",account),("$s",save));return Convert.ToInt32(c.ExecuteScalar()??0);}
    static void Change(SqliteConnection db,SqliteTransaction tx,string account,string save,int delta){
        long balance=(long)Wallet(db,tx,account,save)+delta;Need(balance>=0&&balance<=1000000000,"cash_balance","暂存信用点不足，或余额超过上限。");
        using var c=Cmd(db,tx,"INSERT INTO cash_wallets(account,save_id,balance) VALUES($a,$s,$v) ON CONFLICT(account,save_id) DO UPDATE SET balance=excluded.balance",("$a",account),("$s",save),("$v",balance));c.ExecuteNonQuery();
    }
    static Receipt? Read(SqliteConnection db,SqliteTransaction? tx,string account,string save,string id){
        using var c=Cmd(db,tx,"SELECT account,save_id,direction,state,amount FROM cash_transfers WHERE id=$i",("$i",id));using var r=c.ExecuteReader();
        if(!r.Read())return null;Need(r.GetString(0)==account&&r.GetString(1)==save,"cash_scope","这份暂存记录属于另一账号或存档。");return new(id,r.GetString(2),r.GetString(3),r.GetInt32(4));
    }
    static void Insert(SqliteConnection db,SqliteTransaction tx,string account,string save,Receipt receipt){
        using var c=Cmd(db,tx,"INSERT INTO cash_transfers(id,account,save_id,direction,amount,state,created) VALUES($i,$a,$s,$d,$n,$v,$t)",("$i",receipt.Id),("$a",account),("$s",save),("$d",receipt.Direction),("$n",receipt.Amount),("$v",receipt.State),("$t",DateTimeOffset.UtcNow.ToString("O")));c.ExecuteNonQuery();
    }
    public int Balance(string account,string save){Scope(account,save);using var db=accounts.Open();return Wallet(db,null,account,save);}
    public Receipt Transfer(string account,string save,string id,string direction,int amount){
        Scope(account,save);Id(id);Need(direction is "in" or "out"&&amount is >0 and <=MaxTransfer,"cash_amount","暂存金额须为 1–1000000 信用点。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();var old=Read(db,tx,account,save,id);
        if(old!=null){Need(old.Direction==direction&&old.Amount==amount,"cash_conflict","同一暂存编号的金额或方向发生变化。");return old;}
        if(direction=="in"){
            using var held=Cmd(db,tx,"SELECT COALESCE(SUM(balance),0) FROM cash_holds WHERE account=$a AND save_id=$s",("$a",account),("$s",save));
            long reserve=Math.Max(NightStud.Shared.WagerRules.MaxTableTotal,Convert.ToInt64(held.ExecuteScalar()));
            Need((long)Wallet(db,tx,account,save)+amount+reserve<=1000000000,"cash_balance","暂存余额已接近上限，请先取回一部分。");
        }
        Change(db,tx,account,save,direction=="in"?amount:-amount);
        var receipt=new Receipt(id,direction,direction=="in"?"accepted":"reserved",amount);Insert(db,tx,account,save,receipt);tx.Commit();return receipt;
    }
    public Receipt? Find(string account,string save,string id){Scope(account,save);Id(id);using var db=accounts.Open();return Read(db,null,account,save,id);}
    public Receipt CancelDeposit(string account,string save,string id,int amount,string direction="in"){
        Scope(account,save);Id(id);Need(amount is >0 and <=MaxTransfer,"cash_amount","暂存金额无效。");
        Need(direction is "in" or "out","cash_direction","暂存方向无效。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();var old=Read(db,tx,account,save,id);
        if(old!=null){Need(old.Direction==direction&&old.Amount==amount,"cash_conflict","暂存记录不匹配。");return old;}
        var receipt=new Receipt(id,direction,"cancelled",amount);Insert(db,tx,account,save,receipt);tx.Commit();return receipt;
    }
    public Receipt Acknowledge(string account,string save,string id){
        Scope(account,save);Id(id);using var db=accounts.Open();using var tx=db.BeginTransaction();var receipt=Read(db,tx,account,save,id)??throw new WireFault("cash_missing","没有这份暂存记录。");
        Need(receipt.Direction=="out","cash_direction","该记录不需要领取确认。");
        if(receipt.State=="cancelled")return receipt;
        using var c=Cmd(db,tx,"UPDATE cash_transfers SET state='delivered' WHERE id=$i AND state='reserved'",("$i",id));c.ExecuteNonQuery();tx.Commit();return receipt with{State="delivered"};
    }
    public Hold Reserve(string account,string save,string room,int seat,int amount){
        Scope(account,save);Id(room);Need(seat is >=0 and <4&&amount is >0 and <=NightStud.Shared.WagerRules.MaxBuyIn,"cash_amount","入桌金额或座位无效。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();
        using(var c=Cmd(db,tx,"SELECT id,account,save_id,balance,rollback_balance,in_hand FROM cash_holds WHERE room=$r AND seat=$p",("$r",room),("$p",seat))){using var r=c.ExecuteReader();if(r.Read()){
            Need(r.GetString(1)==account&&r.GetString(2)==save,"cash_scope","该座位已由另一份暂存占用。");return new(r.GetString(0),account,save,room,seat,r.GetInt32(3),r.GetInt32(4),r.GetInt32(5)!=0);
        }}
        Need((long)Wallet(db,tx,account,save)-amount+NightStud.Shared.WagerRules.MaxTableTotal<=1000000000,"cash_balance","暂存余额已接近上限，请先取回一部分再入桌。");
        Change(db,tx,account,save,-amount);var hold=new Hold(Guid.NewGuid().ToString("N"),account,save,room,seat,amount,amount,false);
        using(var c=Cmd(db,tx,"INSERT INTO cash_holds(id,account,save_id,room,seat,balance,rollback_balance) VALUES($i,$a,$s,$r,$p,$n,$n)",("$i",hold.Id),("$a",account),("$s",save),("$r",room),("$p",seat),("$n",amount)))c.ExecuteNonQuery();
        tx.Commit();return hold;
    }
    static List<Hold> Holds(SqliteConnection db,SqliteTransaction? tx,string? room=null){
        using var c=Cmd(db,tx,"SELECT id,account,save_id,room,seat,balance,rollback_balance,in_hand FROM cash_holds"+(room==null?"":" WHERE room=$r"));if(room!=null)c.Parameters.AddWithValue("$r",room);
        using var r=c.ExecuteReader();var result=new List<Hold>();while(r.Read())result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7)!=0));return result;
    }
    public void BeginHand(string room,int[] balances,Action<SqliteConnection,SqliteTransaction>? extra=null){
        Id(room);using var db=accounts.Open();using var tx=db.BeginTransaction();var rows=Holds(db,tx,room);
        Need(rows.Count==balances.Length&&rows.All(h=>!h.InHand&&h.Seat<balances.Length&&h.Balance==balances[h.Seat]),"cash_ledger","桌上暂存与开局余额不一致。");
        extra?.Invoke(db,tx);using var c=Cmd(db,tx,"UPDATE cash_holds SET rollback_balance=balance,in_hand=1 WHERE room=$r",("$r",room));c.ExecuteNonQuery();tx.Commit();
    }
    public void FinishHand(string room,int[] balances,Action<SqliteConnection,SqliteTransaction>? extra=null){
        Id(room);using var db=accounts.Open();using var tx=db.BeginTransaction();var rows=Holds(db,tx,room);
        Need(rows.Count==balances.Length&&rows.All(h=>h.Seat<balances.Length)&&balances.All(b=>b>=0)&&(long)balances.Sum()==rows.Sum(h=>(long)(h.InHand?h.Rollback:h.Balance)),"cash_ledger","牌局结算不守恒，暂存已保留。");
        Need(rows.All(h=>h.InHand)||rows.All(h=>!h.InHand&&h.Balance==balances[h.Seat]),"cash_ledger","这手牌已按另一份结果结算，暂存已保留。");
        extra?.Invoke(db,tx);foreach(var hold in rows){using var c=Cmd(db,tx,"UPDATE cash_holds SET balance=$n,rollback_balance=$n,in_hand=0 WHERE id=$i",("$n",balances[hold.Seat]),("$i",hold.Id));c.ExecuteNonQuery();}tx.Commit();
    }
    public int Release(string id,string account,string save){
        Scope(account,save);Id(id);using var db=accounts.Open();using var tx=db.BeginTransaction();var hold=Holds(db,tx).FirstOrDefault(h=>h.Id==id);
        if(hold==null)return 0;Need(hold.Account==account&&hold.Save==save,"cash_scope","这份桌上暂存属于另一账号或存档。");Need(!hold.InHand,"cash_playing","这手牌尚未结算。");
        Change(db,tx,account,save,hold.Balance);using var c=Cmd(db,tx,"DELETE FROM cash_holds WHERE id=$i",("$i",id));c.ExecuteNonQuery();tx.Commit();return hold.Balance;
    }
    public int RecoverInterruptedServer(){
        using var db=accounts.Open();using var tx=db.BeginTransaction();var holds=Holds(db,tx);
        foreach(var hold in holds)Change(db,tx,hold.Account,hold.Save,hold.InHand?hold.Rollback:hold.Balance);
        using var c=Cmd(db,tx,"DELETE FROM cash_holds");c.ExecuteNonQuery();tx.Commit();return holds.Count;
    }
}
