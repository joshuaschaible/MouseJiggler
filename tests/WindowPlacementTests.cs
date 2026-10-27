using System;
using System.Drawing;
using System.Windows.Forms;
using MouseJigglerApp;

// Compile with MouseJiggler.cs and /main:WindowPlacementTests. No forms are
// created, hotkeys registered, input injected, or user settings read/written.
static class WindowPlacementTests
{
    private static int checks;

    private static void Equal<T>(T expected, T actual, string name)
    {
        checks++;
        if (!object.Equals(expected, actual))
            throw new Exception(name + ": expected " + expected + ", got " + actual);
    }

    private static void True(bool condition, string name)
    {
        Equal(true, condition, name);
    }

    public static void Main()
    {
        var layout = new[] {
            new Rectangle(0, 0, 1920, 1040),
            new Rectangle(-1600, 120, 1600, 860),
            new Rectangle(200, -1200, 1920, 1160),
            new Rectangle(150, 1080, 1280, 984),
            new Rectangle(1920, 0, 2560, 1400)
        };
        Equal(1, WindowPlacement.FindScreenInDirection(0, layout, Keys.Left), "negative left monitor");
        Equal(4, WindowPlacement.FindScreenInDirection(0, layout, Keys.Right), "right monitor over diagonal");
        Equal(2, WindowPlacement.FindScreenInDirection(0, layout, Keys.Up), "upper monitor");
        Equal(3, WindowPlacement.FindScreenInDirection(0, layout, Keys.Down), "lower monitor");
        Equal(-1, WindowPlacement.FindScreenInDirection(1, layout, Keys.Left), "no wrap at left edge");
        Equal(-1, WindowPlacement.FindScreenInDirection(0, new[] { layout[0] }, Keys.Right), "single monitor");
        Equal(-1, WindowPlacement.FindScreenInDirection(-1, layout, Keys.Right), "no current monitor");
        Equal(-1, WindowPlacement.FindScreenInDirection(0, layout, Keys.Home), "unsupported direction");

        Equal(1, WindowPlacement.FindScreen(new Rectangle(-1500, 200, 300, 600), layout), "negative saved position");
        Equal(4, WindowPlacement.FindScreen(new Rectangle(1850, 100, 300, 600), layout), "straddling most visible monitor");
        Equal(-1, WindowPlacement.FindScreen(new Rectangle(0, 0, 300, 600), new Rectangle[0]), "empty display list");
        Equal(new Rectangle(1620, 440, 300, 600),
            WindowPlacement.Clamp(new Rectangle(1919, 1039, 300, 600), layout[0]), "one pixel visible clamps fully");
        Equal(new Rectangle(-1600, 120, 300, 600),
            WindowPlacement.Clamp(new Rectangle(-1800, -50, 300, 600), layout[1]), "left/top clipped on negative monitor");
        Equal(new Rectangle(1620, 440, 300, 600),
            WindowPlacement.Clamp(new Rectangle(int.MaxValue, int.MaxValue, 300, 600), layout[0]), "invalid saved coordinates");
        Equal(new Rectangle(4480 - 300, 800, 300, 600),
            WindowPlacement.Move(new Rectangle(1620, 440, 300, 600), layout[0], layout[4]), "bottom-right preserved across resolutions");
        Equal(new Rectangle(-950, 250, 300, 600),
            WindowPlacement.Move(new Rectangle(810, 220, 300, 600), layout[0], layout[1]), "centered placement across offset displays");
        Equal(new Rectangle(0, 40, 300, 600),
            WindowPlacement.Center(new Rectangle(9000, 9000, 300, 600), new Rectangle(0, 40, 200, 400)), "oversized window keeps title reachable");

        // Exercise clamping and proportional moves through diverse dimensions,
        // offsets, taskbar-sized work areas, and windows larger than a monitor.
        var random = new Random(20261009);
        for (int i = 0; i < 2000; i++)
        {
            Rectangle source = RandomArea(random);
            Rectangle target = RandomArea(random);
            var window = new Rectangle(random.Next(-10000, 10001), random.Next(-10000, 10001),
                random.Next(200, 701), random.Next(300, 801));
            Rectangle moved = WindowPlacement.Move(window, source, target);
            Equal(window.Size, moved.Size, "move retains size");
            Equal(moved, WindowPlacement.Clamp(moved, target), "move already clamped");
            True(moved.Left >= target.Left && moved.Top >= target.Top, "title stays on target");
            if (window.Width <= target.Width && window.Height <= target.Height)
                True(target.Contains(moved), "fitting window fully visible");
        }
        Console.WriteLine("Passed " + checks + " window placement checks.");
    }

    private static Rectangle RandomArea(Random random)
    {
        return new Rectangle(random.Next(-5000, 5001), random.Next(-5000, 5001),
            random.Next(200, 3001), random.Next(250, 2001));
    }
}
