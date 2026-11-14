using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace NightStud.Server;

public sealed class RollingLog : ILoggerProvider
{
    readonly string folder,secret;
    readonly long limit;
    readonly int days;
    readonly Channel<object> queue=Channel.CreateBounded<object>(new BoundedChannelOptions(2048){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly Task writer;
    long dropped;
    public RollingLog(string directory,string accessKey,int maxMb=8,int retentionDays=14){
        folder=Path.GetFullPath(directory);secret=accessKey;limit=Math.Clamp(maxMb,1,128)*1024L*1024;days=Math.Clamp(retentionDays,1,90);
        Directory.CreateDirectory(folder);writer=WriteLoop();
    }
    public static string Sanitize(string? text,string secret){
        string value=text??"";
        if(secret.Length>0)value=value.Replace(secret,"[redacted]",StringComparison.Ordinal);
        value=Regex.Replace(value,"(?i)\\b[0-9a-f]{64}\\b","[token]");
        value=Regex.Replace(value,"(?i)(accessKey|token|authorization|password)([\\s\"':=]+)[^\\s,}]+","$1$2[redacted]");
        return value.Length>12000?value[..12000]:value;
    }
    void Write(string category,LogLevel level,EventId id,string text,Exception? ex){
        var entry=new{time=DateTimeOffset.UtcNow.ToString("O"),level=level.ToString(),category,eventId=id.Id,
            message=Sanitize(text,secret),exception=ex?.GetType().FullName,error=ex is null?null:Sanitize(ex.Message,secret),stack=ex is null?null:Sanitize(ex.StackTrace,secret)};
        if(!queue.Writer.TryWrite(entry))Interlocked.Increment(ref dropped);
    }
    async Task WriteLoop(){
        StreamWriter? stream=null;string day="";int index=0;long size=0;
        try{
            await foreach(var entry in queue.Reader.ReadAllAsync()){
                try{
                    string today=DateTime.UtcNow.ToString("yyyyMMdd");
                    if(today!=day){
                        stream?.Dispose();stream=null;day=today;index=0;
                        foreach(string file in Directory.EnumerateFiles(folder,"nightstud-*.jsonl"))
                            if(File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddDays(-days))File.Delete(file);
                    }
                    if(stream is null || size>=limit){
                        stream?.Dispose();stream=null;
                        while(index<32){string path=Path.Combine(folder,$"nightstud-{day}-{index:D2}.jsonl");size=File.Exists(path)?new FileInfo(path).Length:0;if(size<limit){stream=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read));break;}index++;}
                        if(stream is null){Interlocked.Increment(ref dropped);continue;}
                    }
                    long missed=Interlocked.Exchange(ref dropped,0);
                    if(missed>0)await stream.WriteLineAsync(JsonSerializer.Serialize(new{time=DateTimeOffset.UtcNow,eventName="log_entries_dropped",count=missed}));
                    string line=JsonSerializer.Serialize(entry);await stream.WriteLineAsync(line);await stream.FlushAsync();size+=System.Text.Encoding.UTF8.GetByteCount(line)+1;
                }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){
                    stream?.Dispose();stream=null;Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} file_log_failure {ex.GetType().Name}");
                }
            }
        }finally{stream?.Dispose();}
    }
    public ILogger CreateLogger(string categoryName)=>new Sink(this,categoryName);
    public void Dispose(){queue.Writer.TryComplete();try{writer.Wait(TimeSpan.FromSeconds(5));}catch(AggregateException){}}
    sealed class Sink(RollingLog owner,string category):ILogger{
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Information;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter){if(IsEnabled(level))owner.Write(category,level,id,formatter(state,exception),exception);}
    }
    public static void StartupFailure(Exception error){
        try{
            string directory=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","logs"));Directory.CreateDirectory(directory);
            string message=Sanitize(error.Message,Environment.GetEnvironmentVariable("NIGHTSTUD_ACCESS_KEY")??"");
            File.AppendAllText(Path.Combine(directory,"startup-error-"+DateTime.UtcNow.ToString("yyyyMMdd")+".jsonl"),
                JsonSerializer.Serialize(new{time=DateTimeOffset.UtcNow,level="Critical",exception=error.GetType().FullName,message,stack=error.StackTrace})+Environment.NewLine);
        }catch(Exception){Console.Error.WriteLine("Startup failed; unable to create startup log.");}
    }
}

public sealed record ServerEvent(string Event,string Player,string? Room=null,string? Game=null,string? Reason=null);
