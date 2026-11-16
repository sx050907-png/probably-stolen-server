using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightStud.Server;

public sealed class MarketLedger {
    readonly Accounts accounts;
    static readonly Regex Key=new("^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    public sealed record Listing(string Id,string Seller,string SellerSave,string Buyer,string BuyerSave,string Label,string Kind,int Price,string State,string Payload,string Created,string Expires,bool Delivered);
    public MarketLedger(Accounts accounts){
        this.accounts=accounts;_ = new StakeLedger(accounts);
        using var db=accounts.Open();using var c=Cmd(db,null,"""
            CREATE TABLE IF NOT EXISTS market_listings(
              id TEXT PRIMARY KEY,seller TEXT NOT NULL,seller_save TEXT NOT NULL,buyer TEXT NOT NULL DEFAULT '',buyer_save TEXT NOT NULL DEFAULT '',
              label TEXT NOT NULL,kind TEXT NOT NULL,price INTEGER NOT NULL,state TEXT NOT NULL,payload TEXT NOT NULL,created TEXT NOT NULL,expires TEXT NOT NULL,delivered INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS market_browse ON market_listings(state,created);
            CREATE INDEX IF NOT EXISTS market_seller ON market_listings(seller,seller_save,state);
            CREATE INDEX IF NOT EXISTS market_buyer ON market_listings(buyer,buyer_save,state);
            """);c.ExecuteNonQuery();
    }
    static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object)[] args){var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var p in args)c.Parameters.AddWithValue(p.Item1,p.Item2);return c;}
    static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool value,string code,string text){if(!value)throw new WireFault(code,text);}
    static void Scope(string owner,string save,string id){Need(owner!=null&&save!=null&&id!=null&&Key.IsMatch(owner)&&Key.IsMatch(save)&&Key.IsMatch(id),"market_scope","账号、存档或货单编号无效。");}
    const string Columns="id,seller,seller_save,buyer,buyer_save,label,kind,price,state,payload,created,expires,delivered";
    static Listing Row(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetBoolean(12));
    static Listing? Read(SqliteConnection db,SqliteTransaction? tx,string id){using var c=Cmd(db,tx,"SELECT "+Columns+" FROM market_listings WHERE id=$i",("$i",id));using var r=c.ExecuteReader();return r.Read()?Row(r):null;}
    static bool Owner(Listing r,string owner,string save)=>r.Seller==owner&&r.SellerSave==save||r.Buyer==owner&&r.BuyerSave==save;
    static void Expire(SqliteConnection db,SqliteTransaction? tx){using var c=Cmd(db,tx,"UPDATE market_listings SET state='cancelled' WHERE state='listed' AND expires<=$t",("$t",DateTimeOffset.UtcNow.ToString("O")));c.ExecuteNonQuery();}
    public Listing Offer(string owner,string save,string id,string payload,int price){
        Scope(owner,save,id);Need(price is >0 and <=1000000,"market_price","售价请填 1–1000000 信用点。");
        var kind=GoodsLedger.Validate(payload);using var doc=JsonDocument.Parse(payload);string label=doc.RootElement.GetProperty("label").GetString()??kind.Label;
        Need(label.Length is >0 and <=128&&!label.Any(char.IsControl),"bad_lot","物品名称无效。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();var old=Read(db,tx,id);
        if(old!=null){Need(old.Seller==owner&&old.SellerSave==save,"market_scope","这份货单不属于当前账号或存档。");if(old.State!="cancelled")Need(old.Price==price&&old.Payload==payload||old.State=="sold","market_conflict","这份货单的内容发生变化。");return old;}
        Expire(db,tx);
        using(var count=Cmd(db,tx,"SELECT COUNT(*) FROM market_listings WHERE state='listed' AND seller=$a",("$a",owner)))Need(Convert.ToInt32(count.ExecuteScalar())<20,"market_full","每个账号最多同时上架 20 件货。");
        using(var count=Cmd(db,tx,"SELECT COUNT(*) FROM market_listings WHERE state='listed'"))Need(Convert.ToInt32(count.ExecuteScalar())<2000,"market_full","市场货架已满，请稍后再上架。");
        string now=DateTimeOffset.UtcNow.ToString("O"),until=DateTimeOffset.UtcNow.AddDays(7).ToString("O");
        using(var c=Cmd(db,tx,"INSERT INTO market_listings(id,seller,seller_save,label,kind,price,state,payload,created,expires) VALUES($i,$a,$s,$l,$k,$p,'listed',$d,$t,$e)",("$i",id),("$a",owner),("$s",save),("$l",label),("$k",kind.Id),("$p",price),("$d",payload),("$t",now),("$e",until)))c.ExecuteNonQuery();
        var row=Read(db,tx,id)!;tx.Commit();return row;
    }
    public Listing Buy(string buyer,string save,string id,int expectedPrice){
        Scope(buyer,save,id);Need(expectedPrice is >0 and <=1000000,"market_price","购买价格无效。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();Expire(db,tx);var row=Read(db,tx,id);Need(row!=null,"market_gone","这件货已下架。");
        if(row!.State=="sold"&&row.Buyer==buyer&&row.BuyerSave==save){Need(row.Price==expectedPrice,"market_conflict","购买记录的金额不符。");return row;}
        Need(row.State=="listed"&&row.Price==expectedPrice,"market_gone","这件货已成交或下架，请刷新市场。");Need(row.Seller!=buyer,"market_self","不能购买自己上架的物品。");
        // Both wallets and the unique item change hands in one SQLite transaction.
        ChangeWallet(db,tx,buyer,save,-row.Price);ChangeWallet(db,tx,row.Seller,row.SellerSave,row.Price);
        using(var c=Cmd(db,tx,"UPDATE market_listings SET state='sold',buyer=$a,buyer_save=$s WHERE id=$i AND state='listed'",("$a",buyer),("$s",save),("$i",id)))Need(c.ExecuteNonQuery()==1,"market_gone","这件货刚刚被别人买走了。");
        row=Read(db,tx,id)!;tx.Commit();return row;
    }
    static void ChangeWallet(SqliteConnection db,SqliteTransaction tx,string owner,string save,int delta){
        long value;using(var c=Cmd(db,tx,"SELECT balance FROM cash_wallets WHERE account=$a AND save_id=$s",("$a",owner),("$s",save)))value=Convert.ToInt64(c.ExecuteScalar()??0);
        value+=delta;Need(value>=0&&value<=1000000000,"cash_balance","暂存信用点不足，或收款方余额已满。");
        using var change=Cmd(db,tx,"INSERT INTO cash_wallets(account,save_id,balance) VALUES($a,$s,$v) ON CONFLICT(account,save_id) DO UPDATE SET balance=excluded.balance",("$a",owner),("$s",save),("$v",value));change.ExecuteNonQuery();
    }
    public Listing Cancel(string owner,string save,string id){
        Scope(owner,save,id);using var db=accounts.Open();using var tx=db.BeginTransaction();var row=Read(db,tx,id);
        if(row==null){
            // A missing upload is cancelled durably before its local item is returned.
            // A delayed offer with this ID can therefore never relist that same item.
            string now=DateTimeOffset.UtcNow.ToString("O");using var insert=Cmd(db,tx,"INSERT INTO market_listings(id,seller,seller_save,label,kind,price,state,payload,created,expires) VALUES($i,$a,$s,'','',0,'cancelled','',$t,$t)",("$i",id),("$a",owner),("$s",save),("$t",now));insert.ExecuteNonQuery();
        }else{
            Need(row.Seller==owner&&row.SellerSave==save,"market_scope","这件货属于另一账号或经营存档。");
            using var c=Cmd(db,tx,"UPDATE market_listings SET state='cancelled' WHERE id=$i AND state='listed'",("$i",id));c.ExecuteNonQuery();
        }
        row=Read(db,tx,id)!;tx.Commit();return row;
    }
    public Listing? Status(string owner,string save,string id,bool cancelMissing=false){
        Scope(owner,save,id);using var db=accounts.Open();Expire(db,null);var row=Read(db,null,id);
        if(row==null)return cancelMissing?Cancel(owner,save,id):null;
        Need(Owner(row,owner,save),"market_scope","这份货单属于另一账号或经营存档。");return row;
    }
    public Listing Delivered(string owner,string save,string id){
        Scope(owner,save,id);using var db=accounts.Open();using var tx=db.BeginTransaction();var row=Read(db,tx,id);Need(row!=null,"market_gone","货单不存在。");
        Need(row!.State=="sold"&&row.Buyer==owner&&row.BuyerSave==save||row.State=="cancelled"&&row.Seller==owner&&row.SellerSave==save,"market_scope","这件货目前不能由你领取。");
        using(var c=Cmd(db,tx,"UPDATE market_listings SET delivered=1,payload='' WHERE id=$i",("$i",id)))c.ExecuteNonQuery();row=Read(db,tx,id)!;tx.Commit();return row;
    }
    public Listing? Preview(string id){Need(id!=null&&Key.IsMatch(id),"market_scope","货单编号无效。");using var db=accounts.Open();Expire(db,null);var row=Read(db,null,id);Need(row?.State=="listed","market_gone","这件货已成交或下架。");return row;}
    public Listing[] Browse(string owner,string save,string tab,int page){
        Need(owner!=null&&save!=null&&Key.IsMatch(owner)&&Key.IsMatch(save)&&page is >=0 and <=500,"market_scope","市场页码无效。");
        Need(tab is "buy" or "sell" or "orders","market_scope","未知市场分类。");using var db=accounts.Open();Expire(db,null);
        string where=tab=="buy"?"state='listed'":tab=="sell"?"seller=$a AND seller_save=$s":"(buyer=$a AND buyer_save=$s) OR (seller=$a AND seller_save=$s AND state='cancelled')";
        using var c=Cmd(db,null,"SELECT "+Columns+" FROM market_listings WHERE "+where+" ORDER BY CASE WHEN delivered=0 THEN 0 ELSE 1 END,created DESC,id LIMIT 7 OFFSET $p",("$a",owner),("$s",save),("$p",page*6));using var r=c.ExecuteReader();var rows=new List<Listing>();while(r.Read())rows.Add(Row(r));return rows.ToArray();
    }
}
