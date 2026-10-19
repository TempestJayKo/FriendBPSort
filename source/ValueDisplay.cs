// UwU, format numbers independently of the game. Rich-text colors stay with wins/losses when the pair reverses.
using System.Globalization;
namespace FriendBPSort;
public static class ValueDisplay
{
    // UwU, both widths use the same tiny local units. Never clamp the denominator to a whole metre!
    public static float FitFont(float nativeSize,float availableWidth,float referenceWidth)
        =>nativeSize*System.Math.Min(1f,availableWidth/System.Math.Max(.0001f,referenceWidth))*.95f;
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

