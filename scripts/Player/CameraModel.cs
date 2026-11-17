using System;

namespace World;

/// <summary>
/// The camera maths behind photo mode (ported from the web version's photoMode.js), kept
/// separate from Godot so it is easy to read: how aperture, shutter and ISO expose a scene,
/// and how focal length, aperture and focus distance set the field of view and depth of field.
/// Lengths in metres unless noted.
/// </summary>
public static class CameraModel
{
    public const double SensorHeight = 0.024;      // full frame (36 x 24 mm)
    public const double CircleOfConfusion = 0.00003; // 0.03 mm: the usual full-frame "sharp enough" blur
    // Settings that expose a midday scene neutrally (the "sunny 16"-style reference).
    const double RefAperture = 8, RefShutter = 1.0 / 125, RefIso = 100;
    const double AutoShutterMin = 1.0 / 4000, AutoShutterMax = 1;

    public static readonly double[] Apertures = { 1.4, 2, 2.8, 4, 5.6, 8, 11, 16, 22 };
    // null = Auto (the camera meters and picks the shutter time).
    public static readonly double?[] Shutters =
        { null, 1.0 / 4000, 1.0 / 2000, 1.0 / 1000, 1.0 / 500, 1.0 / 250, 1.0 / 125, 1.0 / 60, 1.0 / 30, 1.0 / 15, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 4, 8, 15, 30 };
    public static readonly int[] Isos = { 100, 200, 400, 800, 1600, 3200, 6400 };
    // null = autofocus on whatever is under the centre mark.
    public static readonly double?[] FocusDistances = { null, 0.5, 1, 2, 3, 5, 8, 12, 20, 35, 60, 100, 200 };
    public const double MinFocalMm = 14, MaxFocalMm = 300;

    /// <summary>Vertical field of view (degrees) of a lens on a full-frame sensor.</summary>
    public static double FovForFocalLength(double focalMm) =>
        2 * Math.Atan(SensorHeight * 1000 / 2 / focalMm) * 180 / Math.PI;

    /// <summary>
    /// Available light relative to midday (1), from the sun's height (y of its unit direction):
    /// full daylight, three stops down through golden hour, eight more through twilight, then moonlight.
    /// </summary>
    public static double SceneBrightness(double sunY)
    {
        double stops = 0;
        if (sunY < -0.2) stops = -11;
        else if (sunY < 0) stops = -3 - 8 * (-sunY / 0.2);
        else if (sunY < 0.3) stops = -3 * (1 - sunY / 0.3);
        return Math.Pow(2, stops);
    }

    /// <summary>Shutter time that exposes the scene neutrally; `ev` shifts it by exposure compensation.</summary>
    public static double MeteredShutter(double aperture, int iso, double brightness, double ev = 0) =>
        RefShutter * aperture * aperture / (RefAperture * RefAperture) * (RefIso / iso) / Math.Max(brightness, 1e-6) * Math.Pow(2, ev);

    /// <summary>
    /// The shutter time in use: the manual one, or on Auto the metered time limited to what the
    /// camera will pick by itself (1/4000 s to 1 s). In dim light that limit is reached and the
    /// photo comes out dark unless you open the aperture, raise ISO or choose a longer shutter.
    /// </summary>
    public static double ShutterInUse(double aperture, double? shutter, int iso, double ev, double brightness) =>
        shutter ?? Math.Clamp(MeteredShutter(aperture, iso, brightness, ev), AutoShutterMin, AutoShutterMax);

    /// <summary>
    /// Brightness multiplier of the photo: 1 = correctly exposed, 2 = one stop over, 0.5 = one under.
    /// </summary>
    public static double Exposure(double aperture, double? shutter, int iso, double ev, double brightness)
    {
        double needed = MeteredShutter(aperture, iso, brightness);
        return Math.Clamp(ShutterInUse(aperture, shutter, iso, ev, brightness) / needed, 0.004, 16);
    }

    /// <summary>
    /// Depth of field: the nearest and farthest distances that look sharp (thin-lens model with
    /// the hyperfocal distance). Far is infinity when focused at or beyond the hyperfocal distance.
    /// </summary>
    public static (double near, double far) SharpRange(double focalMm, double aperture, double focus)
    {
        double f = focalMm / 1000;
        double h = f * f / (aperture * CircleOfConfusion) + f; // hyperfocal distance
        double near = focus * (h - f) / (h + focus - 2 * f);
        double far = focus < h ? focus * (h - f) / (h - focus) : double.PositiveInfinity;
        return (near, far);
    }

    /// <summary>Blur (pixels) of things far behind the focus, for an image this many pixels tall.</summary>
    public static double BackgroundBlurPixels(double focalMm, double aperture, double focus, double imageHeight)
    {
        double f = focalMm / 1000;
        double circle = f * f / (aperture * Math.Max(focus - f, 0.01));
        return circle / SensorHeight * imageHeight / 2;
    }

    public static string FormatShutter(double seconds)
    {
        if (seconds >= 0.75) return Math.Round(seconds, seconds >= 10 ? 0 : 1).ToString(UiText.Culture) + '"';
        double denominator = 1 / seconds;
        foreach (var t in Shutters)
            if (t is double s && Math.Abs(1 / s - denominator) < denominator * 0.03) return $"1/{Math.Round(1 / s)}";
        double digits = Math.Pow(10, Math.Max(0, Math.Floor(Math.Log10(denominator)) - 1));
        return $"1/{Math.Round(denominator / digits) * digits}";
    }
}
