using System.Globalization;
namespace FriendBPSort;
public static class ValueDisplay
{
    public static string Pair(long wins,long losses,bool lossesFirst)
    {
        if(wins<0 || losses<0)return "—";
        string w="<color=#FFD700>"+wins.ToString(CultureInfo.InvariantCulture)+"</color>";
        string l="<color=#FF3030>"+losses.ToString(CultureInfo.InvariantCulture)+"</color>";
        return lossesFirst?l+"/"+w:w+"/"+l;
    }
    public static string Format(SortMode mode,long value)
    {
        if(mode!=SortMode.BattlePoints && mode!=SortMode.Wins && mode!=SortMode.Losses && mode!=SortMode.TotalMatches)return "";
        if(value<0)return "—";
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
