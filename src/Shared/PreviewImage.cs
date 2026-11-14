namespace NightStud.Shared
{
    internal static class PreviewImage
    {
        internal static bool Valid(byte[] bytes){
            if(bytes==null||bytes.Length<16||bytes.Length>80000||bytes[0]!=255||bytes[1]!=216)return false;
            bool found=false;
            for(int p=2;p+8<bytes.Length;){
                if(bytes[p++]!=255)return false;while(p<bytes.Length&&bytes[p]==255)p++;if(p>=bytes.Length)return false;int marker=bytes[p++];
                if(marker==218)return found;if(marker==217)return false;if(marker>=208&&marker<=215)continue;
                if(p+2>bytes.Length)return false;int length=bytes[p]*256+bytes[p+1];if(length<2||p+length>bytes.Length)return false;
                if(marker==192||marker==193||marker==194){if(found||length<8)return false;int h=bytes[p+3]*256+bytes[p+4],w=bytes[p+5]*256+bytes[p+6];if(w<1||w>960||h<1||h>540)return false;found=true;}
                p+=length;
            }return false;
        }
    }
}
