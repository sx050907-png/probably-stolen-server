using System.Text.Json;

namespace NightStud.Server;

public sealed record ServiceState(string Status,string Message,int RetryAfterSeconds=60);
public sealed class ServiceStatus(string directory) {
    public static readonly ServiceState Online=new("online","");
    ServiceState current=Online;
    public ServiceState Read(){
        if(Environment.GetEnvironmentVariable("NIGHTSTUD_MAINTENANCE")=="1")return new("maintenance","服务器正在维护，请稍后再试。");
        string file=Path.Combine(directory,"service-status.json");
        if(!File.Exists(file))return current=Online;
        try{
            var info=new FileInfo(file);if(info.Length>4096)return current;
            using var json=JsonDocument.Parse(File.ReadAllText(file));var root=json.RootElement;
            string? state=root.GetProperty("status").GetString();if(state is not ("online" or "maintenance" or "closed"))return current;
            string text=root.TryGetProperty("message",out var body)?body.GetString()??"":"";
            text=new string(text.Where(c=>!char.IsControl(c)).Take(140).ToArray());
            if(state=="maintenance"&&text.Length==0)text="服务器正在维护，请稍后再试。";
            if(state=="closed"&&text.Length==0)text="服务器已停止提供联机服务。";
            return current=new(state,text);
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){return current;}
    }
    public static void Command(string[] args){
        if(args.Length<1||args[0] is not ("online" or "maintenance" or "closed"))throw new ArgumentException("用法：service online|maintenance|closed [提示文字]");
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));ServerConfig.Load(Path.Combine(root,"config.env"));
        string folder=Environment.GetEnvironmentVariable("NIGHTSTUD_DATA_DIR")??Path.Combine(root,"data");Directory.CreateDirectory(folder);
        string message=string.Join(' ',args.Skip(1));if(message.Length>140)throw new ArgumentException("提示文字最多 140 字。");
        string target=Path.Combine(folder,"service-status.json"),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(new{status=args[0],message},Wire.Json));File.Move(temporary,target,true);
        Console.WriteLine("服务状态已设为 "+args[0]+"；运行中的服务器将在 1 秒内读取。");
    }
}
