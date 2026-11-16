using Microsoft.Data.Sqlite;

namespace NightStud.Server;

// Phone numbers belong to accounts on this server, never to a local save or device.
public sealed class PlayerPhones {
    readonly Accounts accounts;
    public PlayerPhones(Accounts accounts){
        this.accounts=accounts;
        using var db=accounts.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS account_phones(
              sequence INTEGER PRIMARY KEY AUTOINCREMENT CHECK(sequence BETWEEN 1 AND 900000),
              account TEXT NOT NULL UNIQUE,
              number INTEGER GENERATED ALWAYS AS (100000 + ((sequence * 48271 + 137021) % 900000)) STORED UNIQUE);
            CREATE TRIGGER IF NOT EXISTS account_phone_registered AFTER INSERT ON accounts BEGIN
              INSERT INTO account_phones(account) VALUES(NEW.id);
            END;
            INSERT INTO account_phones(account) SELECT id FROM accounts
              WHERE id NOT IN (SELECT account FROM account_phones) ORDER BY created,id;
            CREATE TABLE IF NOT EXISTS phone_history(
              call TEXT NOT NULL,owner TEXT NOT NULL,peer TEXT NOT NULL,incoming INTEGER NOT NULL,
              connected INTEGER NOT NULL,reason TEXT NOT NULL,created TEXT NOT NULL,seen INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(owner,call));
            CREATE INDEX IF NOT EXISTS phone_history_owner ON phone_history(owner,created);
            """;
        cmd.ExecuteNonQuery();tx.Commit();
    }
    public string Number(string? account){
        if(account==null)return "";using var db=accounts.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT number FROM account_phones WHERE account=$a";cmd.Parameters.AddWithValue("$a",account);
        return Convert.ToString(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)??"";
    }
    public AccountIdentity? Resolve(string? number){
        if(number==null||number.Length!=6||number[0]=='0'||number.Any(c=>c<'0'||c>'9'))return null;
        using var db=accounts.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT a.id,a.display_name,a.steam_id,a.banned_until FROM account_phones p JOIN accounts a ON a.id=p.account WHERE p.number=$n";
        cmd.Parameters.AddWithValue("$n",number);using var r=cmd.ExecuteReader();
        if(!r.Read()||!r.IsDBNull(3)&&DateTimeOffset.Parse(r.GetString(3))>DateTimeOffset.UtcNow)return null;
        return new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?"password":"steam");
    }
    public void Record(string call,string from,string to,bool connected,string reason){
        using var db=accounts.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT OR IGNORE INTO phone_history(call,owner,peer,incoming,connected,reason,created) VALUES($c,$o,$p,$i,$k,$r,$t)";
        cmd.Parameters.AddWithValue("$c",call);cmd.Parameters.AddWithValue("$o",from);cmd.Parameters.AddWithValue("$p",to);cmd.Parameters.AddWithValue("$i",0);
        cmd.Parameters.AddWithValue("$k",connected);cmd.Parameters.AddWithValue("$r",reason);cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        cmd.Parameters["$o"].Value=to;cmd.Parameters["$p"].Value=from;cmd.Parameters["$i"].Value=1;cmd.ExecuteNonQuery();
        cmd.CommandText="DELETE FROM phone_history WHERE created < $old OR (owner=$o AND call IN (SELECT call FROM phone_history WHERE owner=$o ORDER BY created DESC LIMIT -1 OFFSET 60)) OR (owner=$p AND call IN (SELECT call FROM phone_history WHERE owner=$p ORDER BY created DESC LIMIT -1 OFFSET 60))";
        cmd.Parameters.AddWithValue("$old",DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));cmd.ExecuteNonQuery();tx.Commit();
    }
    public object[] History(string owner){
        using var db=accounts.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT h.call,h.peer,a.display_name,p.number,h.incoming,h.connected,h.reason,h.created,h.seen FROM phone_history h JOIN accounts a ON a.id=h.peer LEFT JOIN account_phones p ON p.account=h.peer WHERE h.owner=$o ORDER BY h.created DESC LIMIT 30";
        cmd.Parameters.AddWithValue("$o",owner);using var r=cmd.ExecuteReader();var rows=new List<object>();
        while(r.Read())rows.Add(new{id=r.GetString(0),peer=r.GetString(1),name=r.GetString(2),number=r.IsDBNull(3)?"":r.GetInt64(3).ToString(),incoming=r.GetBoolean(4),connected=r.GetBoolean(5),reason=r.GetString(6),created=r.GetString(7),read=r.GetBoolean(8)});
        return rows.ToArray();
    }
    public void Read(string owner){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE phone_history SET seen=1 WHERE owner=$o";cmd.Parameters.AddWithValue("$o",owner);cmd.ExecuteNonQuery();}
}
