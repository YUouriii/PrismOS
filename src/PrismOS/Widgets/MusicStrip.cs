using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using PrismOS.Widgets.Themes;
using PrismOS.Common;

namespace PrismOS.Widgets;

/// <summary>The now-playing pill: album art, small-caps source app, title, artist and a live
/// meter, on a rounded dark card. One renderer for every style, so Classic and the group themes
/// share the face instead of each growing their own. There are no transport controls: the desktop
/// surface is click-through by construction and an invisible click window over it never received
/// clicks reliably, so a row of buttons that looked like they worked was worse than none.</summary>
public static class MusicStrip
{
    // Base proportions in pixels at scale 1.
    private const float Height = 64f, ArtSidePx = 48f, PadPx = 8f, ArtGapPx = 10f, LineGapPx = 2f;
    private const float SourcePx = 8.5f, TitlePx = 12.5f, ArtistPx = 9.5f;
    private const float RightPadPx = 12f;
    private const float MaxTextWidthPx = 190f;

    // The equalizer column. It used to be 16 px for twelve bands, which left the bars with a
    // negative width — so the live meter drew nothing at all. At 30 px the meter can fit eight
    // merged bands with real gaps (see BarGeometry).
    private const float EqWidthPx = 30f, EqGapPx = 10f, EqHeightPx = 30f;

    /// <summary>Everything a painter needs, in panel pixels. All boxes are relative to the
    /// panel's top-left.</summary>
    public readonly record struct Metrics(
        Size Size, int Pad, int ArtSide, int ArtGap,
        int SourceTop, int TitleTop, int ArtistTop,
        string SourceText, string TitleText, string? ArtistText,
        Rectangle ArtBox, Rectangle EqBox);

    /// <summary>Time in seconds for the animated meter. The paint rate is 30 fps; this is the
    /// <em>speed</em> of the motion, deliberately decoupled from it — tying the phase to the frame
    /// count made the bars race on a fast display and crawl on a slow one.</summary>
    public static float BarPhase() => (float)(Environment.TickCount64 / 1000.0);

    /// <summary><paramref name="maxWidth"/> caps the whole pill — the text column shrinks
    /// first, so position never spills off the work area at large scales.</summary>
    public static Metrics Measure(Graphics g, NowPlayingReading r, string? source, float scale, float maxWidth = float.MaxValue)
    {
        float height = Height * scale, art = ArtSidePx * scale, pad = PadPx * scale;
        float secX = pad + art + ArtGapPx * scale;

        // Everything the pill spends on things other than the text: art, gaps and the equalizer
        // column. The leftover goes to text, clamped so the pill is never wider than the cap.
        float eqWidth = EqWidthPx * scale, eqGap = EqGapPx * scale;
        float overhead = secX + eqGap + eqWidth + RightPadPx * scale;
        float textWidth = Math.Min(MaxTextWidthPx * scale, Math.Max(maxWidth - overhead, 8f * scale));

        using var sourceFont = ThemeFonts.Create(ThemeFonts.TextSemibold, Math.Max(SourcePx * scale, 7f));
        using var titleFont = ThemeFonts.Create(ThemeFonts.TextSemibold, TitlePx * scale);
        using var artistFont = ThemeFonts.Create(ThemeFonts.TextSemibold, Math.Max(ArtistPx * scale, 7f));

        float sourcePx = sourceFont.Size, titlePx = titleFont.Size, artistPx = artistFont.Size;
        string sourceText = Fit(g, (string.IsNullOrWhiteSpace(source) ? "NOW PLAYING" : source).ToUpper(CultureInfo.InvariantCulture),
            sourceFont, textWidth, sourcePx * 0.15f);
        string titleText = Fit(g, r.Title ?? "", titleFont, textWidth, 0f);
        string? artistText = string.IsNullOrWhiteSpace(r.Artist)
            ? null
            : Fit(g, r.Artist!, artistFont, textWidth, artistPx * 0.15f);

        var sourceSize = ThemeFonts.MeasureTracked(g, sourceText, sourceFont, sourcePx * 0.15f);
        var titleSize = ThemeFonts.MeasureTracked(g, titleText, titleFont, 0f);
        var artistSize = artistText is null ? SizeF.Empty : ThemeFonts.MeasureTracked(g, artistText, artistFont, artistPx * 0.15f);

        float textHeight = sourceSize.Height + LineGapPx * scale + titleSize.Height + LineGapPx * scale + artistSize.Height;
        float srcY = (height - textHeight) / 2f + 0.5f * scale;
        float titleY = srcY + sourceSize.Height + LineGapPx * scale;
        float artistY = titleY + titleSize.Height + LineGapPx * scale;

        float eqX = secX + textWidth + eqGap;
        float total = eqX + eqWidth + RightPadPx * scale;

        float eqH = Math.Min(EqHeightPx * scale, height * 0.56f);
        return new Metrics(
            new Size((int)Math.Ceiling(total), (int)Math.Ceiling(height)),
            (int)Math.Ceiling(pad), (int)Math.Ceiling(art), (int)Math.Ceiling(ArtGapPx * scale),
            (int)srcY, (int)titleY, (int)artistY,
            sourceText, titleText, artistText,
            new Rectangle((int)pad, (int)((height - art) / 2f), (int)art, (int)art),
            new Rectangle((int)eqX, (int)((height - eqH) / 2f), (int)eqWidth, (int)eqH));
    }

