using System;

namespace NightStud.Shared
{
    public static class VisitLooks
    {
        public static readonly string[] Styles={"hood_ash","hood_olive","hood_ink","hood_sand"};
        public static readonly string[] Names={"旧站灰","橄榄绿","深蓝灰","铁锈棕"};
        public static readonly string[] Directions={"front","front_three_quarter","side_left","back_three_quarter","back"};
        public static bool Style(string id)=>id!=null&&Array.IndexOf(Styles,id)>=0;
        public static bool Facing(string id)=>id!=null&&Array.IndexOf(Directions,id)>=0;
    }
}
