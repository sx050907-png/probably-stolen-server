using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NightStud.Server;

public sealed record AccountIdentity(string Id,string Name,string Provider);
public sealed record Privacy(bool AllowVisits=false,bool AllowInvites=true);
public sealed class Accounts
{
    public const int Iterations=600000;
    readonly string connectionString;
    readonly SemaphoreSlim hashes=new(2);
    readonly byte[] dummySalt=RandomNumberGenerator.GetBytes(16);
    public Accounts(string directory){
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string path=Path.Combine(directory,"accounts.db");
        connectionString=new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,DefaultTimeout=5}.ToString();
        using(var db=Open()){
            using var cmd=db.CreateCommand();cmd.CommandText="""
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS accounts(
                  id TEXT PRIMARY KEY, username TEXT UNIQUE, display_name TEXT NOT NULL,
                  salt BLOB, hash BLOB, iterations INTEGER, steam_id TEXT UNIQUE,
                  allow_visits INTEGER NOT NULL DEFAULT 0, allow_invites INTEGER NOT NULL DEFAULT 1,
                  created TEXT NOT NULL, banned_until TEXT);
                CREATE TABLE IF NOT EXISTS failures(username TEXT PRIMARY KEY, count INTEGER NOT NULL, until_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS trades(id TEXT PRIMARY KEY, seller TEXT NOT NULL, buyer TEXT NOT NULL,
                  payload TEXT NOT NULL, price INTEGER NOT NULL, state TEXT NOT NULL, created TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS remembered_logins(selector TEXT PRIMARY KEY, account_id TEXT NOT NULL,
                  secret_hash BLOB NOT NULL, provider TEXT NOT NULL, expires TEXT NOT NULL, created TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS remembered_account ON remembered_logins(account_id);
                CREATE TABLE IF NOT EXISTS account_appearance(account TEXT PRIMARY KEY, style TEXT NOT NULL);
                """;cmd.ExecuteNonQuery();
        }
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    public SqliteConnection Open(){var db=new SqliteConnection(connectionString);db.Open();return db;}
    public const int RememberDays=30;
    static (string Selector,byte[] Hash) RememberParts(string? token){
        if(token==null||!Regex.IsMatch(token,"^[a-f0-9]{32}\\.[a-f0-9]{64}$",RegexOptions.CultureInvariant))
            throw new WireFault("remember_expired","登录信息已失效，请重新登录。");
        return(token[..32],SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token[33..])));
    }
    static string InsertRemembered(SqliteConnection db,SqliteTransaction tx,AccountIdentity identity){
        string selector=Guid.NewGuid().ToString("N"),secret=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO remembered_logins(selector,account_id,secret_hash,provider,expires,created) VALUES($s,$i,$h,$p,$e,$c)";
        cmd.Parameters.AddWithValue("$s",selector);cmd.Parameters.AddWithValue("$i",identity.Id);
        cmd.Parameters.AddWithValue("$h",SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)));cmd.Parameters.AddWithValue("$p",identity.Provider);
        cmd.Parameters.AddWithValue("$e",DateTimeOffset.UtcNow.AddDays(RememberDays).ToString("O"));cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        return selector+"."+secret;
    }
    public string Remember(AccountIdentity identity){
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM remembered_logins WHERE expires<$now OR selector IN (SELECT selector FROM remembered_logins WHERE account_id=$i ORDER BY created DESC LIMIT -1 OFFSET 19)";
        cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$i",identity.Id);cmd.ExecuteNonQuery();
        string token=InsertRemembered(db,tx,identity);tx.Commit();return token;
    }
    public (AccountIdentity Account,string Token) Resume(string? token){
        var parts=RememberParts(token);using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT r.secret_hash,r.provider,r.expires,a.id,a.display_name,a.banned_until FROM remembered_logins r JOIN accounts a ON a.id=r.account_id WHERE r.selector=$s";
        cmd.Parameters.AddWithValue("$s",parts.Selector);AccountIdentity identity;
        using(var reader=cmd.ExecuteReader()){
            if(!reader.Read()||!CryptographicOperations.FixedTimeEquals(parts.Hash,(byte[])reader[0])||DateTimeOffset.Parse(reader.GetString(2))<=DateTimeOffset.UtcNow)
                throw new WireFault("remember_expired","登录信息已失效，请重新登录。");
            if(!reader.IsDBNull(5)&&DateTimeOffset.Parse(reader.GetString(5))>DateTimeOffset.UtcNow)throw new WireFault("account_banned","该账号暂时无法登录，请联系服主。");
            identity=new(reader.GetString(3),reader.GetString(4),reader.GetString(1));
        }
        cmd.CommandText="DELETE FROM remembered_logins WHERE selector=$s";cmd.ExecuteNonQuery();
        string rotated=InsertRemembered(db,tx,identity);tx.Commit();return(identity,rotated);
    }
    public void Forget(string? token){
        if(string.IsNullOrEmpty(token))return;var parts=RememberParts(token);using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="DELETE FROM remembered_logins WHERE selector=$s AND secret_hash=$h";
        cmd.Parameters.AddWithValue("$s",parts.Selector);cmd.Parameters.AddWithValue("$h",parts.Hash);cmd.ExecuteNonQuery();
    }
    public static string Username(string? value){string name=(value??"").Trim().ToLowerInvariant();
        if(!Regex.IsMatch(name,"^[a-z0-9_]{4,24}$",RegexOptions.CultureInvariant))throw new WireFault("bad_username","用户名需为 4–24 位字母、数字或下划线。");return name;}
    public static string Nickname(string? value){string name=(value??"").Trim();
        if(name.Length is <1 or >20 || name.Any(char.IsControl))throw new WireFault("bad_name","昵称请填 1–20 个字。");return name;}
    static void Password(string? value){if(value is null || value.Length is <10 or >128)throw new WireFault("bad_password","密码长度需为 10–128 位。");}
    public async Task<AccountIdentity> Register(string? username,string? password,string? nickname,CancellationToken cancel=default){
        string user=Username(username),name=Nickname(nickname);Password(password);
        if(!await hashes.WaitAsync(0,cancel))throw new WireFault("auth_busy","登录人数较多，请稍后再试。");
        try{
            byte[] salt=RandomNumberGenerator.GetBytes(16),hash=Rfc2898DeriveBytes.Pbkdf2(password!,salt,Iterations,HashAlgorithmName.SHA256,32);
            using var db=Open();using var cmd=db.CreateCommand();string id=Guid.NewGuid().ToString("N");
            cmd.CommandText="INSERT INTO accounts(id,username,display_name,salt,hash,iterations,created) VALUES($id,$u,$n,$s,$h,$i,$c)";
            cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$u",user);cmd.Parameters.AddWithValue("$n",name);
            cmd.Parameters.AddWithValue("$s",salt);cmd.Parameters.AddWithValue("$h",hash);cmd.Parameters.AddWithValue("$i",Iterations);cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O"));
            try{cmd.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==19){throw new WireFault("username_taken","这个用户名已注册，请换一个或直接登录。");}
            return new(id,name,"password");
        }finally{hashes.Release();}
    }
    public async Task<AccountIdentity> Login(string? username,string? password,CancellationToken cancel=default){
        string user=Username(username);Password(password);
        if(!await hashes.WaitAsync(0,cancel))throw new WireFault("auth_busy","登录人数较多，请稍后再试。");
        try{
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT count,until_at FROM failures WHERE username=$u";cmd.Parameters.AddWithValue("$u",user);
            using(var reader=cmd.ExecuteReader())if(reader.Read()&&reader.GetInt32(0)>=6&&DateTimeOffset.Parse(reader.GetString(1))>DateTimeOffset.UtcNow)
                throw new WireFault("login_cooldown","尝试次数过多，请十五分钟后再试。");
            cmd.CommandText="SELECT id,display_name,salt,hash,iterations,banned_until FROM accounts WHERE username=$u";
            string? id=null,name=null,banned=null;byte[] salt=dummySalt,expected=new byte[32];int rounds=Iterations;
            using(var reader=cmd.ExecuteReader())if(reader.Read()){
                id=reader.GetString(0);name=reader.GetString(1);salt=(byte[])reader[2];expected=(byte[])reader[3];rounds=reader.GetInt32(4);banned=reader.IsDBNull(5)?null:reader.GetString(5);
            }
            byte[] actual=Rfc2898DeriveBytes.Pbkdf2(password!,salt,rounds,HashAlgorithmName.SHA256,32);
            if(!CryptographicOperations.FixedTimeEquals(actual,expected)||id==null){
                cmd.CommandText="INSERT INTO failures(username,count,until_at) VALUES($u,1,$t) ON CONFLICT(username) DO UPDATE SET count=CASE WHEN until_at<$now THEN 1 ELSE count+1 END,until_at=$t";
                cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.AddMinutes(15).ToString("O"));cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
                throw new WireFault("login_failed","用户名或密码不正确。");
            }
            if(banned!=null&&DateTimeOffset.Parse(banned)>DateTimeOffset.UtcNow)throw new WireFault("account_banned","该账号暂时无法登录，请联系服主。");
            cmd.CommandText="DELETE FROM failures WHERE username=$u";cmd.ExecuteNonQuery();
            return new(id,name!,"password");
        }finally{hashes.Release();}
    }
    public AccountIdentity Steam(string steamId,string nickname){
        if(!Regex.IsMatch(steamId,"^[0-9]{17}$"))throw new WireFault("steam_invalid","Steam 身份验证失败。");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,display_name,banned_until FROM accounts WHERE steam_id=$s";cmd.Parameters.AddWithValue("$s",steamId);
        using(var reader=cmd.ExecuteReader())if(reader.Read()){
            if(!reader.IsDBNull(2)&&DateTimeOffset.Parse(reader.GetString(2))>DateTimeOffset.UtcNow)throw new WireFault("account_banned","该账号暂时无法登录，请联系服主。");
            return new(reader.GetString(0),reader.GetString(1),"steam");
        }
        var result=new AccountIdentity(Guid.NewGuid().ToString("N"),Nickname(nickname),"steam");
        cmd.CommandText="INSERT INTO accounts(id,display_name,steam_id,created) VALUES($i,$n,$s,$c)";
        cmd.Parameters.AddWithValue("$i",result.Id);cmd.Parameters.AddWithValue("$n",result.Name);cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O"));
        try{cmd.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==19){return Steam(steamId,nickname);}return result;
    }
    public Privacy GetPrivacy(string? account){
        if(account==null)return new();using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT allow_visits,allow_invites FROM accounts WHERE id=$i";cmd.Parameters.AddWithValue("$i",account);
        using var reader=cmd.ExecuteReader();return reader.Read()?new(reader.GetBoolean(0),reader.GetBoolean(1)):new();
    }
    public void SetPrivacy(string account,Privacy value){
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE accounts SET allow_visits=$v,allow_invites=$n WHERE id=$i";
        cmd.Parameters.AddWithValue("$v",value.AllowVisits);cmd.Parameters.AddWithValue("$n",value.AllowInvites);cmd.Parameters.AddWithValue("$i",account);cmd.ExecuteNonQuery();
    }
    public string GetAppearance(string account){
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT style FROM account_appearance WHERE account=$a";cmd.Parameters.AddWithValue("$a",account);
        var style=cmd.ExecuteScalar() as string;return NightStud.Shared.VisitLooks.Style(style!)?style!:"hood_ash";
    }
    public void SetAppearance(string account,string style){
        if(!NightStud.Shared.VisitLooks.Style(style))throw new WireFault("bad_appearance","请选择列表里的玩家形象。");
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO account_appearance(account,style) VALUES($a,$s) ON CONFLICT(account) DO UPDATE SET style=excluded.style";
        cmd.Parameters.AddWithValue("$a",account);cmd.Parameters.AddWithValue("$s",style);cmd.ExecuteNonQuery();
    }
}
