using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Read-only access to an Unreal Engine 5 process. Never writes memory.
public sealed class Mem : IDisposable
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern unsafe bool ReadProcessMemory(IntPtr h, IntPtr addr, void* buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    const uint PROCESS_VM_READ = 0x10, PROCESS_QUERY_LIMITED = 0x1000;
    public IntPtr Handle;
    public ulong Base;
    public int ModuleSize;
    public int Pid;

    // Tries each process name (Steam, Xbox/WinGDK, …). Returns null if none are running
    // or if the process can't be opened for read (common on some Game Pass builds).
    public static Mem Attach(params string[] procNames)
    {
        foreach (var procName in procNames)
        {
            foreach (var p in Process.GetProcessesByName(procName))
            {
                try
                {
                    var m = new Mem { Pid = p.Id };
                    m.Handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_LIMITED, false, p.Id);
                    if (m.Handle == IntPtr.Zero) continue;
                    m.Base = (ulong)p.MainModule.BaseAddress;
                    m.ModuleSize = p.MainModule.ModuleMemorySize;
                    return m;
                }
                catch { /* MainModule can throw on locked / store processes */ }
            }
        }
        return null;
    }

    // True if any of the named processes is running (even when OpenProcess fails).
    public static bool IsRunning(params string[] procNames)
        => procNames.Any(n => Process.GetProcessesByName(n).Length > 0);

    public bool Alive => GetExitCodeProcess(Handle, out var c) && c == 259;

    public bool Read(ulong addr, byte[] buf, int len)
        => ReadProcessMemory(Handle, (IntPtr)(long)addr, buf, (IntPtr)len, out var r) && (int)r == len;

    public unsafe T R<T>(ulong addr) where T : unmanaged
    {
        T v = default;
        ReadProcessMemory(Handle, (IntPtr)(long)addr, &v, (IntPtr)sizeof(T), out _);
        return v;
    }
    public ulong P(ulong addr) => R<ulong>(addr);
    public static bool ValidPtr(ulong p) => p > 0x10000 && p < 0x7FFFFFFFFFFF && (p & 7) == 0;

    public void Dispose() { if (Handle != IntPtr.Zero) CloseHandle(Handle); Handle = IntPtr.Zero; }
}

// Minimal UE5 reflection reader: FNamePool, GUObjectArray, UClass properties.
public sealed class UE
{
    public readonly Mem M;
    public ulong NamePoolBlocks;   // address of Blocks[0] in module
    public ulong ObjObjects;       // address of FChunkedFixedUObjectArray
    public int ItemSize = 24, ItemObj = 0;
    // UObject layout (stable across UE4.25+/UE5)
    public const int O_Index = 0x0C, O_Class = 0x10, O_Name = 0x18, O_Outer = 0x20;
    // UStruct / FField offsets (detected)
    public int S_Super = 0x40, S_ChildProps = 0x50;
    public int F_Next = 0x18, F_Name = 0x20, P_Offset = 0x44;
    readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> nameCache = new();

    public UE(Mem m) { M = m; }

    (ulong start, int size)[] DataSections()
    {
        var hdr = new byte[0x1000];
        M.Read(M.Base, hdr, hdr.Length);
        int pe = BitConverter.ToInt32(hdr, 0x3C);
        int n = BitConverter.ToUInt16(hdr, pe + 6);
        int optSize = BitConverter.ToUInt16(hdr, pe + 20);
        int sec = pe + 24 + optSize;
        var list = new List<(ulong, int)>();
        for (int i = 0; i < n; i++)
        {
            int o = sec + i * 40;
            string name = Encoding.ASCII.GetString(hdr, o, 8).TrimEnd('\0');
            int vsize = BitConverter.ToInt32(hdr, o + 8);
            int va = BitConverter.ToInt32(hdr, o + 12);
            uint ch = BitConverter.ToUInt32(hdr, o + 36);
            bool writable = (ch & 0x80000000) != 0;
            if (writable || name == ".data") list.Add((M.Base + (ulong)va, vsize));
        }
        return list.ToArray();
    }

