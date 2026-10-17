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
[assembly:MelonInfo(typeof(FriendBPSort.Mod),"FriendBPSort","0.3.3","Colli",null)]
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
    }
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
    private static Material backboardMaterial;
    private static InteractionButton stoneButton;
    private static bool rendering;
    private static string Key(UserSearchResult f)=>!string.IsNullOrWhiteSpace(f?.PlayFabMasterId)?f.PlayFabMasterId:f?.PlayFabTitleId??"";
    private static string Key(UserData u)=>!string.IsNullOrWhiteSpace(u?.playFabMasterId)?u.playFabMasterId:u?.playFabTitleId??"";
    private static int BP(UserSearchResult f)=>points.TryGetValue(Key(f),out int bp)?bp:-1;
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
    private static int CompareFriends(GetFriendsResult a,GetFriendsResult b)
    {
        return Ranking.Compare(mode,BP(a),a?.PublicName,Key(a),IsMatch(mode)?matches.Value(mode,a?.PlayFabMasterId):history.Date(mode,a?.PlayFabMasterId,a?.PlayFabTitleId),BP(b),b?.PublicName,Key(b),IsMatch(mode)?matches.Value(mode,b?.PlayFabMasterId):history.Date(mode,b?.PlayFabMasterId,b?.PlayFabTitleId),inverse);
    }
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
    private static void MetPlayer(string __0)
    {
        if(string.IsNullOrWhiteSpace(__0))return;
        history.RecordPlayed(__0,DateTime.UtcNow.Ticks);historyDirty=true;
        if(mode==SortMode.RecentlyPlayed)dirty=true;
    }
    private static void EnsureAvailableSort()
    {
        if(IsMatch(mode) && !MatchInfoPresent)
        {mode=SortMode.Alphabetical;inverse=false;savedMode.Value=(int)mode;savedInverse.Value=false;dirty=true;historyDirty=true;resetPage=true;}
    }
    private static void Choose(SortMode chosen)
    {
        if(IsMatch(chosen) && !MatchInfoPresent)return;
        if(chosen==SortMode.Wins && (mode==SortMode.Wins || mode==SortMode.Losses))
        {if(!inverse)inverse=true;else{inverse=false;mode=mode==SortMode.Wins?SortMode.Losses:SortMode.Wins;}}
        else if(mode==chosen)inverse=!inverse;
        else {mode=chosen;inverse=false;}
        savedMode.Value=(int)mode;savedInverse.Value=inverse;dirty=true;historyDirty=true;resetPage=true;
    }
    private static void Save()
    {
        try{savedHistory.Value=JsonSerializer.Serialize(history);preferences.SaveToFile(false);historyDirty=false;}
        catch(Exception ex){MelonLogger.Warning("🐱 Could not save sorting preferences: "+ex.Message);}
    }
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
    private static void BindRow(PlayerTag tag,GetFriendsResult friend)
    {
        int id=tag.GetInstanceID();
        if(!rowValues.TryGetValue(id,out var row))rowValues[id]=row=new RowValue{Tag=tag};
        row.Master=friend.PlayFabMasterId;row.Title=friend.PlayFabTitleId;row.Key=Key(friend);
        // Hide recycled row values until the native profile initialization finishes.
        if(row.Box)row.Box.SetActive(false);
    }
    private static void UpdateRowValues()
    {
        foreach(var pair in rowValues.ToArray())
        {
            var row=pair.Value;var tag=row.Tag;
            if(!tag){if(row.Box)Object.Destroy(row.Box);rowValues.Remove(pair.Key);continue;}
            bool visible=tag.gameObject.activeInHierarchy && !tag.isInitializing &&
                (mode==SortMode.BattlePoints || IsMatch(mode) && MatchInfoPresent);
            if(!visible){if(row.Box)row.Box.SetActive(false);continue;}
            if(!tag.battlePoints)continue;
            if(!row.Box)CreateRowBox(row);
            long value=mode==SortMode.BattlePoints?(points.TryGetValue(row.Key,out int bp)?bp:-1):matches.Value(mode,row.Master);
            var record=matches.Find(row.Master);
            string text=mode==SortMode.Wins || mode==SortMode.Losses?
                ValueDisplay.Pair(record?.Wins??-1,record?.Losses??-1,(mode==SortMode.Losses)!=inverse):ValueDisplay.Format(mode,value);
            if(row.Text.text!=text)row.Text.text=text;
            row.Text.color=mode==SortMode.Wins || mode==SortMode.Losses?Color.white:tag.battlePoints.color;
            // Follow the actual BP label's right edge; camera position is a viewing reference.
            var source=tag.battlePoints;var scale=source.transform.lossyScale;
            var right=source.transform.right;
            float halfWidth=source.rectTransform.rect.width*Mathf.Abs(scale.x)*.5f;
            Vector3 center=source.transform.TransformPoint(source.rectTransform.rect.center);
            row.Box.transform.position=center+right*(halfWidth+.085f)-source.transform.forward*.008f;
            row.Box.transform.rotation=source.transform.rotation;
            row.Box.SetActive(true);
        }
    }
    private static void CreateRowBox(RowValue row)
    {
        var source=row.Tag.battlePoints;
        var box=GameObject.CreatePrimitive(PrimitiveType.Cube);box.SetActive(false);
        try
        {
            box.name="FriendBPSort.SortedValue";box.transform.SetParent(row.Tag.transform,false);
            box.transform.rotation=source.transform.rotation;box.transform.localScale=Vector3.one;
            var scale=box.transform.lossyScale;
            if(Mathf.Min(Mathf.Abs(scale.x),Mathf.Min(Mathf.Abs(scale.y),Mathf.Abs(scale.z)))<.0001f)
                throw new InvalidOperationException("🐱 Cannot place value box on a zero-scale row.");
            box.transform.localScale=new Vector3(.15f/Mathf.Abs(scale.x),.075f/Mathf.Abs(scale.y),.008f/Mathf.Abs(scale.z));
            var collider=box.GetComponent<Collider>();collider.enabled=false;Object.Destroy(collider);
            // Copy the nearest small native mesh behind the BP label, excluding text and our own boxes.
            var center=source.transform.TransformPoint(source.rectTransform.rect.center);
            var plate=row.Tag.GetComponentsInChildren<MeshFilter>(true)
                .Where(m=>m.sharedMesh && m.GetComponent<MeshRenderer>() && !m.GetComponent<TMP_Text>() &&
                    !m.name.StartsWith("FriendBPSort",StringComparison.Ordinal) &&
                    m.GetComponent<Renderer>().bounds.size.magnitude<.65f)
                .OrderBy(m=>(m.GetComponent<Renderer>().bounds.center-center).sqrMagnitude).FirstOrDefault();
            if(plate)
            {
                box.GetComponent<MeshFilter>().sharedMesh=plate.sharedMesh;
                box.GetComponent<Renderer>().sharedMaterials=plate.GetComponent<Renderer>().sharedMaterials;
                var bounds=plate.sharedMesh.bounds;var parent=row.Tag.transform.lossyScale;
                box.transform.localScale=new Vector3(.15f/Mathf.Max(.0001f,bounds.size.x*Mathf.Abs(parent.x)),
                    .075f/Mathf.Max(.0001f,bounds.size.y*Mathf.Abs(parent.y)),.008f/Mathf.Max(.0001f,bounds.size.z*Mathf.Abs(parent.z)));
            }
            else box.GetComponent<Renderer>().sharedMaterial=SolidBrown();
            var label=Object.Instantiate(source.gameObject,box.transform).GetComponent<TMP_Text>();
            label.name="SortedValueText";label.transform.position=box.transform.position-source.transform.forward*.006f;
            label.transform.localRotation=Quaternion.identity;
            var parentScale=box.transform.lossyScale;var textScale=source.transform.lossyScale;
            label.transform.localScale=new Vector3(textScale.x/parentScale.x,textScale.y/parentScale.y,textScale.z/parentScale.z);
            label.rectTransform.pivot=new Vector2(.5f,.5f);
            label.rectTransform.sizeDelta=new Vector2(.14f/Mathf.Abs(textScale.x),.065f/Mathf.Abs(textScale.y));
            label.alignment=TextAlignmentOptions.Center;label.raycastTarget=false;label.richText=true;
            label.enableWordWrapping=false;label.enableAutoSizing=true;
            label.fontSizeMax=source.fontSize;label.fontSizeMin=source.fontSize*.3f;
            label.color=source.color;label.gameObject.SetActive(true);
            row.Box=box;row.Text=label;
        }
        catch{box.SetActive(false);Object.Destroy(box);throw;}
    }
    private static Material SolidBrown()
    {
        if(backboardMaterial)return backboardMaterial;
        var shader=Shader.Find("Universal Render Pipeline/Unlit");
        if(!shader)shader=Shader.Find("Unlit/Color");
        if(!shader)throw new InvalidOperationException("🐱 Solid-color backboard shader unavailable.");
        backboardMaterial=new Material(shader);backboardMaterial.name="FriendBPSort.SolidBrown";
        var brown=new Color(.32f,.16f,.07f,1f);
        if(backboardMaterial.HasProperty("_BaseColor"))backboardMaterial.SetColor("_BaseColor",brown);
        if(backboardMaterial.HasProperty("_Color"))backboardMaterial.SetColor("_Color",brown);
        return backboardMaterial;
    }
    private static void ClearEvents(InteractionButton button)
    {
        button.onPressed=new UnityEvent();button.onPressedByPlayer=new UnityEvent<Player>();
        button.onToggledByPlayerController=new UnityEvent<bool,PlayerController>();
        button.onToggleStateChanged=new UnityEventBool();button.isToggleButton=false;button.useLongPress=false;button.disableButtonAfterInit=false;button.pressInterval=.5f;
    }
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
        var sourcePosition=tag.transform.InverseTransformPoint(tag.moreInfoButton.transform.position);
        // Mirror the row's existing action button across the text center to its left.
        float textX=tag.transform.InverseTransformPoint(tag.username.transform.position).x;
        sourcePosition.x=textX-Mathf.Max(Mathf.Abs(sourcePosition.x-textX),.06f);go.transform.position=tag.transform.TransformPoint(sourcePosition)-tag.username.transform.up*(.12f*(int)choice);
        // Text-right matches the reader's right when facing the board. Place the
        // column to the reader's LEFT of every scroll control with 16 cm clearance.
        var readerRight=tag.username.transform.right;
        float leftEdge=Vector3.Dot(go.transform.position,readerRight)-.18f;
        foreach(var scroll in new[]{list.pageUpButton,list.pageDownButton,list.scrollUpButton,list.scrollDownButton})
            if(scroll)leftEdge=Mathf.Min(leftEdge,Vector3.Dot(scroll.transform.position,readerRight)-.16f);
        go.transform.position+=readerRight*(leftEdge-Vector3.Dot(go.transform.position,readerRight));
        // Move the entire control another two nominal 9 cm button widths left.
        go.transform.position-=readerRight*.18f;
        // Text's front faces -forward. Stand clear of the board instead of embedding in it.
        go.transform.position-=tag.username.transform.forward*.12f;
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
        // A small stone backboard behind each button. It is decorative so it
        // cannot obstruct the native hand-contact trigger or scroll controls.
        var backboard=GameObject.CreatePrimitive(PrimitiveType.Cube);
        backboard.SetActive(false);backboard.name="SortButtonBackboard";
        backboard.transform.SetParent(go.transform,false);
        backboard.transform.rotation=tag.username.transform.rotation;
        // One inch (0.0254 m) toward the reader from the previous backing plane.
        backboard.transform.position=go.transform.position+tag.username.transform.forward*(.065f-.0254f);
        backboard.transform.localScale=Vector3.one;
        var backingScale=backboard.transform.lossyScale;
        backboard.transform.localScale=new Vector3(.13f/Mathf.Abs(backingScale.x),.11f/Mathf.Abs(backingScale.y),.015f/Mathf.Abs(backingScale.z));
        var backingCollider=backboard.GetComponent<Collider>();
        if(backingCollider){backingCollider.enabled=false;Object.Destroy(backingCollider);}
        backboard.GetComponent<Renderer>().sharedMaterial=SolidBrown();
        backboard.SetActive(true);
        native.onPressed.AddListener((UnityAction)(Action)(()=>Choose(choice)));native.enabled=true;go.SetActive(true);
        return new SideButton {Object=go,Label=label,Mode=choice};
        }
        catch
        {
            // 🐱 A failed setup must not leave another clone behind on the next retry.
            go.SetActive(false);Object.Destroy(go);throw;
        }
    }
    public override void OnDeinitializeMelon()
    {
        stopped=true;if(historyDirty)Save();HarmonyInstance.UnpatchSelf();
        matchWatcher?.Dispose();matchWatcher=null;
        foreach(var row in rowValues.Values)if(row.Box)Object.Destroy(row.Box);
        rowValues.Clear();
        foreach(var board in controls.Values)foreach(var button in board)if(button.Object)Object.Destroy(button.Object);
        controls.Clear();if(staging)Object.Destroy(staging);
        if(backboardMaterial)Object.Destroy(backboardMaterial);
        foreach(var list in lists.Values.ToArray())if(list && list.gameObject.activeInHierarchy)list.Refresh();
    }
}








