using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightStud.Server;

public sealed class Moderation(string directory)
{
    public sealed class Rules {
        public string[] BlockedWords {get;set;}={"买卖身份证","出售银行卡","代办假证","出售枪支","出售冰毒"};
        public string[] MutedAccounts {get;set;}=Array.Empty<string>();
        public string[] BannedAccounts {get;set;}=Array.Empty<string>();
        public bool BlockLinks {get;set;}=true;
    }
    sealed class Bucket {public DateTimeOffset Since=DateTimeOffset.UtcNow,MutedUntil;public int Count,Strikes;}
    readonly ConcurrentDictionary<string,Bucket> limits=new();
    readonly string path=Path.Combine(directory,"moderation.json");
    volatile Rules rules=new();DateTime checkedAt;DateTime stamp;readonly object reloadGate=new();
    void Reload(){
        lock(reloadGate){
        if((DateTime.UtcNow-checkedAt).TotalSeconds<5)return;checkedAt=DateTime.UtcNow;
        try{
            if(!File.Exists(path)){Directory.CreateDirectory(directory);using var created=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(created,new Rules(),new JsonSerializerOptions{WriteIndented=true});}
            DateTime next=File.GetLastWriteTimeUtc(path);if(next==stamp)return;
            if(new FileInfo(path).Length>262144)return;
            var updated=JsonSerializer.Deserialize<Rules>(File.ReadAllText(path));
            if(updated!=null&&updated.BlockedWords!=null&&updated.MutedAccounts!=null&&updated.BannedAccounts!=null&&
                updated.BlockedWords.Length<=2000&&updated.MutedAccounts.Length<=2000&&updated.BannedAccounts.Length<=2000&&
                updated.BlockedWords.All(w=>w!=null&&w.Length<=120)&&updated.MutedAccounts.Concat(updated.BannedAccounts).All(w=>w!=null&&w.Length<=64)){
                rules=updated;stamp=next;
            }
        }catch(Exception error)when(error is JsonException or IOException or UnauthorizedAccessException){}
        foreach(var pair in limits.Where(p=>(DateTimeOffset.UtcNow-p.Value.Since).TotalHours>1).ToArray())limits.TryRemove(pair.Key,out _);
        }
    }
    public bool Banned(string account){Reload();return rules.BannedAccounts.Contains(account,StringComparer.Ordinal);}
    public string Check(string account,string? text){
        Reload();if(Banned(account))throw new WireFault("account_banned","该账号已被服主停用。");
        if(rules.MutedAccounts.Contains(account,StringComparer.Ordinal))throw new WireFault("muted","你当前处于禁言状态。");
        var bucket=limits.GetOrAdd(account,_=>new());var now=DateTimeOffset.UtcNow;
        if(bucket.MutedUntil>now)throw new WireFault("muted","发送过于频繁，请稍后再试。");
        if((now-bucket.Since).TotalSeconds>=10){bucket.Since=now;bucket.Count=0;}
        if(++bucket.Count>5){bucket.Strikes++;bucket.MutedUntil=now.AddSeconds(Math.Min(1800,30*bucket.Strikes));throw new WireFault("chat_rate","发送过于频繁，已暂停发言。");}
        return Content(text);
    }
    public string Content(string? text){
        Reload();string value=(text??"").Trim();
        if(value.Length is <1 or >120||value.Any(c=>char.IsControl(c)))throw new WireFault("chat_length","消息请保持在 1–120 字内。");
        value=string.Concat(value.Where(c=>char.GetUnicodeCategory(c)!=UnicodeCategory.Format));
        string normalized;try{normalized=value.Normalize(NormalizationForm.FormKC);}catch(ArgumentException){throw new WireFault("chat_filtered","消息中包含无效字符。");}
        string folded=Regex.Replace(normalized,"[\\s\\p{P}\\p{S}]+","").ToLowerInvariant();
        if(rules.BlockedWords.Any(w=>w.Length>0&&folded.Contains(Regex.Replace(w.Normalize(NormalizationForm.FormKC),"[\\s\\p{P}\\p{S}]+","").ToLowerInvariant(),StringComparison.Ordinal))||
            rules.BlockLinks&&Regex.IsMatch(normalized,"(?i)(https?://|www\\.|\\.(com|cn|net|xyz|top)\\b|[0-9]{16,19}|1[3-9][0-9]{9})"))
            throw new WireFault("chat_filtered","这条内容未发送，请调整后重试。");
        if(value.Length==0)throw new WireFault("chat_length","消息内容为空。");return value.Replace('<','＜').Replace('>','＞');
    }
}
