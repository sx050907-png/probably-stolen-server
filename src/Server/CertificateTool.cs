using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using System.Text.Json;
using System.Text.RegularExpressions;
using Directory=System.IO.Directory;

namespace NightStud.Server;

public static class CertificateTool
{
    public sealed record State(string Domain,string AccountKey,string Order,string CertificateKey);
    static void Private(string path){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
    public static bool Domain(string value)=>value.Length is >3 and <=253&&Regex.IsMatch(value,"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$");
    public static async Task Run(string[] args){
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")),folder=Path.Combine(root,"data","certificates");
        if(args.Length<1||args[0] is not ("prepare" or "finish")){
            Console.WriteLine("certificate prepare DOMAIN EMAIL|--no-email --agree-tos\ncertificate finish PORT\nDNS-01 certificate setup; keys remain in data/certificates. See DEPLOY-DNSHE.md.");return;
        }
        Directory.CreateDirectory(folder);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string statePath=Path.Combine(folder,"pending.json");
        if(args[0]=="prepare"){
            if(args.Length!=4||!Domain(args[1])||(args[2]!="--no-email"&&!System.Net.Mail.MailAddress.TryCreate(args[2],out _))||args[3]!="--agree-tos")
                throw new InvalidOperationException("Usage: certificate prepare DOMAIN EMAIL|--no-email --agree-tos; https://letsencrypt.org/repository/");
            string accountPath=Path.Combine(folder,"account.pem");
            var acme=File.Exists(accountPath)?new AcmeContext(WellKnownServers.LetsEncryptV2,KeyFactory.FromPem(File.ReadAllText(accountPath))):new AcmeContext(WellKnownServers.LetsEncryptV2);
            await acme.NewAccount(args[2]=="--no-email"?Array.Empty<string>():new[]{"mailto:"+args[2]},true);File.WriteAllText(accountPath,acme.AccountKey.ToPem());Private(accountPath);
            var order=await acme.NewOrder(new[]{args[1]});var authorization=(await order.Authorizations()).Single();var challenge=await authorization.Dns();
            var state=new State(args[1],acme.AccountKey.ToPem(),order.Location.AbsoluteUri,KeyFactory.NewKey(KeyAlgorithm.ES256).ToPem());
            File.WriteAllText(statePath,JsonSerializer.Serialize(state));Private(statePath);
            Console.WriteLine("Add this DNS TXT record in DNSHE:");Console.WriteLine("Name: _acme-challenge."+args[1]);Console.WriteLine("Value: "+acme.AccountKey.DnsTxt(challenge.Token));
            Console.WriteLine("After DNS propagation, run: certificate finish YOUR_PORT");return;
        }
        if(args.Length!=2||!int.TryParse(args[1],out int port)||port is <1 or >65535)throw new InvalidOperationException("Usage: certificate finish PORT");
        var saved=JsonSerializer.Deserialize<State>(File.ReadAllText(statePath))??throw new InvalidOperationException("Run certificate prepare first.");
        var context=new AcmeContext(WellKnownServers.LetsEncryptV2,KeyFactory.FromPem(saved.AccountKey));var pending=context.Order(new Uri(saved.Order));
        var auth=(await pending.Authorizations()).Single();var dns=await auth.Dns();await dns.Validate();
        for(int n=0;n<10;n++){
            var status=await auth.Resource();if(status.Status==AuthorizationStatus.Valid)break;
            if(status.Status==AuthorizationStatus.Invalid)throw new InvalidOperationException("DNS verification failed. Confirm TXT propagation and run prepare again.");
            if(n==9)throw new InvalidOperationException("DNS verification is pending. Run finish again after propagation.");
            await Task.Delay(3000);
        }
        var leaf=KeyFactory.FromPem(saved.CertificateKey);var certificate=await pending.Generate(new CsrInfo{CommonName=saved.Domain},leaf);
        string password=Wire.Token(),pfx=Path.Combine(folder,"server.pfx"),pass=Path.Combine(folder,"password.txt");
        File.WriteAllBytes(pfx,certificate.ToPfx(leaf).Build(saved.Domain,password));File.WriteAllText(pass,password);Private(pfx);Private(pass);
        string config=Path.Combine(root,"config.env");if(File.Exists(config))File.Copy(config,config+".before-tls-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        string[] updates={"NIGHTSTUD_SCHEME","NIGHTSTUD_PUBLIC_ORIGIN","NIGHTSTUD_PORT","ASPNETCORE_URLS","ASPNETCORE_Kestrel__Certificates__Default__Path","NIGHTSTUD_CERT_PASSWORD_FILE"};
        var lines=File.Exists(config)?File.ReadAllLines(config).Where(line=>!updates.Any(key=>line.StartsWith(key+"=",StringComparison.Ordinal))).ToList():new List<string>();
        lines.AddRange(new[]{"NIGHTSTUD_PORT="+port,"NIGHTSTUD_SCHEME=https","NIGHTSTUD_PUBLIC_ORIGIN=https://"+saved.Domain+":"+port,
            "ASPNETCORE_Kestrel__Certificates__Default__Path=\""+pfx.Replace('\\','/')+"\"","NIGHTSTUD_CERT_PASSWORD_FILE=\""+pass.Replace('\\','/')+"\""});
        File.WriteAllLines(config,lines);Private(config);
        Console.WriteLine("Certificate installed. HTTPS configuration saved; existing config backed up. Restart the server.");
        Console.WriteLine("Keep data/certificates private. Repeat prepare/finish before certificate expiry.");
    }
}
