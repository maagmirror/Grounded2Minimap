using System.Text;

public sealed class Marker
{
    public double X, Y;
    public string Name;
}

public sealed class Snapshot
{
    public bool Valid;
    public double X, Y, Z, Yaw;
    public List<Marker> Friends = new();
}

// Background thread: attaches to the game (read-only), resolves UE reflection offsets by name,
// then polls the local player's position ~30 times per second.
public sealed class GameReader
{
    const string ProcName = "Grounded2Steam-Win64-Shipping";

    public volatile Snapshot Snap = new();
    public volatile string Status = "Buscando Grounded 2...";
    public int GamePid => m?.Pid ?? 0;

    Mem m;
    UE ue;
    ulong engine;
    int oGameInstance, oLocalPlayers, oPC, oPawn, oCtrlRot, oRoot, oRelLoc, oAttachParent;
    int oCamMgr, oCamCache, oPovLoc;
    int oViewport, oWorld, oGameState, oPlayerArray, oPawnPrivate, oPlayerName;
    bool stop;

    public void Start()
    {
        var t = new Thread(Loop) { IsBackground = true, Name = "GameReader", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }
    public void Stop() => stop = true;

    void Loop()
    {
        int tick = 0;
        var friends = new List<Marker>();
        while (!stop)
        {
            try
            {
                if (m == null || !m.Alive)
                {
                    Snap = new Snapshot();
                    m?.Dispose(); m = null; ue = null; engine = 0;
                    Status = "Buscando Grounded 2...";
                    if (!TryAttach()) { Thread.Sleep(2000); continue; }
                }
                if (engine == 0 && !Resolve()) { Thread.Sleep(3000); continue; }

                var s = ReadLocal();
                if (s.Valid)
                {
                    if (tick % 30 == 0) friends = ReadFriends(s);
                    s.Friends = friends;
                    Status = "OK";
                }
                else Status = "En el menu / cargando...";
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

    bool TryAttach()
    {
        m = Mem.Attach(ProcName);
        if (m == null) return false;
        Status = "Leyendo datos del juego...";
        ue = new UE(m);
        if (!ue.Init(_ => { })) { Status = "No se pudo leer el juego (version incompatible?)"; m.Dispose(); m = null; return false; }
        return true;
    }

    int Off(string cls, string prop)
    {
        ulong c = ue.FindObject((cls.EndsWith("CameraCacheEntry") || cls.EndsWith("MinimalViewInfo") ? "ScriptStruct " : "Class ") + cls);
        int o = c == 0 ? -1 : ue.PropOffset(c, prop);
        if (o < 0) throw new Exception($"offset {cls}.{prop} no encontrado");
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
        if (engine == 0) { Status = "GameEngine no encontrado"; return false; }
        return true;
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

    Snapshot ReadLocal()
    {
        var s = new Snapshot();
        ulong pawn = LocalPawn(out ulong pc);
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

    List<Marker> ReadFriends(Snapshot me)
    {
        var list = new List<Marker>();
        ulong vp = m.P(engine + (ulong)oViewport);
        if (!Mem.ValidPtr(vp)) return list;
        ulong world = m.P(vp + (ulong)oWorld);
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
                Name = ReadFString(ps + (ulong)oPlayerName)
            };
            if (Math.Abs(mk.X) < 1e7 && Math.Abs(mk.Y) < 1e7) list.Add(mk);
        }
        return list;
    }

    string ReadFString(ulong addr)
    {
        ulong data = m.P(addr);
        int len = m.R<int>(addr + 8);
        if (!Mem.ValidPtr(data) || len <= 1 || len > 64) return "";
        var b = new byte[(len - 1) * 2];
        return m.Read(data, b, b.Length) ? Encoding.Unicode.GetString(b) : "";
    }
}