    public bool Init(Action<string> log)
    {
        var buf = new byte[0x10000];
        var probe = new byte[32];
        foreach (var (start, size) in DataSections())
        {
            for (int off = 0; off < size; off += buf.Length - 64)
            {
                int len = Math.Min(buf.Length, size - off);
                if (len < 64 || !M.Read(start + (ulong)off, buf, len)) continue;
                for (int i = 0; i + 48 <= len; i += 8)
                {
                    ulong a = start + (ulong)off + (ulong)i;
                    // --- GUObjectArray.ObjObjects: Objects**, PreAlloc*, Max, Num, MaxChunks, NumChunks
                    if (ObjObjects == 0)
                    {
                        ulong objs = BitConverter.ToUInt64(buf, i);
                        int max = BitConverter.ToInt32(buf, i + 16), num = BitConverter.ToInt32(buf, i + 20);
                        int maxCh = BitConverter.ToInt32(buf, i + 24), numCh = BitConverter.ToInt32(buf, i + 28);
                        // NumChunks can exceed what Num needs (chunks get preallocated), so only bound it.
                        if (Mem.ValidPtr(objs) && max >= 0x10000 && max < 0x4000000 && num > 0x1000 && num <= max
                            && maxCh == (max + 0xFFFF) / 0x10000 && numCh >= (num + 0xFFFF) / 0x10000 && numCh <= maxCh)
                        {
                            ObjObjects = a;
                            if (DetectItemLayout())
                                log($"GUObjectArray.ObjObjects @ +0x{a - M.Base:X} num={num} max={max} chunks={numCh}/{maxCh}");
                            else ObjObjects = 0;
                        }
                    }
                    // --- FNamePool.Blocks[0] -> "None", "ByteProperty"
                    if (NamePoolBlocks == 0)
                    {
                        ulong b0 = BitConverter.ToUInt64(buf, i);
                        uint curBlock = BitConverter.ToUInt32(buf, i - 8 >= 0 ? i - 8 : i);
                        if (i >= 8 && Mem.ValidPtr(b0) && curBlock > 0 && curBlock < 8192)
                        {
                            if (M.Read(b0, probe, 24) && Encoding.ASCII.GetString(probe, 2, 4) == "None"
                                && Encoding.ASCII.GetString(probe, 8, 12) == "ByteProperty")
                            {
                                NamePoolBlocks = a;
                                log($"FNamePool.Blocks @ +0x{a - M.Base:X} blocks={curBlock + 1}");
                            }
                        }
                    }
                }
                if (ObjObjects != 0 && NamePoolBlocks != 0) break;
            }
            if (ObjObjects != 0 && NamePoolBlocks != 0) break;
        }
        if (ObjObjects == 0 || NamePoolBlocks == 0) return false;

        log($"FUObjectItem size={ItemSize} objOff={ItemObj}");
        return DetectFieldLayout(log);
    }

    // FUObjectItem size / object pointer offset: object #t's InternalIndex must be t.
    bool DetectItemLayout()
    {
        foreach (int sz in new[] { 24, 16, 32, 40 })
            foreach (int ob in new[] { 0, 8, 16 })
            {
                if (ob >= sz) continue;
                ItemSize = sz; ItemObj = ob;
                bool ok = true;
                foreach (int t in new[] { 1, 2, 5, 77, 1000 })
                {
                    ulong ot = GetObject(t);
                    if (!Mem.ValidPtr(ot) || M.R<int>(ot + O_Index) != t) { ok = false; break; }
                }
                if (ok) return true;
            }
        return false;
    }

