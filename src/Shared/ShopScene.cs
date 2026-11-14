using System;

namespace NightStud.Shared
{
    // Design coordinates match the native 960 x 540 shop artwork. No remote
    // filenames, textures, executable callbacks or inventory payloads here.
    public sealed class ShopScene
    {
        public string Generation {get;set;}="";
        public bool DoorOpen {get;set;}
        public bool CounterBusy {get;set;}
        public bool CounterStaging {get;set;}
        public ShopLayer[] Layers {get;set;}=Array.Empty<ShopLayer>();
        public ShopThing[] Items {get;set;}=Array.Empty<ShopThing>();
        public ShopZone[] Zones {get;set;}=Array.Empty<ShopZone>();
    }
    public sealed class ShopZone
    {
        public string Id {get;set;}="";
        public int X {get;set;}
        public int Y {get;set;}
        public int W {get;set;}
        public int H {get;set;}
    }
    public sealed class ShopLayer
    {
        public string Art {get;set;}="";
        public int X {get;set;}
        public int Y {get;set;}
        public int W {get;set;}
        public int H {get;set;}
        public int Order {get;set;}
        public bool FlipX {get;set;}
        public bool FlipY {get;set;}
        public long Color {get;set;}=4294967295;
    }
    public sealed class ShopThing
    {
        public string Id {get;set;}="";
        public string Kind {get;set;}="";
        public string Zone {get;set;}="";
        public int Count {get;set;}
        public int Quote {get;set;}
        public int Flags {get;set;}
        // Older publishers only sent tradable goods. Display-only goods never
        // enter GoodsLedger or the transfer codec.
        public bool CanTrade {get;set;}=true;
        public int Orientation {get;set;}
        public bool Flipped {get;set;}
        public int X {get;set;}
        public int Y {get;set;}
        public int W {get;set;}
        public int H {get;set;}
    }
}
