using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLoader;
using Il2CppRUMBLE.Social;
using Il2CppRUMBLE.Social.Phone;
using Il2CppRUMBLE.Interactions.InteractionBase;
using Il2CppRUMBLE.Players;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using Object=UnityEngine.Object;
[assembly:MelonInfo(typeof(FriendBPSort.Mod),"FriendBPSort","0.3.7","Colli",null)]
[assembly:MelonGame("Buckethead Entertainment","RUMBLE")]
namespace FriendBPSort;
public sealed class Mod:MelonMod
{
    private sealed class SideButton {internal GameObject Object;internal TMP_Text Label;internal SortMode Mode;internal string Caption;}
    private static readonly Dictionary<int,FriendList> lists=new();
    private static readonly Dictionary<int,List<SideButton>> controls=new();
    private static readonly Dictionary<string,int> points=new(StringComparer.Ordinal);
    private sealed class RowValue
    {
        internal PlayerTag Tag;
        internal GameObject Box;
        internal TMP_Text Text;
        internal string Master,Title,Key;
        internal bool Captured,Applied;
        internal Vector3 BPPosition,PlatePosition,PlateScale;
        internal Vector2 BPSize;
        internal float BPFont;
        internal bool BPAuto;
        internal TextAlignmentOptions BPAlignment;
        internal Transform Plate;
        internal Bounds PlateBounds;

    }
    // UwU, instance IDs identify reusable visual slots; Master/Title identify the player currently occupying each slot.
    private static readonly Dictionary<int,RowValue> rowValues=new();
    private static readonly string[] captions={"SORT A–Z","SORT BY BP","FRIEND ADDED","RECENTLY PLAYED","WLR","TOTAL MATCHES"};
    private static readonly MatchStats matches=new();
    private static readonly Dictionary<int,string> summaries=new();
    private static bool inverse,matchWarning;
    private static bool MatchInfoPresent=>MelonMod.RegisteredMelons.Any(m=>string.Equals(m.Info?.Name,"MatchInfo",StringComparison.OrdinalIgnoreCase));
    private static float nextMatchRead;
    private static System.IO.FileSystemWatcher matchWatcher;
    private static int matchFileChanged=1;
    private static int matchReadRetries;
    private static string MatchPath=>System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory,"MatchInfo","MatchInfo.txt");
    // UwU, monitor the optional file only while MatchInfo is loaded; callbacks set an atomic flag rather than touching Unity.
    private static void WatchMatches(bool present)
    {
        if(!present){matchWatcher?.Dispose();matchWatcher=null;return;}
        if(matchWatcher!=null)return;
        string folder=System.IO.Path.GetDirectoryName(MatchPath);
        if(!System.IO.Directory.Exists(folder))return;
        matchWatcher=new System.IO.FileSystemWatcher(folder,"MatchInfo.txt");
        matchWatcher.NotifyFilter=System.IO.NotifyFilters.LastWrite|System.IO.NotifyFilters.Size|System.IO.NotifyFilters.FileName;
        // 🐱 File callbacks only queue work; all game/UI changes run on the main thread.
        matchWatcher.Changed+=(_,__)=>System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
        matchWatcher.Created+=(_,__)=>System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
        matchWatcher.Deleted+=(_,__)=>System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
        matchWatcher.Renamed+=(_,__)=>System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
        matchWatcher.Error+=(_,__)=>System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
        matchWatcher.EnableRaisingEvents=true;
        System.Threading.Interlocked.Exchange(ref matchFileChanged,1);
    }
    private static MelonPreferences_Entry<bool> savedInverse;
    private static bool IsMatch(SortMode value)=>value==SortMode.Wins || value==SortMode.Losses || value==SortMode.TotalMatches;
    private static SortHistory history=new();
    private static SortMode mode;
    private static MelonPreferences_Category preferences;
    private static MelonPreferences_Entry<int> savedMode;
    private static MelonPreferences_Entry<string> savedHistory;
    private static bool dirty,historyDirty,stopped,warnedMissingAsset,resetPage;
    private static float nextTick,nextAssetSearch,nextSave;
    private static GameObject staging;

    private static InteractionButton stoneButton;
    private static bool rendering;
    private static string Key(UserSearchResult f)=>!string.IsNullOrWhiteSpace(f?.PlayFabMasterId)?f.PlayFabMasterId:f?.PlayFabTitleId??"";
    private static string Key(UserData u)=>!string.IsNullOrWhiteSpace(u?.playFabMasterId)?u.playFabMasterId:u?.playFabTitleId??"";
    private static int BP(UserSearchResult f)=>points.TryGetValue(Key(f),out int bp)?bp:-1;
    // UwU, create saved preferences, restore history, and patch only the native refresh/encounter entry points.
    public override void OnInitializeMelon()
    {
        preferences=MelonPreferences.CreateCategory("FriendBPSort");
        savedMode=preferences.CreateEntry("SortMode",0);savedInverse=preferences.CreateEntry("Inverse",false);inverse=savedInverse.Value;
        savedHistory=preferences.CreateEntry("SortHistory","");
        mode=Enum.IsDefined(typeof(SortMode),savedMode.Value)?(SortMode)savedMode.Value:SortMode.Alphabetical;
        if(!string.IsNullOrWhiteSpace(savedHistory.Value))try
        {
            history=JsonSerializer.Deserialize<SortHistory>(savedHistory.Value)??new();
            history.KnownFriends??=new();history.Added??=new();history.Played??=new();
        }
        catch(Exception ex){MelonLogger.Warning("🐱 Sort history could not be read: "+ex.Message);history=new();}
        var flags=BindingFlags.Static|BindingFlags.NonPublic;
        HarmonyInstance.Patch(typeof(FriendList).GetMethod("Refresh"),prefix:new HarmonyMethod(typeof(Mod).GetMethod(nameof(RefreshUnified),flags)));
        HarmonyInstance.Patch(typeof(FriendHandler).GetMethod("AddRecentlyMetPlayer"),postfix:new HarmonyMethod(typeof(Mod).GetMethod(nameof(MetPlayer),flags)));
        MelonLogger.Msg("FriendBPSort: four sort buttons ready; two additional match-stat buttons require MatchInfo. Historical friend dates are unknown; new additions and encounters are tracked locally.");
    }
    // Mew, copy BP from profiles the game already fetched; never create temporary profile objects or network requests.
    private static bool Snapshot()
    {
        bool changed=false;var data=UserDataManager.userDataList;
        if(data!=null)foreach(var user in data)
        {
            if(user==null || user.battlePoints<0)continue;
            string id=Key(user);if(id.Length==0)continue;
            if(!points.TryGetValue(id,out int old) || old!=user.battlePoints){points[id]=user.battlePoints;changed=true;}
        }
        return changed;
    }
    // UwU, send stable IDs and the chosen metric to our comparator; unknown values always sort last.
    private static int CompareFriends(GetFriendsResult a,GetFriendsResult b)
    {
        return Ranking.Compare(mode,BP(a),a?.PublicName,Key(a),IsMatch(mode)?matches.Value(mode,a?.PlayFabMasterId):history.Date(mode,a?.PlayFabMasterId,a?.PlayFabTitleId),BP(b),b?.PublicName,Key(b),IsMatch(mode)?matches.Value(mode,b?.PlayFabMasterId):history.Date(mode,b?.PlayFabMasterId,b?.PlayFabTitleId),inverse);
    }
    // Mew, sort the full friend list before paging so online status cannot override the requested order. The rendering guard prevents recursive refresh.
    private static bool RefreshUnified(FriendList __instance)
    {
        if(rendering)return false;
        EnsureAvailableSort();
        lists[__instance.GetInstanceID()]=__instance;
        var items=__instance.ScrollableItems;if(items==null)return true;
        if(items.Any(item=>item?.TryCast<PlayerTag>()?.isInitializing==true)){dirty=true;return false;}
        rendering=true;
        try
        {
            Snapshot();
            var native=FriendHandler.GetFriendsWithTag(__instance.listeningToTags);
            if(native==null)return true;
            // Sort one complete sequence before paging: native online/offline groups
            // must not independently sort and then put all online players first.
            var ordered=new List<GetFriendsResult>();foreach(var friend in native)if(friend!=null)ordered.Add(friend);
            ordered.Sort(CompareFriends);
            string summary="NO MATCH DATA";
            if(IsMatch(mode) && ordered.Count>0)
            {
                var top=ordered[0];var record=matches.Find(top.PlayFabMasterId);
                if(record!=null)
                {
                    string name=Ranking.PlainName(top.PublicName).Replace("\n"," ").Replace("\r"," ");
                    if(name.Length>12)name=name.Substring(0,12)+"…";
                    summary=name+": "+record.Wins+"W / "+record.Losses+"L";
                    if(mode==SortMode.TotalMatches)summary=name+": "+record.Total+" MATCHES";
                }
            }
            summaries[__instance.GetInstanceID()]=summary;
            __instance.SetListSize(ordered.Count);
            __instance.topItemNo=Math.Max(0,Math.Min(__instance.topItemNo,Math.Max(0,ordered.Count-items.Length)));
            for(int i=0;i<items.Length;i++)
            {
                var tag=items[i]?.TryCast<PlayerTag>();if(!tag)continue;
                int index=__instance.topItemNo+i;bool visible=index<ordered.Count;
                if(tag.gameObject.activeSelf!=visible)tag.gameObject.SetActive(visible);
                if(!visible)continue;
                // Only initialize the real, persistent visible rows. No temporary profile clones.
                tag.Initialize(ordered[index],(Il2CppSystem.Action<bool>)(Action<bool>)(_=>{}));
                BindRow(tag,ordered[index]);
            }
            return false;
        }
        catch(Exception ex)
        {MelonLogger.Warning("🐱 Unified friend sort failed: "+ex.GetBaseException().Message);return true;}
        finally{rendering=false;}
    }
    // UwU, timestamp an encounter; this means recently met, not proof of a completed match.
    private static void MetPlayer(string __0)
    {
        if(string.IsNullOrWhiteSpace(__0))return;
        history.RecordPlayed(__0,DateTime.UtcNow.Ticks);historyDirty=true;
        if(mode==SortMode.RecentlyPlayed)dirty=true;
    }
    // Mew, fall back to A-Z if the optional statistics provider disappears, including a saved match-only sort.
    private static void EnsureAvailableSort()
    {
        if(IsMatch(mode) && !MatchInfoPresent)
        {mode=SortMode.Alphabetical;inverse=false;savedMode.Value=(int)mode;savedInverse.Value=false;dirty=true;historyDirty=true;resetPage=true;}
    }
    // UwU, a second press reverses the active sort; WLR has four states. Save state and reset paging once.
    private static void Choose(SortMode chosen)
    {
        if(IsMatch(chosen) && !MatchInfoPresent)return;
        if(chosen==SortMode.Wins && (mode==SortMode.Wins || mode==SortMode.Losses))
        {if(!inverse)inverse=true;else{inverse=false;mode=mode==SortMode.Wins?SortMode.Losses:SortMode.Wins;}}
        else if(mode==chosen)inverse=!inverse;
        else {mode=chosen;inverse=false;}
        savedMode.Value=(int)mode;savedInverse.Value=inverse;dirty=true;historyDirty=true;resetPage=true;
    }
    // Mew, persist history and selection together; throttling in OnUpdate avoids a disk write on every frame.
    private static void Save()
    {
        try{savedHistory.Value=JsonSerializer.Serialize(history);preferences.SaveToFile(false);historyDirty=false;}
        catch(Exception ex){MelonLogger.Warning("🐱 Could not save sorting preferences: "+ex.Message);}
    }
    // UwU, run lightweight work twice a second. File notifications only queue work; native game objects stay on this main thread.
    public override void OnUpdate()
    {
        if(stopped || Time.unscaledTime<nextTick)return;nextTick=Time.unscaledTime+.5f;
        try
        {
            EnsureAvailableSort();
            bool matchInfoPresent=MatchInfoPresent;
            WatchMatches(matchInfoPresent);
            // 🐱 Remove optional controls even on inactive boards when the mod is absent.
            if(!matchInfoPresent)
                foreach(var board in controls.Values)
                    foreach(var button in board.Where(b=>IsMatch(b.Mode)).ToArray())
                    {if(button.Object){button.Object.SetActive(false);Object.Destroy(button.Object);}board.Remove(button);}
            if(Snapshot() && mode==SortMode.BattlePoints)dirty=true;
            if(System.Threading.Interlocked.Exchange(ref matchFileChanged,0)!=0){matchReadRetries=3;nextMatchRead=Time.unscaledTime+.5f;}
            if(matchInfoPresent && matchReadRetries>0 && Time.unscaledTime>=nextMatchRead)
            {
                nextMatchRead=Time.unscaledTime+1;matchReadRetries--;
                try
                {
                    if(matches.Reload(MatchPath) && IsMatch(mode))dirty=true;
                    matchWarning=false;
                }
                catch(Exception ex)
                {if(!matchWarning)MelonLogger.Warning("🐱 MatchInfo data unavailable: "+ex.Message);matchWarning=true;}
            }
            var friends=FriendHandler.ConfirmedFriends;
            if(friends!=null)
            {
                var ids=new List<string>();foreach(var friend in friends)ids.Add(Key(friend));
                if(history.ObserveFriends(ids,DateTime.UtcNow.Ticks))
                {historyDirty=true;if(mode==SortMode.FriendAdded)dirty=true;}
            }
            if(historyDirty && Time.unscaledTime>=nextSave){nextSave=Time.unscaledTime+5;Save();}
            foreach(int id in lists.Where(p=>!p.Value).Select(p=>p.Key).ToArray())
            {
                if(controls.TryGetValue(id,out var old))foreach(var button in old)if(button.Object)Object.Destroy(button.Object);
                controls.Remove(id);lists.Remove(id);summaries.Remove(id);
            }
            bool deferred=false;
            foreach(var pair in lists.ToArray())
            {
                var list=pair.Value;if(!list.gameObject.activeInHierarchy){deferred|=dirty;continue;}
                var items=list.ScrollableItems;if(items==null){deferred|=dirty;continue;}
                if(dirty)
                {
                    if(items.Any(item=>item?.TryCast<PlayerTag>()?.isInitializing==true))deferred=true;
                    else {if(resetPage)list.topItemNo=0;list.Refresh();}
                }
                if(!controls.TryGetValue(pair.Key,out var buttons))controls[pair.Key]=buttons=new();
                buttons.RemoveAll(b=>!b.Object);
                var first=items.Select(i=>i?.TryCast<PlayerTag>()).FirstOrDefault(t=>t && t.username && t.moreInfoButton);
                if(!first)continue;
                // One column of six controls per board, independent of recycled friend rows.
                foreach(SortMode choice in new[]{SortMode.Alphabetical,SortMode.BattlePoints,SortMode.FriendAdded,SortMode.RecentlyPlayed,SortMode.Wins,SortMode.TotalMatches})
                    if((matchInfoPresent || !IsMatch(choice)) && !buttons.Any(b=>b.Mode==choice))
                    {var button=MakeButton(first,list,choice);if(button!=null)buttons.Add(button);}
                foreach(var button in buttons)
                {
                    bool selected=button.Mode==mode || button.Mode==SortMode.Wins && mode==SortMode.Losses;
                    string title=captions[(int)button.Mode];
                    if(selected)
                    {
                        title=mode==SortMode.Alphabetical?(inverse?"SORT Z–A":"SORT A–Z"):
                            mode==SortMode.Wins?"WLR WINS":mode==SortMode.Losses?"WLR LOSSES":title;
                        if(mode!=SortMode.Alphabetical)title+=inverse?" ↑":" ↓";
                        title+=" ✓";
                        if(IsMatch(mode))title+="\n<size=70%>"+(summaries.TryGetValue(pair.Key,out var info)?info:"NO MATCH DATA")+"</size>";
                    }
                    if(button.Caption==title)continue;
                    button.Label.text=title;
                    button.Label.color=selected?new Color(1,.8f,.15f):Color.white;button.Caption=title;
                }
            }
            dirty=deferred;
            if(!deferred)resetPage=false;
            UpdateRowValues();
        }
        catch(Exception ex){MelonLogger.Warning("🐱 FriendBPSort: "+ex.GetBaseException().Message);nextTick=Time.unscaledTime+5;}
    }
    // Mew, recycled rows get new player IDs; hide their old value until native profile initialization finishes.
    private static void BindRow(PlayerTag tag,GetFriendsResult friend)
    {
        int id=tag.GetInstanceID();
        if(!rowValues.TryGetValue(id,out var row))rowValues[id]=row=new RowValue{Tag=tag};
        row.Master=friend.PlayFabMasterId;row.Title=friend.PlayFabTitleId;row.Key=Key(friend);
        // Hide recycled row values until the native profile initialization finishes.
        if(row.Box)row.Box.SetActive(false);
        RestoreRow(row);
    }
    // UwU, keep labels attached to the current player, restore native BP when hidden, and constrain text to the row.
    private static void UpdateRowValues()
    {
        foreach(var pair in rowValues.ToArray())
        {
            var row=pair.Value;var tag=row.Tag;
            if(!tag){if(row.Box)Object.Destroy(row.Box);rowValues.Remove(pair.Key);continue;}
            bool visible=tag.gameObject.activeInHierarchy && !tag.isInitializing && IsMatch(mode) && MatchInfoPresent;
            if(!visible)
            {
                if(row.Box)row.Box.SetActive(false);
                RestoreRow(row);
                continue;
            }
            if(!tag.battlePoints)continue;
            if(!row.Box)CreateRowBox(row);
            long value=mode==SortMode.BattlePoints?(points.TryGetValue(row.Key,out int bp)?bp:-1):matches.Value(mode,row.Master);
            var record=matches.Find(row.Master);
            string text=mode==SortMode.Wins || mode==SortMode.Losses?
                ValueDisplay.Pair(record?.Wins??-1,record?.Losses??-1,(mode==SortMode.Losses)!=inverse):ValueDisplay.Format(mode,value);
            if(row.Text.text!=text)row.Text.text=text;
            row.Text.color=mode==SortMode.Wins || mode==SortMode.Losses?Color.white:tag.battlePoints.color;
            var source=tag.battlePoints;
            CaptureRow(row);RestoreRow(row);
            var scale=source.transform.lossyScale;
            float sx=Mathf.Abs(scale.x);
            if(sx<.0001f)continue;
            var right=source.transform.right;
            Vector3 center=source.transform.TransformPoint(source.rectTransform.rect.center);
            var nameRect=tag.username.rectTransform;
            Vector3 rowRight=nameRect.TransformPoint(new Vector3(nameRect.rect.xMax,nameRect.rect.center.y,0));
            // UwU, share the BP field and spare space while leaving a safe border inset.
            float left=-source.rectTransform.rect.width*sx*.5f+.004f;
            float end=Vector3.Dot(rowRight-center,right)-.016f;
            float available=(end-left)/sx;
            if(available<=.01f){row.Box.SetActive(false);continue;}
            row.Text.enableAutoSizing=false;row.Text.fontSize=row.BPFont;
            // Mew, reserve six widest digits even before BP loads, followed by a bracket-width gap.
            float digit=0;
            for(int d=0;d<10;d++)digit=Mathf.Max(digit,row.Text.GetPreferredValues(d.ToString()).x);
            float bpWidth=Mathf.Max(digit*6,row.Text.GetPreferredValues(source.text).x);
            float gap=Mathf.Max(row.Text.GetPreferredValues("]").x,.003f/sx);
            float valueWidth=row.Text.GetPreferredValues(text).x;
            float factor=Mathf.Min(1f,available/Mathf.Max(.0001f,bpWidth+gap+valueWidth))*.96f;
            // UwU, short values stay near native size; long pairs shrink together with BP.
            source.enableAutoSizing=false;source.fontSize=row.BPFont*factor;
            source.alignment=TextAlignmentOptions.MidlineLeft;
            source.rectTransform.sizeDelta=new Vector2(bpWidth*factor,row.BPSize.y);
            Vector3 target=center+right*(left+bpWidth*factor*sx*.5f);
            source.transform.position+=target-source.transform.TransformPoint(source.rectTransform.rect.center);
            row.Text.fontSize=row.BPFont*factor;row.Text.alignment=TextAlignmentOptions.MidlineLeft;
            float start=left+(bpWidth+gap)*factor*sx;
            float width=end-start;
            row.Box.transform.position=center+right*(start+width*.5f)-source.transform.forward*.001f;
            row.Box.transform.rotation=source.transform.rotation;
            row.Text.rectTransform.sizeDelta=new Vector2(width/sx,row.BPSize.y);
            row.Text.margin=Vector4.zero;
            // Mew, stretch the original tan plate inside the border; keep its material and outline.
            if(row.Plate)
            {
                float nativeWidth=row.PlateBounds.size.x*Mathf.Abs(row.Plate.lossyScale.x);
                if(nativeWidth>.001f)
                {
                    var ps=row.Plate.localScale;
                    row.Plate.localScale=new Vector3(ps.x*(end-left+.008f)/nativeWidth,ps.y,ps.z);
                    float current=Vector3.Dot(row.Plate.TransformPoint(row.PlateBounds.center)-center,right);
                    row.Plate.position+=right*((left+end)*.5f-current);
                }
            }
            row.Applied=true;
            row.Box.SetActive(true);
        }
    }
    // UwU, capture native values once so each layout starts fresh instead of shrinking repeatedly.
    private static void CaptureRow(RowValue row)
    {
        if(row.Captured)return;
        var bp=row.Tag.battlePoints;
        row.BPPosition=bp.transform.localPosition;row.BPSize=bp.rectTransform.sizeDelta;
        row.BPFont=bp.fontSize;row.BPAuto=bp.enableAutoSizing;row.BPAlignment=bp.alignment;
        Vector3 center=bp.transform.TransformPoint(bp.rectTransform.rect.center);
        float best=float.MaxValue;
        foreach(var mesh in row.Tag.GetComponentsInChildren<MeshFilter>(true))
        {
            if(!mesh.sharedMesh || mesh.GetComponent<TMP_Text>())continue;
            var t=mesh.transform; if(bp.transform.IsChildOf(t))continue; // UwU, keep text parents untouched.
            if(Vector3.Dot(t.right,bp.transform.right)<.99f)continue;
            var bounds=mesh.sharedMesh.bounds;
            float width=bounds.size.x*Mathf.Abs(t.lossyScale.x);
            var local=t.InverseTransformPoint(center);
            // Mew, reject full-row and rank meshes; only use a small plate containing the BP center.
            if(width<.08f || width>.30f || local.x<bounds.min.x || local.x>bounds.max.x ||
                local.y<bounds.min.y || local.y>bounds.max.y)continue;
            float distance=(t.TransformPoint(bounds.center)-center).sqrMagnitude;
            if(distance>=best)continue;
            best=distance;row.Plate=t;row.PlateBounds=bounds;
        }
        if(row.Plate){row.PlatePosition=row.Plate.localPosition;row.PlateScale=row.Plate.localScale;}
        row.Captured=true;
    }
    // UwU, switching sorts and unloading the mod put native BP and its plate back exactly.
    private static void RestoreRow(RowValue row)
    {
        if(!row.Applied || !row.Tag || !row.Tag.battlePoints)return;
        var bp=row.Tag.battlePoints;
        if(row.Plate){row.Plate.localPosition=row.PlatePosition;row.Plate.localScale=row.PlateScale;}
        bp.transform.localPosition=row.BPPosition;bp.rectTransform.sizeDelta=row.BPSize;
        bp.fontSize=row.BPFont;bp.enableAutoSizing=row.BPAuto;bp.alignment=row.BPAlignment;
        row.Applied=false;
    }
    // Mew, this is only a transform and native-font text now. The original board supplies the background, so no duplicate plate protrudes.
    private static void CreateRowBox(RowValue row)
    {
        var source=row.Tag.battlePoints;
        var box=new GameObject("FriendBPSort.SortedValue");box.SetActive(false);
        try
        {
            box.name="FriendBPSort.SortedValue";box.transform.SetParent(row.Tag.transform,false);
            box.transform.rotation=source.transform.rotation;box.transform.localScale=Vector3.one;
            var scale=box.transform.lossyScale;
            if(Mathf.Min(Mathf.Abs(scale.x),Mathf.Min(Mathf.Abs(scale.y),Mathf.Abs(scale.z)))<.0001f)
                throw new InvalidOperationException("🐱 Cannot place value box on a zero-scale row.");
            // Use the existing row background itself: identical color, material and depth,
            // with no cloned mesh whose off-center bounds can protrude beyond the board.
            var label=Object.Instantiate(source.gameObject,box.transform).GetComponent<TMP_Text>();
            label.name="SortedValueText";label.transform.localPosition=Vector3.zero;
            label.enabled=true;
            label.transform.localRotation=Quaternion.identity;
            var parentScale=box.transform.lossyScale;var textScale=source.transform.lossyScale;
            label.transform.localScale=new Vector3(textScale.x/parentScale.x,textScale.y/parentScale.y,textScale.z/parentScale.z);
            label.rectTransform.pivot=new Vector2(.5f,.5f);
            label.rectTransform.sizeDelta=new Vector2(.14f/Mathf.Abs(textScale.x),.065f/Mathf.Abs(textScale.y));
            label.alignment=TextAlignmentOptions.Center;label.raycastTarget=false;label.richText=true;
            label.enableWordWrapping=false;label.enableAutoSizing=true;
            label.overflowMode=TextOverflowModes.Masking;
            label.fontSizeMax=source.fontSize;label.fontSizeMin=source.fontSize*.3f;
            label.color=source.color;label.gameObject.SetActive(true);
            row.Box=box;row.Text=label;
        }
        catch{box.SetActive(false);Object.Destroy(box);throw;}
    }
    // Mew, clear callbacks cloned from the stone switch so these buttons cannot accidentally change your stones.
    private static void ClearEvents(InteractionButton button)
    {
        button.onPressed=new UnityEvent();button.onPressedByPlayer=new UnityEvent<Player>();
        button.onToggledByPlayerController=new UnityEvent<bool,PlayerController>();
        button.onToggleStateChanged=new UnityEventBool();button.isToggleButton=false;button.useLongPress=false;button.disableButtonAfterInit=false;button.pressInterval=.5f;
    }
    // UwU, borrow native stone-button contact/haptic behavior, fit its mesh, then place each control in the lever column.
    private static SideButton MakeButton(PlayerTag tag,FriendList list,SortMode choice)
    {
        if(!tag.moreInfoButton || !tag.username)return null;
        if(!stoneButton && Time.unscaledTime>=nextAssetSearch)
        {
            nextAssetSearch=Time.unscaledTime+5;
            foreach(var candidate in Object.FindObjectsOfType<InteractionButton>(true))
            {
                for(var ancestor=candidate.transform.parent;ancestor;ancestor=ancestor.parent)
                    if(ancestor.name.IndexOf("ShiftstoneQuickswapper",StringComparison.OrdinalIgnoreCase)>=0)
                    {stoneButton=candidate;break;}
                if(stoneButton)break;
            }
            if(!stoneButton && !warnedMissingAsset)
            {warnedMissingAsset=true;MelonLogger.Warning("🐱 Shiftstone quickswap button not found; favorites will appear when the asset is available.");}
            if(stoneButton)warnedMissingAsset=false;
        }
        if(!stoneButton)return null;
        if(!staging){staging=new GameObject("FriendBPSort.Staging");staging.SetActive(false);}
        var go=Object.Instantiate(stoneButton.gameObject,staging.transform);go.name="FriendBPSort.Sort."+choice;
        try
        {
        go.SetActive(false);go.transform.SetParent(list.transform,false);
        go.transform.rotation=tag.moreInfoButton.transform.rotation;
        var boardScale=list.transform.lossyScale;
        var assetScale=stoneButton.transform.lossyScale;
        if(Mathf.Abs(boardScale.x)<.0001f || Mathf.Abs(boardScale.y)<.0001f || Mathf.Abs(boardScale.z)<.0001f)
            throw new InvalidOperationException("🐱 Cannot place favorite button on a board with zero scale.");
        go.transform.localScale=new Vector3(assetScale.x/boardScale.x,assetScale.y/boardScale.y,assetScale.z/boardScale.z);
        // Fit the actual stone-switch button geometry to a compact 9 cm target.
        var meshes=go.GetComponentsInChildren<Renderer>(true);
        if(meshes.Length>0)
        {
            var bounds=meshes[0].bounds;
            foreach(var mesh in meshes)bounds.Encapsulate(mesh.bounds);
            float extent=Mathf.Max(bounds.size.x,Mathf.Max(bounds.size.y,bounds.size.z));
            if(extent>.001f)go.transform.localScale*=.09f/extent;
        }
        // Requested world-space anchor beside the lever, stacked vertically downward.
        Vector3 buttonCenter=new Vector3(8.48f,1.76f-.12f*(int)choice,13.65f);
        go.transform.position=buttonCenter;
        // UwU, the borrowed stone button has an offset pivot. Center its visible
        // geometry on the requested column so every button lines up evenly.
        if(meshes.Length>0)
        {
            var centeredBounds=meshes[0].bounds;
            foreach(var mesh in meshes)centeredBounds.Encapsulate(mesh.bounds);
            go.transform.position+=buttonCenter-centeredBounds.center;
        }
        var readerRight=tag.username.transform.right;
        var native=go.GetComponent<InteractionButton>();ClearEvents(native);
        // Preserve the native hand filters, press rails, haptics and sound.
        // Reset contact state copied from a potentially pressed source button.
        native.isPressed=false;native.waitingForRelease=false;native.canBeToggledAgain=true;
        native.closestCollider=null;native.lastTimePressed=-100;
        foreach(var collider in go.GetComponentsInChildren<Collider>(true))collider.enabled=true;
        foreach(var body in go.GetComponentsInChildren<Rigidbody>(true))
        {body.isKinematic=true;body.useGravity=false;body.detectCollisions=true;}
        foreach(var text in go.GetComponentsInChildren<TMP_Text>(true))text.gameObject.SetActive(false);
        var label=Object.Instantiate(tag.username.gameObject,go.transform).GetComponent<TMP_Text>();
        label.name="SortLabel";label.text=captions[(int)choice];label.alignment=TextAlignmentOptions.Center;label.raycastTarget=false;
        label.gameObject.SetActive(true);
        // Place the description to the player's left, on the exposed button plane.
        label.transform.position=go.transform.position-tag.username.transform.right*.22f-tag.username.transform.up*.025f-tag.username.transform.forward*.055f;
        label.transform.rotation=tag.username.transform.rotation;label.fontSize=tag.username.fontSize*.85f;
        var parentScale=go.transform.lossyScale;var textScale=tag.username.transform.lossyScale;
        if(Mathf.Abs(parentScale.x)<.0001f || Mathf.Abs(parentScale.y)<.0001f || Mathf.Abs(parentScale.z)<.0001f)
            throw new InvalidOperationException("🐱 Favorite button asset has zero scale.");
        label.transform.localScale=new Vector3(textScale.x/parentScale.x,textScale.y/parentScale.y,textScale.z/parentScale.z);
        label.rectTransform.sizeDelta=new Vector2(.32f/Mathf.Max(Mathf.Abs(textScale.x),.0001f),.065f/Mathf.Max(Mathf.Abs(textScale.y),.0001f));
        label.enableAutoSizing=false;
        label.enableWordWrapping=false;
        // Seven representative letter advances measured using the label's own font.
        float sevenLetters=label.GetPreferredValues("ABCDEFG").x*Mathf.Abs(label.transform.lossyScale.x);
        label.transform.position-=readerRight*sevenLetters;
        native.onPressed.AddListener((UnityAction)(Action)(()=>Choose(choice)));native.enabled=true;go.SetActive(true);
        return new SideButton {Object=go,Label=label,Mode=choice};
        }
        catch
        {
            // 🐱 A failed setup must not leave another clone behind on the next retry.
            go.SetActive(false);Object.Destroy(go);throw;
        }
    }
    // Mew, restore original labels, dispose the watcher, remove patches and destroy only objects this mod created.
    public override void OnDeinitializeMelon()
    {
        stopped=true;if(historyDirty)Save();HarmonyInstance.UnpatchSelf();
        matchWatcher?.Dispose();matchWatcher=null;
        foreach(var row in rowValues.Values)
        {RestoreRow(row);if(row.Box)Object.Destroy(row.Box);}
        rowValues.Clear();
        foreach(var board in controls.Values)foreach(var button in board)if(button.Object)Object.Destroy(button.Object);
        controls.Clear();if(staging)Object.Destroy(staging);

        foreach(var list in lists.Values.ToArray())if(list && list.gameObject.activeInHierarchy)list.Refresh();
    }
}













