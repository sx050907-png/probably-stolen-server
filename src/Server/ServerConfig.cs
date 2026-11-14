namespace NightStud.Server;

public static class ServerConfig
{
    public static string AccessKey()
    {
        string key = Environment.GetEnvironmentVariable("NIGHTSTUD_ACCESS_KEY") ?? "";
        if (key.Length is < 16 or > 256 || key.StartsWith("replace-", StringComparison.Ordinal))
            throw new InvalidOperationException("Set NIGHTSTUD_ACCESS_KEY to 16–256 characters before starting; replace the example key.");
        return key;
    }
    // The operator owns config.env. Read key/value data without executing a shell.
    public static void Load(string file)
    {
        if (!File.Exists(file)) return;
        int lineNumber = 0;
        foreach (string raw in File.ReadLines(file))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int at = line.IndexOf('=');
            if (at < 1) throw new InvalidOperationException($"config.env 第 {lineNumber} 行格式无效。");
            string name = line[..at], value = line[(at + 1)..].Trim();
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                !(name.StartsWith("NIGHTSTUD_", StringComparison.Ordinal) || name.StartsWith("ASPNETCORE_", StringComparison.Ordinal)))
                throw new InvalidOperationException($"config.env 第 {lineNumber} 行配置名称无效。");
            if (value.Length >= 2 && ((value[0] == '\"' && value[^1] == '\"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
            if(value.Length>0&&(name=="ASPNETCORE_Kestrel__Certificates__Default__Path"||name=="NIGHTSTUD_CERT_PASSWORD_FILE")&&!Path.IsPathRooted(value))
                value=Path.GetFullPath(value,Path.GetDirectoryName(Path.GetFullPath(file))!);
            Environment.SetEnvironmentVariable(name, value);
        }
    }
    public static void SetBinding()
    {
        string? urls=Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),platformPort=Environment.GetEnvironmentVariable("SERVER_PORT"),origin=Environment.GetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN");
        bool platform=!string.IsNullOrEmpty(platformPort),template=urls?.Contains("{port}",StringComparison.Ordinal)==true,originTemplate=origin?.Contains("{port}",StringComparison.Ordinal)==true;
        int port=0;
        if(platform||string.IsNullOrEmpty(urls)||template||originTemplate){
            string name=platform?"SERVER_PORT":"NIGHTSTUD_PORT";
            if(!int.TryParse(platform?platformPort:Environment.GetEnvironmentVariable(name),out port)||port is <1 or >65535)
                throw new InvalidOperationException(name+" 需要有效端口，范围 1–65535。");
        }
        string number=port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(platform||string.IsNullOrEmpty(urls)){
            string scheme=Environment.GetEnvironmentVariable("NIGHTSTUD_SCHEME")??(urls?.StartsWith("https://",StringComparison.OrdinalIgnoreCase)==true?"https":"http");
            if(scheme is not ("http" or "https"))throw new InvalidOperationException("NIGHTSTUD_SCHEME 请选择 http 或 https。");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS",$"{scheme}://0.0.0.0:{number}");
        }else if(template)Environment.SetEnvironmentVariable("ASPNETCORE_URLS",urls!.Replace("{port}",number,StringComparison.Ordinal));
        if(originTemplate)Environment.SetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN",origin!.Replace("{port}",number,StringComparison.Ordinal));
    }
}
