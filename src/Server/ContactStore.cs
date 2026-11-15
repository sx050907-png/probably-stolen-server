using Microsoft.Data.Sqlite;

namespace NightStud.Server;

public sealed record ContactPrivacy(bool AllowMessages=true,bool AllowCalls=true);
public sealed record ContactMessage(long Sequence,string Id,string Sender,string Recipient,string Name,string Text,string Kind,string Created,bool Read);
public sealed record ContactThread(string Id,string Name,string Provider,string Text,string Created,long Sequence,int Unread,bool Blocked);
public sealed class ContactStore {
    readonly Accounts accounts;
    public ContactStore(Accounts accounts){
        this.accounts=accounts;using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS contact_settings(account TEXT PRIMARY KEY,messages INTEGER NOT NULL,calls INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS contact_blocks(owner TEXT NOT NULL,peer TEXT NOT NULL,PRIMARY KEY(owner,peer));
            CREATE TABLE IF NOT EXISTS contact_messages(sequence INTEGER PRIMARY KEY AUTOINCREMENT,nonce TEXT NOT NULL,
              sender TEXT NOT NULL,recipient TEXT NOT NULL,text TEXT NOT NULL,kind TEXT NOT NULL,created TEXT NOT NULL,seen INTEGER NOT NULL DEFAULT 0,UNIQUE(sender,nonce));
            CREATE INDEX IF NOT EXISTS contact_inbox ON contact_messages(recipient,sequence);
            CREATE INDEX IF NOT EXISTS contact_outbox ON contact_messages(sender,sequence);
            CREATE INDEX IF NOT EXISTS contact_expiry ON contact_messages(created);
            """;cmd.ExecuteNonQuery();
    }
    public ContactPrivacy Settings(string account){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT messages,calls FROM contact_settings WHERE account=$a";cmd.Parameters.AddWithValue("$a",account);using var row=cmd.ExecuteReader();return row.Read()?new(row.GetBoolean(0),row.GetBoolean(1)):new();}
    public void Settings(string account,ContactPrivacy value){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO contact_settings VALUES($a,$m,$c) ON CONFLICT(account) DO UPDATE SET messages=$m,calls=$c";cmd.Parameters.AddWithValue("$a",account);cmd.Parameters.AddWithValue("$m",value.AllowMessages);cmd.Parameters.AddWithValue("$c",value.AllowCalls);cmd.ExecuteNonQuery();}
    public AccountIdentity? Identity(string id){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,display_name,steam_id,banned_until FROM accounts WHERE id=$a";cmd.Parameters.AddWithValue("$a",id);using var row=cmd.ExecuteReader();if(!row.Read()||!row.IsDBNull(3)&&DateTimeOffset.Parse(row.GetString(3))>DateTimeOffset.UtcNow)return null;return new(row.GetString(0),row.GetString(1),row.IsDBNull(2)?"password":"steam");}
    public bool Blocked(string owner,string peer){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM contact_blocks WHERE owner=$a AND peer=$p";cmd.Parameters.AddWithValue("$a",owner);cmd.Parameters.AddWithValue("$p",peer);return Convert.ToInt32(cmd.ExecuteScalar())>0;}
    public void Block(string owner,string peer,bool blocked){
        using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$a",owner);cmd.Parameters.AddWithValue("$p",peer);
        if(blocked){cmd.CommandText="SELECT COUNT(*) FROM contact_blocks WHERE owner=$a";if(Convert.ToInt32(cmd.ExecuteScalar())>=200&&!Blocked(owner,peer))throw new WireFault("block_limit","屏蔽名单已满，请先移除不需要的记录。");}
        cmd.CommandText=blocked?"INSERT OR IGNORE INTO contact_blocks VALUES($a,$p)":"DELETE FROM contact_blocks WHERE owner=$a AND peer=$p";cmd.ExecuteNonQuery();
    }
    public ContactMessage? Existing(string sender,string nonce){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT m.sequence,m.nonce,m.sender,m.recipient,a.display_name,m.text,m.kind,m.created,m.seen FROM contact_messages m JOIN accounts a ON a.id=m.sender WHERE m.sender=$a AND m.nonce=$n";cmd.Parameters.AddWithValue("$a",sender);cmd.Parameters.AddWithValue("$n",nonce);using var row=cmd.ExecuteReader();return row.Read()?Read(row):null;}
    static ContactMessage Read(SqliteDataReader row)=>new(row.GetInt64(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.GetString(5),row.GetString(6),row.GetString(7),row.GetBoolean(8));
    public ContactMessage Save(string nonce,string sender,string recipient,string text,string kind){
        using var db=accounts.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT COUNT(*) FROM contact_messages";if(Convert.ToInt32(cmd.ExecuteScalar())>=100000)throw new WireFault("inbox_full","服务器消息存储暂满，请稍后再试。");
        cmd.CommandText="SELECT COUNT(*) FROM contact_messages WHERE sender=$a OR recipient=$a";cmd.Parameters.AddWithValue("$a",recipient);if(Convert.ToInt32(cmd.ExecuteScalar())>=1000)throw new WireFault("inbox_full","对方的消息存储已满，暂时无法留言。");
        cmd.Parameters["$a"].Value=sender;if(Convert.ToInt32(cmd.ExecuteScalar())>=1000)throw new WireFault("inbox_full","消息存储已满，请先清理旧对话。");
        cmd.CommandText="INSERT INTO contact_messages(nonce,sender,recipient,text,kind,created) VALUES($n,$a,$p,$t,$k,$c) RETURNING sequence";
        cmd.Parameters.AddWithValue("$n",nonce);cmd.Parameters.AddWithValue("$p",recipient);cmd.Parameters.AddWithValue("$t",text);cmd.Parameters.AddWithValue("$k",kind);cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O"));long sequence=Convert.ToInt64(cmd.ExecuteScalar());tx.Commit();return Existing(sender,nonce)!;
    }
    public ContactMessage[] History(string owner,string peer,long before=0){
        using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT m.sequence,m.nonce,m.sender,m.recipient,a.display_name,m.text,m.kind,m.created,m.seen FROM contact_messages m JOIN accounts a ON a.id=m.sender WHERE ((m.sender=$a AND m.recipient=$p) OR (m.sender=$p AND m.recipient=$a)) AND ($b=0 OR m.sequence<$b) ORDER BY m.sequence DESC LIMIT 12";
        cmd.Parameters.AddWithValue("$a",owner);cmd.Parameters.AddWithValue("$p",peer);cmd.Parameters.AddWithValue("$b",before);using var row=cmd.ExecuteReader();var messages=new List<ContactMessage>();while(row.Read())messages.Add(Read(row));messages.Reverse();return messages.ToArray();
    }
    public ContactThread[] Threads(string owner){
        using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="""
            SELECT a.id,a.display_name,a.steam_id,m.text,m.created,m.sequence,
              (SELECT COUNT(*) FROM contact_messages u WHERE u.sender=a.id AND u.recipient=$a AND u.seen=0),
              (SELECT COUNT(*) FROM contact_blocks b WHERE b.owner=$a AND b.peer=a.id)
            FROM contact_messages m JOIN
              (SELECT MAX(sequence) AS seq FROM contact_messages WHERE sender=$a OR recipient=$a GROUP BY CASE WHEN sender=$a THEN recipient ELSE sender END) latest ON latest.seq=m.sequence
            JOIN accounts a ON a.id=CASE WHEN m.sender=$a THEN m.recipient ELSE m.sender END
            ORDER BY m.sequence DESC LIMIT 30
            """;cmd.Parameters.AddWithValue("$a",owner);using var row=cmd.ExecuteReader();var list=new List<ContactThread>();while(row.Read())list.Add(new(row.GetString(0),row.GetString(1),row.IsDBNull(2)?"password":"steam",row.GetString(3),row.GetString(4),row.GetInt64(5),row.GetInt32(6),row.GetInt32(7)>0));return list.ToArray();
    }
    public int Unread(string account){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM contact_messages WHERE recipient=$a AND seen=0";cmd.Parameters.AddWithValue("$a",account);return Convert.ToInt32(cmd.ExecuteScalar());}
    public void MarkRead(string owner,string peer,long through){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE contact_messages SET seen=1 WHERE recipient=$a AND sender=$p AND sequence<=$s";cmd.Parameters.AddWithValue("$a",owner);cmd.Parameters.AddWithValue("$p",peer);cmd.Parameters.AddWithValue("$s",through);cmd.ExecuteNonQuery();}
    public void Prune(){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM contact_messages WHERE created<$c";cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));cmd.ExecuteNonQuery();}
}
