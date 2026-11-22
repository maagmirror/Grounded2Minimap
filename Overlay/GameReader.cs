using System.Text;
using System.Text.RegularExpressions;

public sealed class Marker
{
    public double X, Y;
    public string Name;
}

// What the game's own map shows, grouped so each group can be turned on/off.
public enum MarkKind { Pin, Bed, Ping, Quest, Backpack, Pet, Collectible, Base, Other }

public sealed class MapMark
{
    public double X, Y, Z;
    public MarkKind Kind;
    public string Type;     // EHUDMarkerType name, e.g. "MarkerBuilding"
    public string Name;     // player-written name for pins, empty otherwise
    public Color Color;     // Color.Empty when the game has no custom color
    public string Icon;     // game texture path for this marker (null = draw a fallback shape)
}

public sealed class Creature
{
    public double X, Y;
    public string Name;     // readable name from the class, e.g. "Spider Diving Bell"
}

public sealed class Poi
{
    public double X, Y;
    public string Name;
    public string Icon;
}

// Current level as the game describes it (SurvivalGameInstance.LevelData).
public sealed class LevelInfo
{
    public string Name;                 // world name, e.g. "Augusta_Main"
    public double MinX, MaxX, MinY, MaxY;
    public int TexW, TexH;
    public string MapWidget;            // e.g. "/Game/UI/Map/UI_MapBGBackyard"
}

public sealed class Snapshot
{
    public bool Valid;
    public double X, Y, Z, Yaw;
    public List<Marker> Friends = new();
    public List<MapMark> Marks = new();
    public List<Creature> Creatures = new();
    public List<Poi> Pois = new();
    public LevelInfo Level;
}

// Background thread: attaches to the game (read-only), resolves UE reflection offsets by name,
// then polls the local player's position ~30 times per second. Everything beyond the player's
// position is optional: if a game update renames something, only that feature turns off.
public sealed class GameReader
{
    const string ProcName = "Grounded2Steam-Win64-Shipping";

    public volatile Snapshot Snap = new();
    public volatile string Status = L.T("Looking for Grounded 2...", "Buscando Grounded 2...");
    public int GamePid => m?.Pid ?? 0;
    public string GameExePath { get; private set; }
    public Action<string> Log = _ => { };
    // Every texture the game's map uses for markers/places (filled once per attach).
    public volatile string[] IconPaths = Array.Empty<string>();
    public volatile string PlayerIcon;

    Mem m;
    UE ue;
    ulong engine;
    int oGameInstance, oLocalPlayers, oPC, oPawn, oCtrlRot, oRoot, oRelLoc, oAttachParent;
    int oCamMgr, oCamCache, oPovLoc;
    int oViewport, oWorld, oGameState, oPlayerArray, oPawnPrivate, oPlayerName;
    bool stop;

    // Optional features (-1 / null = unavailable)
    Optional opt;
    sealed class Optional
    {
        public int oLevelData = -1, ldStride, ldRel = -1, ldDev = -1, ldMin = -1, ldMax = -1, ldTex = -1, ldWidget = -1;
        public int oMarkerMgr = -1;
        public List<int> markerArrays = new();
        public int mkLoc = -1, mkType = -1, mkName = -1, mkColor = -1, mkColorVar = -1, mkHidden = -1, mkRef = -1, mkVariant = -1;
        public string[] markerTypes = Array.Empty<string>();
        public uint[] markerColors = Array.Empty<uint>();
        public ulong creatureCls, poiCompCls;
        public int oHealth = -1, oHealthState = -1, deadState = -1;
        public int oPoiData = -1, oPlayerState = -1, oDiscovered = -1, oPoiTag = -1;
        // icons, all read from GlobalUIData
        public Dictionary<string, string[]> markerIcons = new();     // marker type -> texture per variant
        public Dictionary<string, string> legendIcons = new();       // EMapEntryType -> texture
        public Dictionary<string, string> poiTypeByTag = new();      // POI category tag -> EMapEntryType
    }