    /// <summary><paramref name="levels"/> carries the live 12-band meter (see LoopbackMeter).
    /// Null keeps the pre-audio behaviour (deterministic sine) so previews and tests still render.
    /// <paramref name="accent"/> tints the progress line and the no-artwork tile; the default
    /// (an empty colour) falls back to neutral white for callers that have no accent.
    /// <paramref name="showProgress"/> hides the progress line entirely — the Classic record maps
    /// this to its ring's visibility.</summary>
    public static void Paint(Graphics g, in Metrics m, NowPlayingReading r, Bitmap? art, float scale, float eqPhase,
                             int cardAlpha = 216, float[]? levels = null, Color accent = default,
                             bool showProgress = true)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var card = new RectangleF(0, 0, m.Size.Width, m.Size.Height);
        float radius = Height * scale * 0.30f;
        using (var path = RoundedRect(card, radius))
        {
            // A surface rather than a flat swatch: the room is a little lighter at the top.
            using (var brush = new LinearGradientBrush(card,
                       Color.FromArgb(Math.Clamp(cardAlpha, 0, 255), 34, 38, 49),
                       Color.FromArgb(Math.Clamp(cardAlpha, 0, 255), 18, 21, 28), 90f))
                g.FillPath(brush, path);
            using var border = new Pen(Color.FromArgb(30, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        // Artwork
        using var artPath = RoundedRect(m.ArtBox, ArtSidePx * scale * 0.18f);
        if (art is not null)
        {
            g.SetClip(artPath, CombineMode.Replace);
            double s = Math.Max((double)m.ArtBox.Width / art.Width, (double)m.ArtBox.Height / art.Height);
            int w = Math.Max(1, (int)Math.Round(art.Width * s)), h = Math.Max(1, (int)Math.Round(art.Height * s));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(art, m.ArtBox.X + (m.ArtBox.Width - w) / 2, m.ArtBox.Y + (m.ArtBox.Height - h) / 2, w, h);
            g.ResetClip();
        }
        else
        {
            // No thumbnail from the media session: an accent-tinted tile with the note glyph,
            // instead of a black box with a play icon.
            PaintArtPlaceholder(g, m.ArtBox, artPath, accent);
        }
        using (var artEdge = new Pen(Color.FromArgb(34, 255, 255, 255), 1f))
            g.DrawPath(artEdge, artPath);

        // Text column
        float x = m.Pad + m.ArtSide + m.ArtGap;
        using var sourceFont = ThemeFonts.Create(ThemeFonts.TextSemibold, Math.Max(SourcePx * scale, 7f));
        using var titleFont = ThemeFonts.Create(ThemeFonts.TextSemibold, TitlePx * scale);
        using var artistFont = ThemeFonts.Create(ThemeFonts.TextSemibold, Math.Max(ArtistPx * scale, 7f));
        using var dim = new SolidBrush(Color.FromArgb(174, 255, 255, 255));
        using var subtle = new SolidBrush(Color.FromArgb(142, 255, 255, 255));
        using var ink = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        ThemeFonts.DrawTracked(g, m.SourceText, sourceFont, subtle, x, m.SourceTop, Math.Max(SourcePx * scale, 7f) * 0.15f);
        ThemeFonts.DrawTracked(g, m.TitleText, titleFont, ink, x, m.TitleTop, 0f);
        if (m.ArtistText is not null)
            ThemeFonts.DrawTracked(g, m.ArtistText, artistFont, dim, x, m.ArtistTop, Math.Max(ArtistPx * scale, 7f) * 0.15f);

        // Equalizer column: real WASAPI-loopback spectrum when the engine supplies it,
        // a playful sine otherwise (previews and tests / no audio device).
        if (levels is not null) PaintEqualizer(g, m.EqBox, levels, r.IsPlaying);
        else PaintEqualizerSine(g, m.EqBox, eqPhase, r.IsPlaying);

        if (showProgress) PaintProgress(g, m, r, scale, accent);
    }

    private static string Fit(Graphics g, string text, Font font, float maxWidth, float tracking)
    {
        text = ThemeFonts.Plain(text);
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (ThemeFonts.MeasureTracked(g, text, font, tracking).Width <= maxWidth) return text;
        for (int len = text.Length - 1; len >= 1; len--)
        {
            // Never between the halves of a surrogate pair: half an emoji draws as a box.
            if (char.IsHighSurrogate(text[len - 1])) continue;
            string candidate = text[..len].TrimEnd() + "…";
            if (ThemeFonts.MeasureTracked(g, candidate, font, tracking).Width <= maxWidth) return candidate;
        }
        return "…";
    }

    // ---- artwork placeholder ---------------------------------------------------------------------

    private static void PaintArtPlaceholder(Graphics g, RectangleF box, GraphicsPath clip, Color accent)
    {
        bool tinted = accent.A != 0;
        Color top = tinted ? Mix(accent, Color.FromArgb(255, 30, 34, 44), 0.62f) : Color.FromArgb(255, 58, 44, 84);
        Color bottom = tinted ? Mix(accent, Color.FromArgb(255, 14, 16, 22), 0.82f) : Color.FromArgb(255, 16, 18, 26);
        using (var brush = new LinearGradientBrush(box, top, bottom, 135f))
            g.FillPath(brush, clip);

        // An eighth note, drawn rather than typed: no icon font to go missing.
        var state = g.Save();
        g.SetClip(clip, CombineMode.Replace);
        float s = Math.Min(box.Width, box.Height);
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        using (var ink = new SolidBrush(Color.FromArgb(210, 255, 255, 255)))
        {
            float headW = s * 0.32f, headH = s * 0.23f;
            float headX = cx - s * 0.22f, headY = cy + s * 0.10f;
            // A note head is an egg set at an angle; an upright ellipse read as a stray blob.
            var head = g.Save();
            g.TranslateTransform(headX + headW / 2f, headY + headH / 2f);
            g.RotateTransform(-18f);
            g.FillEllipse(ink, -headW / 2f, -headH / 2f, headW, headH);
            g.Restore(head);

            float stemX = headX + headW * 0.78f, stemW = Math.Max(s * 0.055f, 1.5f);
            float stemTop = cy - s * 0.30f;
            g.FillRectangle(ink, stemX, stemTop, stemW, headY + headH * 0.68f - stemTop);

            using var flag = new GraphicsPath();
            flag.AddBezier(stemX + stemW, stemTop,
                           stemX + s * 0.34f, stemTop + s * 0.03f,
                           stemX + s * 0.32f, stemTop + s * 0.20f,
                           stemX + s * 0.10f, stemTop + s * 0.30f);
            flag.AddBezier(stemX + s * 0.10f, stemTop + s * 0.30f,
                           stemX + s * 0.24f, stemTop + s * 0.16f,
                           stemX + stemW, stemTop + s * 0.10f,
                           stemX + stemW, stemTop);
            flag.CloseFigure();
            g.FillPath(ink, flag);
        }
        g.Restore(state);
    }

    // ---- the spectrum ----------------------------------------------------------------------------

    /// <summary>The column is only 30 px wide at scale 1, so twelve bands would be hairlines with
    /// wider gaps than bars. Fold neighbouring bands together until each bar is a few pixels
    /// across: a merged meter still moves with the music and reads as a shape.</summary>
    private static (int Count, float Gap, float BarW) BarGeometry(Rectangle box, int bandCount)
    {
        float gap = Math.Max(1f, box.Height * 0.045f);
        float minBar = Math.Max(2.2f, box.Height * 0.08f);
        int count = (int)MathF.Floor((box.Width + gap) / (minBar + gap));
        count = Math.Clamp(count, Math.Min(3, Math.Max(bandCount, 1)), Math.Max(bandCount, 1));
        float step = (box.Width + gap) / count;
        return (count, gap, MathF.Max(1f, step - gap));
    }

    /// <summary>The real spectrum: bars rise with their bands, merged when the column is narrow.
    /// The peak of a merged group keeps the kick visible instead of averaging the shape away.</summary>
    private static void PaintEqualizer(Graphics g, Rectangle box, float[] bands, bool playing)
    {
        if (bands.Length == 0 || box.Width <= 0 || box.Height <= 0) return;
        var (bars, gap, barW) = BarGeometry(box, bands.Length);
        using var brush = new SolidBrush(Color.White);
        using var path = new GraphicsPath();
        for (int i = 0; i < bars; i++)
        {
            float mag = playing ? BandPeak(bands, i, bars) : 0f;
            float h = Math.Max(1.5f, box.Height * (0.08f + 0.92f * mag));
            brush.Color = Color.FromArgb((int)(135 + 120 * mag), 255, 255, 255);
            FillBar(g, path, brush, box.X + i * (barW + gap), box.Y + box.Height - h, barW, h);
        }
    }

    private static float BandPeak(float[] bands, int bar, int bars)
    {
        int start = bar * bands.Length / bars;
        int end = Math.Max(start + 1, (bar + 1) * bands.Length / bars);
        float peak = 0f;
        for (int b = start; b < end && b < bands.Length; b++)
            peak = Math.Max(peak, bands[b]);
        return Math.Clamp(peak, 0f, 1f);
    }

    private static void PaintEqualizerSine(Graphics g, Rectangle box, float phase, bool playing)
    {
        // Base-level (not audio-reactive) bars: two slow, incommensurate sines with a per-bar
        // offset read as a gentle wave, not the frantic flicker that came from tying the phase to
        // the 30 fps timer. The paint rate stays 30 fps; only the motion is slow.
        if (box.Width <= 0 || box.Height <= 0) return;
        var (bars, gap, barW) = BarGeometry(box, Spectrum.BandCount);
        float amp = playing ? 1f : 0.32f;
        using var brush = new SolidBrush(Color.White);
        using var path = new GraphicsPath();
        for (int i = 0; i < bars; i++)
        {
            float band = amp * (0.60f
                + 0.26f * MathF.Sin(phase * 3.6f + i * 0.62f)
                + 0.16f * MathF.Sin(phase * 2.1f + i * 1.37f));
            float mag = Math.Clamp(band, 0.08f, 1f);
            float h = box.Height * Math.Max(mag, 0.08f);
            brush.Color = Color.FromArgb((int)(135 + 120 * Math.Min(1f, mag)), 255, 255, 255);
            FillBar(g, path, brush, box.X + i * (barW + gap), box.Y + box.Height - h, barW, h);
        }
    }

    /// <summary>One bar: a rounded cap and a square foot on the meter's baseline.</summary>
    private static void FillBar(Graphics g, GraphicsPath path, SolidBrush brush, float x, float top, float w, float h)
    {
        if (w <= 0f || h <= 0f) return;
        float r = Math.Min(w / 2f, h);
        if (r < 0.8f)
        {
            g.FillRectangle(brush, x, top, w, h);
            return;
        }
        float d = r * 2f;
        path.Reset();
        path.AddArc(x, top, d, d, 180, 90);
        path.AddArc(x + w - d, top, d, d, 270, 90);
        path.AddLine(x + w, top + r, x + w, top + h);
        path.AddLine(x + w, top + h, x, top + h);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    // ---- progress --------------------------------------------------------------------------------

    /// <summary>A hairline of listened-through time along the bottom edge, when the session reports
    /// a duration. Live streams report none, and then there is no line rather than a lying one.</summary>
    private static void PaintProgress(Graphics g, in Metrics m, NowPlayingReading r, float scale, Color accent)
    {
        if (r.Progress <= 0.001d || m.Size.Width <= 0) return;

        float lineH = Math.Max(2f, 2.4f * scale);
        float inset = Height * scale * 0.30f + 2f * scale;
        float width = m.Size.Width - inset * 2f;
        if (width <= lineH) return;
        float y = m.Size.Height - lineH - Math.Max(1.5f * scale, 2f);

        using (var track = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
            g.FillPath(track, RoundedRect(new RectangleF(inset, y, width, lineH), lineH / 2f));

        Color fillColour = accent.A == 0
            ? Color.FromArgb(170, 255, 255, 255)
            : Color.FromArgb(200, accent);
        float filled = Math.Max(lineH, width * (float)Math.Clamp(r.Progress, 0d, 1d));
        using var brush = new SolidBrush(fillColour);
        g.FillPath(brush, RoundedRect(new RectangleF(inset, y, filled, lineH), lineH / 2f));
    }

    private static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(255,
            (int)Math.Round(a.R * (1 - t) + b.R * t),
            (int)Math.Round(a.G * (1 - t) + b.G * t),
            (int)Math.Round(a.B * (1 - t) + b.B * t));
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0f, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        if (d <= 0f) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedRect(Rectangle rect, float radius) =>
        RoundedRect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius);
}
