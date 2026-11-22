using System.Diagnostics;

var sw = Stopwatch.StartNew();
var m = Mem.Attach("Grounded2Steam-Win64-Shipping");
if (m == null) { Console.WriteLine("game not found / cannot open"); return; }
var ue = new UE(m);
if (!ue.Init(Console.WriteLine)) { Console.WriteLine("init failed"); return; }
Console.WriteLine($"init {sw.ElapsedMilliseconds}ms, objects={ue.NumObjects}");

ulong lpClass = ue.FindObject("Class /Script/Engine.LocalPlayer");
ulong playerCls = ue.FindObject("Class /Script/Engine.Player");
ulong ctrlCls = ue.FindObject("Class /Script/Engine.Controller");
ulong pcCls = ue.FindObject("Class /Script/Engine.PlayerController");
ulong actorCls = ue.FindObject("Class /Script/Engine.Actor");
ulong sceneCls = ue.FindObject("Class /Script/Engine.SceneComponent");
int oPC = ue.PropOffset(playerCls, "PlayerController");
int oPawn = ue.PropOffset(pcCls, "AcknowledgedPawn");
int oCtrlRot = ue.PropOffset(ctrlCls, "ControlRotation");
int oRoot = ue.PropOffset(actorCls, "RootComponent");
int oRelLoc = ue.PropOffset(sceneCls, "RelativeLocation");
Console.WriteLine($"offsets PC=0x{oPC:X} Pawn=0x{oPawn:X} CtrlRot=0x{oCtrlRot:X} Root=0x{oRoot:X} RelLoc=0x{oRelLoc:X}  ({sw.ElapsedMilliseconds}ms)");

int n = ue.NumObjects;
for (int i = 0; i < n; i++)
{
    ulong o = ue.GetObject(i);
    if (o == 0 || !ue.IsA(o, lpClass) || ue.ObjName(o).StartsWith("Default__")) continue;
    Console.WriteLine($"LocalPlayer {ue.FullName(o)}");
    ulong pc = m.P(o + (ulong)oPC);
    ulong pawn = pc != 0 ? m.P(pc + (ulong)oPawn) : 0;
    Console.WriteLine($"  PC {(pc != 0 ? ue.FullName(pc) : "null")}");
    Console.WriteLine($"  Pawn {(pawn != 0 ? ue.FullName(pawn) : "null")}");
    if (pawn == 0) continue;
    ulong root = m.P(pawn + (ulong)oRoot);
    Console.WriteLine($"  Root {ue.FullName(root)}");
    for (int k = 0; k < 5; k++)
    {
        double x = m.R<double>(root + (ulong)oRelLoc), y = m.R<double>(root + (ulong)oRelLoc + 8), z = m.R<double>(root + (ulong)oRelLoc + 16);
        double yaw = m.R<double>(pc + (ulong)oCtrlRot + 8);
        Console.WriteLine($"  pos ({x:F0}, {y:F0}, {z:F0}) yaw {yaw:F1}");
        Thread.Sleep(500);
    }
}
Console.WriteLine($"done {sw.ElapsedMilliseconds}ms");
