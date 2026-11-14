#nullable disable
using System;
using System.Globalization;
using System.Linq;

namespace NightStud.Shared {
    // Data only: no link targets, file paths, actions, script or executable fields.
    public sealed class ServerNotice {
        public string Id {get;private set;}
        public string Title {get;private set;}
        public string Body {get;private set;}
        public string UpdatedAt {get;private set;}
        ServerNotice(){}
        public static bool TryCreate(string id,string title,string body,string updatedAt,out ServerNotice notice){
            notice=null;
            if(string.IsNullOrWhiteSpace(id)||id.Length>64||id.Any(c=>!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')||c=='-'||c=='_')))return false;
            if(string.IsNullOrWhiteSpace(title)||title.Length>60||title.Any(char.IsControl)||string.IsNullOrWhiteSpace(body)||body.Length>1200||body.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t'))return false;
            if(!DateTimeOffset.TryParseExact(updatedAt,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))return false;
            notice=new ServerNotice{Id=id,Title=title,Body=body,UpdatedAt=updatedAt};return true;
        }
    }
}