    public string Name(uint idx)
    {
        if (nameCache.TryGetValue(idx, out var s)) return s;
        uint block = idx >> 16, offs = (idx & 0xFFFF) * 2;
        ulong blk = M.P(NamePoolBlocks + block * 8);
        if (blk == 0) return null;
        ushort hdr = M.R<ushort>(blk + offs);
        int len = hdr >> 6; bool wide = (hdr & 1) != 0;
        if (len <= 0 || len > 1024) return null;
        var b = new byte[wide ? len * 2 : len];
        if (!M.Read(blk + offs + 2, b, b.Length)) return null;
        s = wide ? Encoding.Unicode.GetString(b) : Encoding.ASCII.GetString(b);
        nameCache[idx] = s;
        return s;
    }

    public int NumObjects => M.R<int>(ObjObjects + 20);
    public ulong GetObject(int i)
    {
        ulong chunks = M.P(ObjObjects);
        ulong chunk = M.P(chunks + (ulong)(i / 0x10000) * 8);
        if (chunk == 0) return 0;
        return M.P(chunk + (ulong)(i % 0x10000) * (ulong)ItemSize + (ulong)ItemObj);
    }
    public string ObjName(ulong o) => o == 0 ? null : Name(M.R<uint>(o + O_Name));
    public ulong ObjClass(ulong o) => M.P(o + O_Class);
    public string ClassName(ulong o) => ObjName(ObjClass(o));

    public string FullName(ulong o)
    {
        var parts = new List<string>();
        for (ulong x = o; x != 0 && parts.Count < 16; x = M.P(x + O_Outer)) parts.Add(ObjName(x));
        parts.Reverse();
        return ClassName(o) + " " + string.Join(".", parts);
    }

    public ulong FindObject(string fullName)
    {
        // Cheap check on the object's own name first; build the full path only for those.
        int sp = fullName.IndexOf(' ');
        string last = fullName[(sp + 1)..];
        int cut = last.LastIndexOfAny(new[] { '.', '/', ':' });
        if (cut >= 0) last = last[(cut + 1)..];
        int n = NumObjects;
        for (int i = 0; i < n; i++)
        {
            ulong o = GetObject(i);
            if (o != 0 && ObjName(o) == last && FullName(o) == fullName) return o;
        }
        return 0;
    }

    // Reads all object pointers of one GUObjectArray chunk in a single call.
    public int ReadChunk(int chunk, ulong[] dst, byte[] scratch)
    {
        ulong chunks = M.P(ObjObjects);
        ulong c = M.P(chunks + (ulong)chunk * 8);
        int count = Math.Min(0x10000, NumObjects - chunk * 0x10000);
        if (c == 0 || count <= 0 || !M.Read(c, scratch, count * ItemSize)) return 0;
        for (int i = 0; i < count; i++) dst[i] = BitConverter.ToUInt64(scratch, i * ItemSize + ItemObj);
        return count;
    }

    // FWeakObjectPtr = { ObjectIndex, ObjectSerialNumber }
    public ulong Weak(ulong addr)
    {
        int idx = M.R<int>(addr);
        return idx > 0 && idx < NumObjects ? GetObject(idx) : 0;
    }

    public string FString(ulong addr, int maxLen = 256)
    {
        ulong data = M.P(addr);
        int len = M.R<int>(addr + 8);
        if (!Mem.ValidPtr(data) || len <= 1 || len > maxLen) return "";
        var b = new byte[(len - 1) * 2];
        return M.Read(data, b, b.Length) ? Encoding.Unicode.GetString(b) : "";
    }

    // UEnum names in value order ("EFoo::Bar" -> "Bar"), until the *_MAX entry.
    public string[] EnumNames(ulong en)
    {
        if (en == 0) return Array.Empty<string>();
        for (int off = 0x30; off <= 0x60; off += 8)
        {
            ulong arr = M.P(en + (ulong)off) & ~7UL;
            if (!Mem.ValidPtr(arr)) continue;
            foreach (int stride in new[] { 8, 16 })
            {
                var list = new List<string>();
                for (int k = 0; k < 512; k++)
                {
                    var s = Name(M.R<uint>(arr + (ulong)(k * stride)));
                    if (s == null || !s.Contains("::")) break;
                    s = s[(s.LastIndexOf("::") + 2)..];
                    if (s.EndsWith("_MAX")) return list.ToArray();
                    list.Add(s);
                }
            }
        }
        return Array.Empty<string>();
    }

