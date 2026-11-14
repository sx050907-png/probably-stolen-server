namespace NightStud.Shared
{
    public static class GamePayloadBudget
    {
        public const int RequestStringBytes=128000,ResponseStringBytes=180000;
        // System.Text.Json's default BasicLatin encoder. Non-ASCII UTF-16
        // units use six bytes; a surrogate pair uses twelve. Verified against
        // the server runtime. Leave room for envelope and metadata fields.
        public static int ServerStringBytes(string value){
            int bytes=2;
            foreach(char c in value){
                if(c<' '||c>'~'||c=='"'||c=='&'||c=='\''||c=='+'||c=='<'||c=='>'||c=='`')bytes+=6;
                else bytes+=c=='\\'?2:1;
            }
            return bytes;
        }
    }
}
