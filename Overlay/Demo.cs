// Sample data for --demo (README screenshots without the game running). Uses the real map and
// the game's icons from the icons\ cache, but made-up player, friend and marker names.
static class Demo
{
    public static bool On => Environment.GetCommandLineArgs().Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
    public static bool Big => Environment.GetCommandLineArgs().Any(a => a.Equals("--demo-big", StringComparison.OrdinalIgnoreCase));

    const string Icons = "/Game/UI/Images/MapIcons/";
    public const string PlayerIcon = Icons + "T_UI_MapMarker_Teen";

    public static Snapshot Snapshot()
    {
        double px = -181800, py = 29500;
        return new Snapshot
        {
            Valid = true, X = px, Y = py, Yaw = -35,
            Level = new LevelInfo { Name = "Augusta_Main", MinX = -261500, MaxX = 73500, MinY = -137500, MaxY = 197500, TexW = 4096, TexH = 4096 },
            Friends = { new Marker { X = px + 5200, Y = py - 3600, Name = "Friend" } },
            Marks =
            {
                new MapMark { X = px - 4200, Y = py + 2600, Kind = MarkKind.Pin, Type = "MarkerBuilding", Name = "Base", Icon = Icons + "T_UI_Flag_Map_Home", Color = Color.FromArgb(255, 140, 103, 226) },
                new MapMark { X = px - 3400, Y = py + 3600, Kind = MarkKind.Bed, Type = "RestingSpot", Icon = Icons + "T_UI_MapMarker_Respawn" },
                new MapMark { X = px + 7000, Y = py + 4500, Kind = MarkKind.Pin, Type = "MarkerBuilding", Name = "Water", Icon = Icons + "T_UI_Flag_Map_A", Color = Color.FromArgb(255, 80, 149, 192) },
                new MapMark { X = px + 2600, Y = py - 6800, Kind = MarkKind.Ping, Type = "PlayerWaypoint", Icon = Icons + "T_UI_MapMarker_Waypoint_Filter" },
                new MapMark { X = px - 1500, Y = py - 9000, Kind = MarkKind.Backpack, Type = "DroppedBackpack", Icon = Icons + "T_UI_MapMarker_Backpack" },
                new MapMark { X = px - 40000, Y = py + 30000, Kind = MarkKind.Pin, Type = "MarkerBuilding", Name = "Cave", Icon = Icons + "T_UI_Flag_Map_B", Color = Color.FromArgb(255, 174, 69, 71) },
            },
            Pois =
            {
                new Poi { X = -187524, Y = 34206, Name = "Outpost Flower Bed", Icon = Icons + "T_UI_MapMarker_POI" },
                new Poi { X = -184288, Y = 24929, Name = "Statue Checkpoint", Icon = Icons + "T_UI_MapMarker_POI" },
                new Poi { X = -175395, Y = 15020, Name = "Network Center", Icon = Icons + "T_UI_MapMarker_Lab" },
            },
            Creatures =
            {
                new Creature { X = px + 3000, Y = py + 1800, Name = "Spider Wolf" },
                new Creature { X = px - 2600, Y = py - 3200, Name = "Ladybug" },
                new Creature { X = px + 1200, Y = py - 2400, Name = "Aphid" },
                new Creature { X = px - 5200, Y = py - 500, Name = "Ant Worker" },
                new Creature { X = px + 4800, Y = py - 900, Name = "Aphid" },
            },
        };
    }

    // A curvy path that ends at the player, newest last.
    public static IEnumerable<(double X, double Y, long T)> Trail(Snapshot s, long now)
    {
        for (int i = 60; i >= 0; i--)
        {
            double t = i / 60.0;
            yield return (s.X - 9000 * t + 1800 * Math.Sin(t * 7), s.Y + 7000 * t * t - 1200 * Math.Cos(t * 5) + 1200, now - i * 2500L);
        }
    }

    // Left-most monitor, so demo screenshots don't cover the screen in use.
    public static Rectangle Area() => Screen.AllScreens.OrderBy(sc => sc.Bounds.X).First().Bounds;
}