    // Size of a UScriptStruct / UClass instance.
    public int StructSize(ulong st) => M.R<int>(st + (ulong)S_ChildProps + 8);

    public bool IsA(ulong obj, ulong cls)
    {
        for (ulong c = ObjClass(obj); c != 0; c = M.P(c + (ulong)S_Super)) if (c == cls) return true;
        return false;
    }

    // Walk class + supers looking for an FProperty by name, return Offset_Internal.
    public int PropOffset(ulong cls, string prop)
    {
        for (ulong c = cls; c != 0; c = M.P(c + (ulong)S_Super))
            for (ulong f = M.P(c + (ulong)S_ChildProps); f != 0; f = M.P(f + (ulong)F_Next))
                if (Name(M.R<uint>(f + (ulong)F_Name)) == prop) return M.R<int>(f + (ulong)P_Offset);
        return -1;
    }

    bool DetectFieldLayout(Action<string> log)
    {
        // Use /Script/Engine.Actor which has known properties.
        ulong actor = FindObject("Class /Script/Engine.Actor");
        ulong obj = FindObject("Class /Script/CoreUObject.Object");
        log($"Actor class 0x{actor:X}, Object class 0x{obj:X}");
        if (actor == 0) return false;
        // SuperStruct: pointer in Actor that points to Object class
        for (int o = 0x28; o < 0x60; o += 8) if (M.P(actor + (ulong)o) == obj) { S_Super = o; break; }
        // ChildProperties + FField name/next: find pointer whose target has a name resolving to an Actor property
        var known = new HashSet<string> { "PrimaryActorTick", "bNetTemporary", "bHidden", "bOnlyRelevantToOwner", "RootComponent", "UpdateOverlapsMethodDuringLevelStreaming", "bReplicateMovement", "bAutoDestroyWhenFinished", "bCanBeDamaged", "bActorIsBeingDestroyed", "bReplicates", "RemoteRole", "ReplicatedMovement", "InitialLifeSpan", "CustomTimeDilation" };
        for (int co = S_Super + 8; co < 0x80; co += 8)
        {
            ulong f = M.P(actor + (ulong)co);
            if (!Mem.ValidPtr(f)) continue;
            foreach (int no in new[] { 0x20, 0x28, 0x18, 0x30 })
            {
                var n = Name(M.R<uint>(f + (ulong)no));
                if (n == null || !known.Contains(n)) continue;
                foreach (int nx in new[] { 0x18, 0x20, 0x10, 0x28 })
                {
                    ulong f2 = M.P(f + (ulong)nx);
                    if (Mem.ValidPtr(f2) && Name(M.R<uint>(f2 + (ulong)no)) is string n2 && known.Contains(n2))
                    {
                        S_ChildProps = co; F_Name = no; F_Next = nx;
                        // Offset_Internal: RootComponent offset is a small aligned int; ArrayDim==1 precedes ElementSize
                        foreach (int po in new[] { 0x44, 0x4C, 0x3C, 0x50, 0x54 })
                        {
                            P_Offset = po;
                            int rc = PropOffset(actor, "RootComponent");
                            if (rc > 0x28 && rc < 0x400 && rc % 8 == 0) break;
                        }
                        log($"Layout: Super=0x{S_Super:X} ChildProps=0x{S_ChildProps:X} F_Name=0x{F_Name:X} F_Next=0x{F_Next:X} P_Offset=0x{P_Offset:X} RootComponent=0x{PropOffset(actor, "RootComponent"):X}");
                        return true;
                    }
                }
            }
        }
        return false;
    }
}

