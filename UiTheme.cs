namespace RxBarcodeListener;

/// <summary>
/// Shared dark color palette and fonts used by ToastWindow and DiagnosticsWindow.
/// Centralizing these here means the two windows can't visually drift apart, and a
/// palette tweak only needs to happen in one place instead of N scattered literals.
/// </summary>
internal static class UiTheme
{
    public static readonly Color Background      = Color.FromArgb(26, 26, 46);   // #1a1a2e
    public static readonly Color Panel           = Color.FromArgb(36, 36, 58);
    public static readonly Color InputBackground = Color.FromArgb(18, 18, 32);

    public static readonly Color TextPrimary = Color.White;
    public static readonly Color TextMuted   = Color.FromArgb(170, 170, 170);
    public static readonly Color TextDim     = Color.FromArgb(80, 80, 80);

    public static readonly Color AccentGreen  = Color.FromArgb(0, 160, 120);
    public static readonly Color AccentRed    = Color.FromArgb(220, 80, 80);
    public static readonly Color AccentDanger = Color.FromArgb(255, 80, 80); // toast warning headers
    public static readonly Color AccentBlue   = Color.FromArgb(0, 120, 180);
    public static readonly Color AccentAmber  = Color.FromArgb(240, 165, 0);

    public static readonly Color ButtonNeutral = Color.FromArgb(70, 70, 100);

    // Fonts are process-lifetime singletons — created once, never disposed (reclaimed
    // on process exit), instead of a fresh Font object per label/window creation.
    public static readonly Font TitleFont = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font BodyFont  = new("Segoe UI", 9.5f);
    public static readonly Font MonoFont  = new("Consolas", 9f);
}