    // Filled by the world scanner thread.
    volatile ulong[] creatureActors = Array.Empty<ulong>();
    volatile (ulong comp, ulong data, ulong actor)[] poiComps = Array.Empty<(ulong, ulong, ulong)>();
    readonly Dictionary<ulong, string> creatureNames = new();
    readonly Dictionary<ulong, string> poiNames = new();

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "GameReader", Priority = ThreadPriority.BelowNormal };
        t.Start();
        var s = new Thread(ScanLoop) { IsBackground = true, Name = "WorldScanner", Priority = ThreadPriority.Lowest };
        s.Start();
    }
    public void Stop() => stop = true;

    void Loop()
    {
        int tick = 0;
        var friends = new List<Marker>();
        var marks = new List<MapMark>();
        var creatures = new List<Creature>();
        var pois = new List<Poi>();
        HashSet<ulong> pets = new();
        LevelInfo level = null;
        while (!stop)
        {
            try
            {
                if (m == null || !m.Alive)
                {
                    Snap = new Snapshot();
                    m?.Dispose(); m = null; ue = null; engine = 0; opt = null; level = null;
                    creatureActors = Array.Empty<ulong>(); poiComps = Array.Empty<(ulong, ulong, ulong)>();
                    Status = L.T("Looking for Grounded 2...", "Buscando Grounded 2...");
                    if (!TryAttach()) { Thread.Sleep(2000); continue; }
                }
                if (engine == 0 && !Resolve()) { Thread.Sleep(3000); continue; }

                var s = ReadLocal(out ulong pc);
                if (s.Valid)
                {
                    if (tick % 60 == 0) level = Safe(() => ReadLevel(), level, "level data");
                    if (tick % 30 == 0) friends = ReadFriends(s);
                    if (tick % 15 == 0) marks = Safe(() => ReadMarks(pets = new HashSet<ulong>()), marks, "markers");
                    if (tick % 3 == 0) creatures = Safe(() => ReadCreatures(pets), creatures, "creatures");
                    if (tick % 60 == 30) pois = Safe(() => ReadPois(pc), pois, "places");
                    s.Friends = friends; s.Marks = marks; s.Creatures = creatures; s.Pois = pois; s.Level = level;
                    Status = "OK";
                }
                else Status = L.T("In the menu / loading...", "En el menú / cargando...");
                Snap = s;
                tick++;
            }
            catch (Exception ex)
            {
                Status = "Error: " + ex.Message;
                engine = 0;
                Thread.Sleep(2000);
            }
            Thread.Sleep(33);
        }
        m?.Dispose();
    }

    readonly HashSet<string> warned = new();
    T Safe<T>(Func<T> f, T fallback, string what)
    {
        if (opt == null) return fallback;
        try { return f(); }
        catch (Exception ex)
        {
            if (warned.Add(what)) Log($"Could not read {what}: {ex.Message}");
            return fallback;
        }
    }

    bool TryAttach()
    {
        m = Mem.Attach(ProcName);
        if (m == null) return false;
        Status = L.T("Reading game data...", "Leyendo datos del juego...");
        try { GameExePath = System.Diagnostics.Process.GetProcessById(m.Pid).MainModule?.FileName; } catch { }
        ue = new UE(m);
        if (!ue.Init(_ => { })) { Status = L.T("Could not read the game (incompatible version?)", "No se pudo leer el juego (¿versión incompatible?)"); m.Dispose(); m = null; return false; }
        return true;
    }

    int Off(string cls, string prop)
    {
        ulong c = ue.FindObject((cls.EndsWith("CameraCacheEntry") || cls.EndsWith("MinimalViewInfo") ? "ScriptStruct " : "Class ") + cls);
        int o = c == 0 ? -1 : ue.PropOffset(c, prop);
        if (o < 0) throw new Exception($"offset {cls}.{prop} not found");
        return o;
    }

    bool Resolve()
    {
        oGameInstance = Off("/Script/Engine.GameEngine", "GameInstance");
        oViewport = Off("/Script/Engine.Engine", "GameViewport");
        oLocalPlayers = Off("/Script/Engine.GameInstance", "LocalPlayers");
        oPC = Off("/Script/Engine.Player", "PlayerController");
        oPawn = Off("/Script/Engine.PlayerController", "AcknowledgedPawn");
        oCamMgr = Off("/Script/Engine.PlayerController", "PlayerCameraManager");
        oCamCache = Off("/Script/Engine.PlayerCameraManager", "CameraCachePrivate");
        oPovLoc = Off("/Script/Engine.CameraCacheEntry", "POV") + Off("/Script/Engine.MinimalViewInfo", "Location");
        oCtrlRot = Off("/Script/Engine.Controller", "ControlRotation");
        oRoot = Off("/Script/Engine.Actor", "RootComponent");
        oRelLoc = Off("/Script/Engine.SceneComponent", "RelativeLocation");
        oAttachParent = Off("/Script/Engine.SceneComponent", "AttachParent");
        oWorld = Off("/Script/Engine.GameViewportClient", "World");
        oGameState = Off("/Script/Engine.World", "GameState");
        oPlayerArray = Off("/Script/Engine.GameStateBase", "PlayerArray");
        oPawnPrivate = Off("/Script/Engine.PlayerState", "PawnPrivate");
        oPlayerName = Off("/Script/Engine.PlayerState", "PlayerNamePrivate");

        ulong engCls = ue.FindObject("Class /Script/Engine.GameEngine");
        int n = ue.NumObjects;
        for (int i = 0; i < n && engine == 0; i++)
        {
            ulong o = ue.GetObject(i);
            if (o != 0 && ue.IsA(o, engCls) && !ue.ObjName(o).StartsWith("Default__")) engine = o;
        }
        if (engine == 0) { Status = L.T("GameEngine not found", "GameEngine no encontrado"); return false; }
        opt = ResolveOptional();
        return true;
    }

    Optional ResolveOptional()
    {
        var o = new Optional();
        void Try(string what, Action a) { try { a(); } catch (Exception ex) { Log($"Disabled {what}: {ex.Message}"); } }
        int P(ulong cls, string name) { int r = cls == 0 ? -1 : ue.PropOffset(cls, name); if (r < 0) throw new Exception(name + " not found"); return r; }
        // Find a property by a predicate on its name (for names the game may tweak).
        int PLike(ulong cls, Func<string, bool> pred)
        {
            for (ulong c = cls; c != 0; c = m.P(c + (ulong)ue.S_Super))
                for (ulong f = m.P(c + (ulong)ue.S_ChildProps); f != 0; f = m.P(f + (ulong)ue.F_Next))
                    if (ue.Name(m.R<uint>(f + (ulong)ue.F_Name)) is string s && pred(s)) return m.R<int>(f + (ulong)ue.P_Offset);
            throw new Exception("property not found");
        }

        Try("level data", () =>
        {
            ulong gi = m.P(engine + (ulong)oGameInstance);
            o.oLevelData = P(ue.ObjClass(gi), "LevelData");
            ulong ld = ue.FindObject("ScriptStruct /Script/Maine.LevelData");
            o.ldStride = ((8 + ue.StructSize(ld) + 7) & ~7) + 8;   // TSetElement<TPair<FGameplayTag, FLevelData>>
            o.ldRel = P(ld, "RelLevel"); o.ldDev = P(ld, "DevLevel");
            o.ldMin = P(ld, "MinBounds"); o.ldMax = P(ld, "MaxBounds"); o.ldTex = P(ld, "MapTextureSize");
            o.ldWidget = PLike(ld, s => s.StartsWith("MapBa") && s.EndsWith("Widget"));
        });
        Try("markers", () =>
        {
            ulong mgrCls = ue.FindObject("Class /Script/Maine.HUDMarkerManagerComponent");
            ulong arrSt = ue.FindObject("ScriptStruct /Script/Maine.HUDMarkerArray");
            int inner = P(arrSt, "MarkerArray");
            for (ulong f = m.P(mgrCls + (ulong)ue.S_ChildProps); f != 0; f = m.P(f + (ulong)ue.F_Next))
                if (ue.Name(m.R<uint>(f + (ulong)ue.F_Name)) is string s && s.StartsWith("Registered") && s.EndsWith("MarkerData"))
                    o.markerArrays.Add(m.R<int>(f + (ulong)ue.P_Offset) + inner);
            ulong gsCls = ue.FindObject("Class /Script/Maine.SurvivalGameState");
            o.oMarkerMgr = P(gsCls, "HUDMarkerManagerComponent");
            ulong md = ue.FindObject("Class /Script/Maine.HUDMarkerData");
            o.mkLoc = P(md, "Location"); o.mkType = P(md, "MarkerType"); o.mkName = P(md, "DisplayName");
            o.mkColor = P(md, "Color"); o.mkColorVar = P(md, "MarkerTypeColorVariant");
            o.mkHidden = P(md, "HiddenByPlayers"); o.mkRef = P(md, "ReferenceActor"); o.mkVariant = P(md, "MarkerTypeVariant");
            o.markerTypes = ue.EnumNames(ue.FindObject("Enum /Script/Maine.EHUDMarkerType"));
            if (o.markerTypes.Length == 0) throw new Exception("EHUDMarkerType is empty");
        });
        Try("marker colors", () =>
        {
            // GlobalUIData.HUDMarkerColorOptions: the palette the player picks pin colors from.
            ulong uiCls = ue.FindObject("Class /Script/Maine.GlobalUIData");
            int off = P(uiCls, "HUDMarkerColorOptions");
            int n = ue.NumObjects;
            for (int i = 0; i < n; i++)
            {
                ulong ob = ue.GetObject(i);
                if (ob == 0 || ue.ObjClass(ob) != uiCls && !ue.IsA(ob, uiCls) || ue.ObjName(ob).StartsWith("Default__")) continue;
                ulong arr = m.P(ob + (ulong)off); int num = m.R<int>(ob + (ulong)off + 8);
                if (!Mem.ValidPtr(arr) || num <= 0 || num > 64) continue;
                var c = new uint[num];
                for (int k = 0; k < num; k++) c[k] = m.R<uint>(arr + (ulong)k * 4);   // FColor (B,G,R,A) = 0xAARRGGBB
                o.markerColors = c;
            }
        });
        Try("map icons", () => ResolveIcons(o, P));
        Try("creatures", () =>
        {
            o.creatureCls = ue.FindObject("Class /Script/Maine.SurvivalCreature");
            o.oHealth = P(o.creatureCls, "HealthComponent");
            o.oHealthState = P(ue.FindObject("Class /Script/Maine.HealthComponent"), "HealthState");
            o.deadState = Array.IndexOf(ue.EnumNames(ue.FindObject("Enum /Script/Maine.EHealthState")), "Dead");
        });
        Try("places", () =>
        {
            o.poiCompCls = ue.FindObject("Class /Script/Maine.PointOfInterestComponent");
            o.oPoiData = P(o.poiCompCls, "PointOfInterestData");
            o.oPlayerState = P(ue.FindObject("Class /Script/Engine.Controller"), "PlayerState");
            o.oDiscovered = P(ue.FindObject("Class /Script/Maine.SurvivalPlayerState"), "DiscoveredPointsOfInterest");
            try { o.oPoiTag = P(ue.FindObject("Class /Script/Maine.PointOfInterestDataAsset"), "CategoryTag"); } catch { }
        });
        var all = o.markerIcons.Values.SelectMany(v => v).Concat(o.legendIcons.Values).Where(v => v != null).Distinct().ToArray();
        IconPaths = all;
        PlayerIcon = o.legendIcons.GetValueOrDefault("Player");
        if (all.Length > 0) Log($"Game map icons: {all.Length}");
        return o;
    }

    // "Texture2D /Game/UI/Images/X.X" -> "/Game/UI/Images/X.X" (only textures; materials can't be extracted)
    string TexturePath(ulong obj)
    {
        if (!Mem.ValidPtr(obj) || ue.ClassName(obj) != "Texture2D") return null;
        var f = ue.FullName(obj);
        int sp = f.IndexOf(' ');
        return sp > 0 ? f[(sp + 1)..] : null;
    }

    // Element stride of a TMap<K,V>: TSetElement<TPair<K,V>> + hash index/next (8 bytes).
    static int MapStride(int keySize, int valSize, out int valOff)
    {
        int align = valSize >= 8 ? 8 : valSize >= 4 ? 4 : 1;
        valOff = (keySize + align - 1) / align * align;
        return ((valOff + valSize + 7) & ~7) + 8;
    }

    void ResolveIcons(Optional o, Func<ulong, string, int> P)
    {
        ulong uiCls = ue.FindObject("Class /Script/Maine.GlobalUIData");
        ulong ui = 0;
        int n = ue.NumObjects;
        for (int i = 0; i < n; i++)
        {
            ulong ob = ue.GetObject(i);
            if (ob == 0 || !ue.IsA(ob, uiCls)) continue;
            string name = ue.ObjName(ob);
            if (name.StartsWith("Default__")) continue;
            // Prefer the level-specific data asset (e.g. GlobalUIData_Augusta) over the base one.
            if (ui == 0 || name != "GlobalUIData") ui = ob;
        }
        if (ui == 0) throw new Exception("GlobalUIData not found");

        ulong brushSt = ue.FindObject("ScriptStruct /Script/SlateCore.SlateBrush");
        int brushSize = ue.StructSize(brushSt), oRes = P(brushSt, "ResourceObject");
        ulong varSt = ue.FindObject("ScriptStruct /Script/Maine.HUDMarkerVariationData");
        int oMapList = P(varSt, "MapBrushList"), oHudList = P(varSt, "BrushList");
        string[] mapEntry = ue.EnumNames(ue.FindObject("Enum /Script/Maine.EMapEntryType"));

        // HUDMarkerBrushes: TMap<EHUDMarkerType, FHUDMarkerVariationData>
        int off = P(uiCls, "HUDMarkerBrushes");
        int stride = MapStride(1, ue.StructSize(varSt), out int vOff);
        ulong data = m.P(ui + (ulong)off); int max = m.R<int>(ui + (ulong)off + 12);
        for (int i = 0; Mem.ValidPtr(data) && i < max && i < 1024; i++)
        {
            ulong e = data + (ulong)(i * stride);
            int key = m.R<byte>(e);
            if (key >= o.markerTypes.Length) continue;
            ulong v = e + (ulong)vOff;
            string[] Read(int listOff)
            {
                ulong arr = m.P(v + (ulong)listOff); int cnt = m.R<int>(v + (ulong)listOff + 8);
                if (!Mem.ValidPtr(arr) || cnt <= 0 || cnt > 256) return Array.Empty<string>();
                var r = new string[cnt];
                for (int k = 0; k < cnt; k++) r[k] = TexturePath(m.P(arr + (ulong)(k * brushSize + oRes)));
                return r;
            }
            var mapList = Read(oMapList);
            var hudList = Read(oHudList);
            int len = Math.Max(mapList.Length, hudList.Length);
            var merged = new string[len];
            for (int k = 0; k < len; k++) merged[k] = (k < mapList.Length ? mapList[k] : null) ?? (k < hudList.Length ? hudList[k] : null);
            o.markerIcons[o.markerTypes[key]] = merged;
        }

        // LegendIconMap: TMap<EMapEntryType, UTexture2D*>
        off = P(uiCls, "LegendIconMap");
        stride = MapStride(1, 8, out vOff);
        data = m.P(ui + (ulong)off); max = m.R<int>(ui + (ulong)off + 12);
        for (int i = 0; Mem.ValidPtr(data) && i < max && i < 1024; i++)
        {
            ulong e = data + (ulong)(i * stride);
            int key = m.R<byte>(e);
            string tex = TexturePath(m.P(e + (ulong)vOff));
            if (key < mapEntry.Length && tex != null) o.legendIcons[mapEntry[key]] = tex;
        }

        // PointOfInterestTypeMappings: TMap<FGameplayTag, EMapEntryType>
        off = P(uiCls, "PointOfInterestTypeMappings");
        stride = MapStride(8, 1, out vOff);
        data = m.P(ui + (ulong)off); max = m.R<int>(ui + (ulong)off + 12);
        for (int i = 0; Mem.ValidPtr(data) && i < max && i < 1024; i++)
        {
            ulong e = data + (ulong)(i * stride);
            string tag = ue.Name(m.R<uint>(e));
            int val = m.R<byte>(e + (ulong)vOff);
            if (tag != null && tag.Contains('.') && val < mapEntry.Length) o.poiTypeByTag[tag] = mapEntry[val];
        }
    }

    // Marker types whose own brush is a material (not extractable) use the legend icon of the same thing.
    static readonly Dictionary<string, string> LegendFallback = new()
    {
        ["TechChip"] = "ChipLocation", ["SuperTechChip"] = "ChipLocation", ["PlayerWaypoint"] = "UserWaypoint",
        ["DroppedBackpack"] = "Backpack", ["MountDroppedBackpack"] = "Backpack", ["RestingSpot"] = "RespawnPoint",
        ["MissionObjective"] = "Objective", ["ObjectiveItem"] = "Objective", ["CollectUpgrade"] = "CollectibleUpgrade",
        ["CollectColor"] = "CollectibleColor",
    };

    string MarkerIcon(string type, int variant)
    {
        if (opt.markerIcons.TryGetValue(type, out var list) && list.Length > 0)
        {
            var t = variant >= 0 && variant < list.Length ? list[variant] : list[0];
            if (t != null) return t;
        }
        if (opt.legendIcons.TryGetValue(type, out var lt)) return lt;
        return LegendFallback.TryGetValue(type, out var alias) ? opt.legendIcons.GetValueOrDefault(alias) : null;
    }

    ulong LocalPawn(out ulong pc)
    {
        pc = 0;
        ulong gi = m.P(engine + (ulong)oGameInstance);
        if (!Mem.ValidPtr(gi)) return 0;
        ulong lps = m.P(gi + (ulong)oLocalPlayers);
        if (!Mem.ValidPtr(lps) || m.R<int>(gi + (ulong)oLocalPlayers + 8) < 1) return 0;
        ulong lp = m.P(lps);
        if (!Mem.ValidPtr(lp)) return 0;
        pc = m.P(lp + (ulong)oPC);
        if (!Mem.ValidPtr(pc)) return 0;
        ulong pawn = m.P(pc + (ulong)oPawn);
        return Mem.ValidPtr(pawn) ? pawn : 0;
    }

    Snapshot ReadLocal(out ulong pc)
    {
        var s = new Snapshot();
        ulong pawn = LocalPawn(out pc);
        if (pawn == 0) return s;
        ulong root = m.P(pawn + (ulong)oRoot);
        if (!Mem.ValidPtr(root)) return s;

        if (m.P(root + (ulong)oAttachParent) == 0)
        {
            s.X = m.R<double>(root + (ulong)oRelLoc);
            s.Y = m.R<double>(root + (ulong)oRelLoc + 8);
            s.Z = m.R<double>(root + (ulong)oRelLoc + 16);
        }
        else
        {
            // Attached (zipline, etc): relative location is local, use the camera position instead.
            ulong cam = m.P(pc + (ulong)oCamMgr);
            if (!Mem.ValidPtr(cam)) return s;
            ulong a = cam + (ulong)oCamCache + (ulong)oPovLoc;
            s.X = m.R<double>(a); s.Y = m.R<double>(a + 8); s.Z = m.R<double>(a + 16);
        }
        s.Yaw = m.R<double>(pc + (ulong)oCtrlRot + 8);
        s.Valid = !double.IsNaN(s.X) && Math.Abs(s.X) < 1e7 && Math.Abs(s.Y) < 1e7;
        return s;
    }

    ulong World()
    {
        ulong vp = m.P(engine + (ulong)oViewport);
        return Mem.ValidPtr(vp) ? m.P(vp + (ulong)oWorld) : 0;
    }

    List<Marker> ReadFriends(Snapshot me)
    {
        var list = new List<Marker>();
        ulong world = World();
        if (!Mem.ValidPtr(world)) return list;
        ulong gs = m.P(world + (ulong)oGameState);
        if (!Mem.ValidPtr(gs)) return list;
        ulong arr = m.P(gs + (ulong)oPlayerArray);
        int num = m.R<int>(gs + (ulong)oPlayerArray + 8);
        if (!Mem.ValidPtr(arr) || num <= 0 || num > 16) return list;
        ulong myPawn = LocalPawn(out _);
        for (int i = 0; i < num; i++)
        {
            ulong ps = m.P(arr + (ulong)i * 8);
            if (!Mem.ValidPtr(ps)) continue;
            ulong pawn = m.P(ps + (ulong)oPawnPrivate);
            if (!Mem.ValidPtr(pawn) || pawn == myPawn) continue;
            ulong root = m.P(pawn + (ulong)oRoot);
            if (!Mem.ValidPtr(root)) continue;
            var mk = new Marker
            {
                X = m.R<double>(root + (ulong)oRelLoc),
                Y = m.R<double>(root + (ulong)oRelLoc + 8),
                Name = ue.FString(ps + (ulong)oPlayerName, 64)
            };
            if (Math.Abs(mk.X) < 1e7 && Math.Abs(mk.Y) < 1e7) list.Add(mk);
        }
        return list;
    }

    LevelInfo ReadLevel()
    {
        if (opt.oLevelData < 0) return null;
        ulong world = World();
        string worldName = Mem.ValidPtr(world) ? ue.ObjName(world) : null;
        if (string.IsNullOrEmpty(worldName)) return null;
        ulong gi = m.P(engine + (ulong)oGameInstance);
        ulong data = m.P(gi + (ulong)opt.oLevelData);
        int max = m.R<int>(gi + (ulong)opt.oLevelData + 12);
        if (!Mem.ValidPtr(data) || max <= 0 || max > 256) return null;
        for (int i = 0; i < max; i++)
        {
            ulong v = data + (ulong)(i * opt.ldStride) + 8;   // value follows the 8-byte key
            string rel = ue.Name(m.R<uint>(v + (ulong)opt.ldRel + 0x10));
            string dev = ue.Name(m.R<uint>(v + (ulong)opt.ldDev + 0x10));
            if (rel != worldName && dev != worldName) continue;
            var li = new LevelInfo
            {
                Name = worldName,
                MaxX = m.R<double>(v + (ulong)opt.ldMax), MaxY = m.R<double>(v + (ulong)opt.ldMax + 8),
                MinX = m.R<double>(v + (ulong)opt.ldMin), MinY = m.R<double>(v + (ulong)opt.ldMin + 8),
                TexW = (int)m.R<double>(v + (ulong)opt.ldTex), TexH = (int)m.R<double>(v + (ulong)opt.ldTex + 8),
                MapWidget = ue.Name(m.R<uint>(v + (ulong)opt.ldWidget + 8)),
            };
            bool sane = li.MaxX > li.MinX && li.MaxY > li.MinY && li.MaxX - li.MinX < 1e7 && li.TexW is > 0 and <= 16384 && li.TexH is > 0 and <= 16384;
            return sane ? li : null;
        }
        return null;
    }

    static MarkKind KindOf(string type) => type switch
    {
        "MarkerBuilding" or "UGCMarker" => MarkKind.Pin,
        "RestingSpot" or "StarterLocation" => MarkKind.Bed,
        "PlayerWaypoint" or "ResourceWaypoint" or "RadarLocation" or "SurveyResourceCluster" or "MarkedHaulable" => MarkKind.Ping,
        "MissionObjective" or "ObjectiveItem" or "TalkToMe" or "Tutorial" or "BurgleQuestFlag" or "DefensePoint" or "WaveSpawn" => MarkKind.Quest,
        "DroppedBackpack" or "MountDroppedBackpack" or "DroppedItem" or "ThrownItem" => MarkKind.Backpack,
        "ActivePet" => MarkKind.Pet,
        "TechChip" or "SuperTechChip" or "CollectUpgrade" or "CollectColor" or "AquaticDisc" => MarkKind.Collectible,
        "BaseUnderAttack" or "UnderAttack" or "TheftInProgress" or "NeedsRepairBuilding" => MarkKind.Base,
        _ => MarkKind.Other,
    };

    List<MapMark> ReadMarks(HashSet<ulong> pets)
    {
        var list = new List<MapMark>();
        if (opt.oMarkerMgr < 0) return list;
        ulong world = World();
        ulong gs = Mem.ValidPtr(world) ? m.P(world + (ulong)oGameState) : 0;
        ulong mgr = Mem.ValidPtr(gs) ? m.P(gs + (ulong)opt.oMarkerMgr) : 0;
        if (!Mem.ValidPtr(mgr)) return list;
        var seen = new HashSet<ulong>();
        foreach (int off in opt.markerArrays)
        {
            ulong arr = m.P(mgr + (ulong)off);
            int num = m.R<int>(mgr + (ulong)off + 8);
            if (!Mem.ValidPtr(arr) || num <= 0 || num > 5000) continue;
            var ptrs = new byte[num * 8];
            if (!m.Read(arr, ptrs, ptrs.Length)) continue;
            for (int i = 0; i < num; i++)
            {
                ulong md = BitConverter.ToUInt64(ptrs, i * 8);
                if (!Mem.ValidPtr(md) || !seen.Add(md)) continue;
                int t = m.R<byte>(md + (ulong)opt.mkType);
                string type = t < opt.markerTypes.Length ? opt.markerTypes[t] : "Type" + t;
                if (type == "Player" || type == "None") continue;      // players are drawn from PlayerArray
                if (m.R<int>(md + (ulong)opt.mkHidden + 8) > 0) continue; // hidden by a player on the game map
                var mk = new MapMark
                {
                    X = m.R<double>(md + (ulong)opt.mkLoc), Y = m.R<double>(md + (ulong)opt.mkLoc + 8), Z = m.R<double>(md + (ulong)opt.mkLoc + 16),
                    Type = type, Kind = KindOf(type), Name = ue.FString(md + (ulong)opt.mkName, 128),
                    Icon = MarkerIcon(type, m.R<sbyte>(md + (ulong)opt.mkVariant)),
                };
                if (mk.Kind == MarkKind.Pet) pets.Add(ue.Weak(md + (ulong)opt.mkRef));
                int cv = m.R<sbyte>(md + (ulong)opt.mkColorVar);
                if (cv >= 0 && cv < opt.markerColors.Length) mk.Color = Color.FromArgb(255, Color.FromArgb((int)opt.markerColors[cv]));
                else if (cv >= 0)
                {
                    uint c = m.R<uint>(md + (ulong)opt.mkColor);   // FColor = B,G,R,A
                    if (c >> 24 != 0 && (c & 0xFFFFFF) != 0xFFFFFF) mk.Color = Color.FromArgb(255, Color.FromArgb((int)c));
                }
                if (double.IsNaN(mk.X) || Math.Abs(mk.X) > 1e7 || Math.Abs(mk.Y) > 1e7 || (mk.X == 0 && mk.Y == 0)) continue;
                list.Add(mk);
            }
        }
        return list;
    }

    List<Creature> ReadCreatures(HashSet<ulong> pets)
    {
        var list = new List<Creature>();
        var actors = creatureActors;
        if (actors.Length == 0 || opt.oHealth < 0) return list;
        foreach (ulong a in actors)
        {
            if (pets.Contains(a)) continue;
            ulong hc = m.P(a + (ulong)opt.oHealth);
            if (Mem.ValidPtr(hc) && m.R<byte>(hc + (ulong)opt.oHealthState) == opt.deadState) continue;
            ulong root = m.P(a + (ulong)oRoot);
            if (!Mem.ValidPtr(root)) continue;
            double x = m.R<double>(root + (ulong)oRelLoc), y = m.R<double>(root + (ulong)oRelLoc + 8);
            if (double.IsNaN(x) || Math.Abs(x) > 1e7 || Math.Abs(y) > 1e7 || (x == 0 && y == 0)) continue;
            list.Add(new Creature { X = x, Y = y, Name = CreatureName(a) });
        }
        return list;
    }

    // "BP_Spider_DivingBell_Augusta_C" -> "Spider Diving Bell"
    string CreatureName(ulong actor)
    {
        ulong cls = ue.ObjClass(actor);
        lock (creatureNames)
        {
            if (creatureNames.TryGetValue(cls, out var n)) return n;
            n = Humanize(ue.ObjName(cls), new[] { "BP", "C", "Augusta", "Base" });
            creatureNames[cls] = n;
            return n;
        }
    }

    public static string Humanize(string raw, string[] drop)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(p => !drop.Contains(p, StringComparer.OrdinalIgnoreCase));
        return Regex.Replace(string.Join(" ", parts), "(?<=[a-z])(?=[A-Z])", " ");
    }

    List<Poi> ReadPois(ulong pc)
    {
        var list = new List<Poi>();
        var comps = poiComps;
        if (comps.Length == 0 || opt.oDiscovered < 0 || !Mem.ValidPtr(pc)) return list;
        ulong ps = m.P(pc + (ulong)opt.oPlayerState);
        if (!Mem.ValidPtr(ps)) return list;
        ulong arr = m.P(ps + (ulong)opt.oDiscovered);
        int num = m.R<int>(ps + (ulong)opt.oDiscovered + 8);
        if (!Mem.ValidPtr(arr) || num <= 0 || num > 10000) return list;
        var buf = new byte[num * 8];
        if (!m.Read(arr, buf, buf.Length)) return list;
        var discovered = new HashSet<ulong>();
        for (int i = 0; i < num; i++) discovered.Add(BitConverter.ToUInt64(buf, i * 8));
        string level = Snap.Level?.Name?.Split('_')[0];
        foreach (var (comp, data, actor) in comps)
        {
            if (!discovered.Contains(data)) continue;
            ulong root = m.P(actor + (ulong)oRoot);
            if (!Mem.ValidPtr(root)) continue;
            double x = m.R<double>(root + (ulong)oRelLoc), y = m.R<double>(root + (ulong)oRelLoc + 8);
            if (double.IsNaN(x) || Math.Abs(x) > 1e7 || Math.Abs(y) > 1e7) continue;
            string name;
            lock (poiNames)
                if (!poiNames.TryGetValue(data, out name))
                    poiNames[data] = name = Humanize(ue.ObjName(data), new[] { "POI", level ?? "" });
            // Some places have several trigger actors: keep one per place and area.
            if (list.Any(q => q.Name == name && Math.Abs(q.X - x) + Math.Abs(q.Y - y) < 8000)) continue;
            string icon = opt.legendIcons.GetValueOrDefault("POILocation");
            if (opt.oPoiTag >= 0 && ue.Name(m.R<uint>(data + (ulong)opt.oPoiTag)) is string tag
                && opt.poiTypeByTag.TryGetValue(tag, out var entry) && opt.legendIcons.TryGetValue(entry, out var ti)) icon = ti;
            list.Add(new Poi { X = x, Y = y, Name = name, Icon = icon });
        }
        return list;
    }

    // ---- World scanner: walks GUObjectArray every couple of seconds to find creatures and POIs.
    // Only objects whose slot changed since the last pass are inspected, so later passes are cheap.
    void ScanLoop()
    {
        ulong[] seenPtr = Array.Empty<ulong>();
        byte[] kind = Array.Empty<byte>();
        var classKind = new Dictionary<ulong, byte>();
        UE cur = null;
        var chunkPtrs = new ulong[0x10000];
        byte[] scratch = null;
        while (!stop)
        {
            Thread.Sleep(2000);
            var u = ue; var o = opt;
            if (u == null || o == null || (o.creatureCls == 0 && o.poiCompCls == 0)) continue;
            try
            {
                if (u != cur) { cur = u; seenPtr = Array.Empty<ulong>(); kind = Array.Empty<byte>(); classKind.Clear(); scratch = new byte[0x10000 * u.ItemSize]; }
                int n = u.NumObjects;
                if (seenPtr.Length < n) { Array.Resize(ref seenPtr, n + 0x10000); Array.Resize(ref kind, n + 0x10000); }
                var creatures = new List<ulong>();
                var pois = new List<(ulong, ulong, ulong)>();
                for (int ch = 0; ch * 0x10000 < n; ch++)
                {
                    int cnt = u.ReadChunk(ch, chunkPtrs, scratch);
                    for (int i = 0; i < cnt; i++)
                    {
                        int idx = ch * 0x10000 + i;
                        ulong ptr = chunkPtrs[i];
                        if (ptr != seenPtr[idx])
                        {
                            seenPtr[idx] = ptr;
                            kind[idx] = 0;
                            if (Mem.ValidPtr(ptr))
                            {
                                ulong cls = u.ObjClass(ptr);
                                if (!classKind.TryGetValue(cls, out byte k))
                                {
                                    k = (byte)(o.creatureCls != 0 && u.IsA(ptr, o.creatureCls) ? 1 : o.poiCompCls != 0 && u.IsA(ptr, o.poiCompCls) ? 2 : 0);
                                    classKind[cls] = k;
                                }
                                if (k != 0 && u.ObjName(ptr)?.StartsWith("Default__") != false) k = 0;
                                kind[idx] = k;
                            }
                        }
                        if (kind[idx] == 1) creatures.Add(ptr);
                        else if (kind[idx] == 2 && o.oPoiData >= 0)
                        {
                            ulong data = u.M.P(ptr + (ulong)o.oPoiData);
                            ulong owner = u.M.P(ptr + UE.O_Outer);
                            if (Mem.ValidPtr(data) && Mem.ValidPtr(owner)) pois.Add((ptr, data, owner));
                        }
                    }
                }
                creatureActors = creatures.ToArray();
                poiComps = pois.ToArray();
            }
            catch { }
        }
    }
}
