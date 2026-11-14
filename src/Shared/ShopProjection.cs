using System;

namespace NightStud.Shared
{
    public readonly struct ShopPlacement
    {
        public readonly double X,Y,W,H;
        public ShopPlacement(double x,double y,double w,double h){X=x;Y=y;W=w;H=h;}
    }
    public static class ShopProjection
    {
        // Source is a native inventory's top-left extent. Target is the glass
        // cabinet or counter surface seen from the other side. Keep grid units.
        public static ShopPlacement Place(ShopThing item,ShopZone source,ShopZone target){
            if(source.W<=0||source.H<=0||target.W<=0||target.H<=0)throw new ArgumentException("Invalid shop zone");
            double scale=Math.Min((double)target.W/source.W,(double)target.H/source.H);
            return new ShopPlacement(target.X+target.W*.5+(item.X-source.X-source.W*.5)*scale,
                target.Y+target.H*.5+(item.Y-source.Y-source.H*.5)*scale,item.W*scale,item.H*scale);
        }
    }
}
