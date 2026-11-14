using System.Text.Json;
using NightStud.Shared;

namespace NightStud.Server;

public sealed class Announcements(string directory){
    ServerNotice? current;
    public ServerNotice? Read(){
        string path=Path.Combine(directory,"announcement.json");if(!File.Exists(path))return current=null;
        try{
            if(new FileInfo(path).Length>8192)return current;
            using var doc=JsonDocument.Parse(File.ReadAllText(path));var r=doc.RootElement;
            if(ServerNotice.TryCreate(r.GetProperty("id").GetString()!,r.GetProperty("title").GetString()!,r.GetProperty("body").GetString()!,r.GetProperty("updatedAt").GetString()!,out var notice))current=notice;
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){}
        return current;
    }
    public static void Command(string[] args){
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));ServerConfig.Load(Path.Combine(root,"config.env"));
        string directory=Environment.GetEnvironmentVariable("NIGHTSTUD_DATA_DIR")??Path.Combine(root,"data");Directory.CreateDirectory(directory);string path=Path.Combine(directory,"announcement.json");
        if(args.Length==1&&args[0]=="clear"){if(File.Exists(path))File.Delete(path);Console.WriteLine("公告已撤下。");return;}
        if(args.Length!=2||!ServerNotice.TryCreate(Guid.NewGuid().ToString("N"),args[0],args[1],DateTimeOffset.UtcNow.ToString("O"),out var notice))throw new ArgumentException("用法：announce 标题 正文；标题最多 60 字，正文最多 1200 字。announce clear 撤下。");
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temporary,Wire.Encode(notice));File.Move(temporary,path,true);Console.WriteLine("文字公告已发布，运行中的服务器将在 1 秒内读取。");
    }
}
