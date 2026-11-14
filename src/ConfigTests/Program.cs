using NightStud.Server;
int checks=0;
void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
void Bad(Action action,string reason){bool thrown=false;try{action();}catch(InvalidOperationException){thrown=true;}Check(thrown,reason);}
string[] keys={"NIGHTSTUD_PORT","NIGHTSTUD_ACCESS_KEY","NIGHTSTUD_SCHEME","ASPNETCORE_URLS","ASPNETCORE_Kestrel__Certificates__Default__Path","NIGHTSTUD_CERT_PASSWORD_FILE","SERVER_PORT","NIGHTSTUD_PUBLIC_ORIGIN"};
var saved=keys.ToDictionary(k=>k,Environment.GetEnvironmentVariable);
string folder=Path.Combine(Directory.GetCurrentDirectory(),"work","server-config-tests");Directory.CreateDirectory(folder);
string file=Path.Combine(folder,Guid.NewGuid().ToString("N")+".env");
try{
    Environment.SetEnvironmentVariable("SERVER_PORT",null);Environment.SetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN",null);
    ServerConfig.Load(file);Check(!File.Exists(file),"missing config is optional");
    string absoluteCertificate=Path.Combine(folder,"certs","test.pfx");
    File.WriteAllText(file,"# sample\nNIGHTSTUD_PORT=18811\nNIGHTSTUD_SCHEME=http\nNIGHTSTUD_ACCESS_KEY='literal-key-01234=#'\nASPNETCORE_Kestrel__Certificates__Default__Path=\"E:/certs/test.pfx\"\n".Replace("E:/certs/test.pfx",absoluteCertificate));
    ServerConfig.Load(file);
    Check(ServerConfig.AccessKey()=="literal-key-01234=#","read quoted key as data");
    Check(Environment.GetEnvironmentVariable(keys[4])==absoluteCertificate,"certificate path");
    File.WriteAllText(file,"ASPNETCORE_Kestrel__Certificates__Default__Path=data/certificates/server.pfx\nNIGHTSTUD_CERT_PASSWORD_FILE=data/certificates/password.txt\n");
    ServerConfig.Load(file);
    Check(Environment.GetEnvironmentVariable(keys[4])==Path.Combine(folder,"data","certificates","server.pfx"),"relative certificate anchored to config folder");
    Check(Environment.GetEnvironmentVariable(keys[5])==Path.Combine(folder,"data","certificates","password.txt"),"relative certificate password anchored to config folder");
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS",null);ServerConfig.SetBinding();
    Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="http://0.0.0.0:18811","port config binding without starting listener");
    foreach(string port in new[]{"0","65536","abc","-1"}){
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS",null);Environment.SetEnvironmentVariable("NIGHTSTUD_PORT",port);Bad(ServerConfig.SetBinding,"bad port "+port);
    }
    Environment.SetEnvironmentVariable("NIGHTSTUD_PORT","18811");Environment.SetEnvironmentVariable("NIGHTSTUD_SCHEME","file");Bad(ServerConfig.SetBinding,"bad scheme");
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS","http://127.0.0.1:18741");ServerConfig.SetBinding();Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="http://127.0.0.1:18741","respect explicit binding");
    foreach(string key in new[]{"","short","replace-with-your-random-test-key",new string('a',257)}){Environment.SetEnvironmentVariable("NIGHTSTUD_ACCESS_KEY",key);Bad(()=>ServerConfig.AccessKey(),"reject unusable test key");}
    Environment.SetEnvironmentVariable("NIGHTSTUD_ACCESS_KEY",new string('b',16));Check(ServerConfig.AccessKey().Length==16,"minimum key length");
    string? originalPath=Environment.GetEnvironmentVariable("PATH");
    foreach(string text in new[]{"no-equals","=empty-name","PATH=other","NIGHTSTUD-PORT=1234"}){File.WriteAllText(file,text);Bad(()=>ServerConfig.Load(file),"invalid config data");}
    Check(Environment.GetEnvironmentVariable("PATH")==originalPath,"leave process path intact");
    Environment.SetEnvironmentVariable("SERVER_PORT","23678");Environment.SetEnvironmentVariable("NIGHTSTUD_SCHEME","https");
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS","http://0.0.0.0:18811");Environment.SetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN","https://casino.example:{port}");ServerConfig.SetBinding();
    Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="https://0.0.0.0:23678","platform port overrides stale manual binding and retains HTTPS");
    Check(Environment.GetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN")=="https://casino.example:23678","Steam callback origin follows platform port");
    Environment.SetEnvironmentVariable("NIGHTSTUD_PORT",null);ServerConfig.SetBinding();Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="https://0.0.0.0:23678","platform-only configuration works");
    foreach(string invalid in new[]{"0","65536","bad"}){Environment.SetEnvironmentVariable("SERVER_PORT",invalid);Bad(ServerConfig.SetBinding,"invalid platform port rejected");}
    Environment.SetEnvironmentVariable("SERVER_PORT",null);Environment.SetEnvironmentVariable("NIGHTSTUD_PORT","18811");Environment.SetEnvironmentVariable("ASPNETCORE_URLS","https://0.0.0.0:{port}");
    Environment.SetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN","https://casino.example:{port}");ServerConfig.SetBinding();
    Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="https://0.0.0.0:18811","self-host fallback expands port template");
    Check(Environment.GetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN")=="https://casino.example:18811","self-host callback port template");
    Environment.SetEnvironmentVariable("SERVER_PORT","23678");Environment.SetEnvironmentVariable("NIGHTSTUD_SCHEME",null);Environment.SetEnvironmentVariable("ASPNETCORE_URLS","https://0.0.0.0:18811");ServerConfig.SetBinding();
    Check(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")=="https://0.0.0.0:23678","existing HTTPS scheme retained without scheme setting");
    Console.WriteLine($"PASS {checks} configuration assertions; no listener or server process started.");
}finally{foreach(var pair in saved)Environment.SetEnvironmentVariable(pair.Key,pair.Value);if(File.Exists(file))File.Delete(file);}
