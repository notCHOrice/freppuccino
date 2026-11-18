namespace Freppuccino.Display;

/// Session-only (not persisted).
public static class FrepSettings
{
    /// Smooth-union blend k, in model units. 0 = hard union.
    public static double Blend = 0.5;

    /// FZebra: reflection stripes on the surfaces of FUnion nodes (viewport analysis; uniforms only, never recompiles).
    /// On while the FZebra options window is open (like Rhino's Zebra); edited live from <see cref="Ui.ZebraOptionsForm"/>.
    public static bool Zebra = false;
    /// Number of stripes across the half-sphere of reflected directions.
    public static int ZebraStripes = 12;
    /// Stripes run vertically (true) or horizontally in the view, like Rhino's Zebra "Stripe direction".
    public static bool ZebraVertical = true;
    public static System.Drawing.Color ZebraColor1 = System.Drawing.Color.Black;
    public static System.Drawing.Color ZebraColor2 = System.Drawing.Color.White;
}
