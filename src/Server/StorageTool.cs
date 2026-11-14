using Microsoft.Data.Sqlite;

namespace NightStud.Server;

public static class StorageTool
{
    public static string Backup(){
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));ServerConfig.Load(Path.Combine(root,"config.env"));
        string directory=Environment.GetEnvironmentVariable("NIGHTSTUD_DATA_DIR")??Path.Combine(root,"data"),source=Path.Combine(directory,"accounts.db");
        if(!File.Exists(source))throw new InvalidOperationException("尚未生成账号数据库，请先启动服务端。未创建空备份。");
        string destination=Path.Combine(root,"backups");Directory.CreateDirectory(destination);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(destination,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string file=Path.Combine(destination,"accounts-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+".db");
        using(var input=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Mode=SqliteOpenMode.ReadOnly}.ToString()))
        using(var output=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadWriteCreate}.ToString())){
            input.Open();output.Open();input.BackupDatabase(output);
        }
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        Console.WriteLine("数据库备份完成："+file);Console.WriteLine("另行保管 config.env、data/moderation.json 和 data/certificates。备份含私人账号数据，请勿公开上传。");
        return file;
    }
}
