namespace NightStud.Server;

public static class ServerBuild
{
    public static string Version {get;}=typeof(ServerBuild).Assembly.GetName().Version?.ToString(3)??"unknown";
}
