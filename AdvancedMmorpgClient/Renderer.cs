using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AdvancedMmorpgClient;

public sealed class Renderer
{
    private readonly Texture2D _pixel;
    private readonly Texture2D _circle;
    private readonly int _circleRadius;
    private readonly int _screenW;
    private readonly int _screenH;
    private readonly int _padding;

    private static readonly Color[] KindColor =
    {
        new(120, 180, 255),
        new(124, 252, 0),
        new(139, 69, 19),
        new(160, 160, 160),
        new(245, 245, 220),
        new(255, 20, 147),
    };

    public Renderer(GraphicsDevice gd, int screenW, int screenH, int padding = 40)
    {
        _screenW = screenW;
        _screenH = screenH;
        _padding = padding;

        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _circleRadius = 14;
        _circle = CreateCircleTexture(gd, _circleRadius);
    }

    public void Draw(SpriteBatch sb, GameTime gameTime, WorldState? world)
    {
        DrawGrid(sb);

        if (world is null)
        {
            DrawText(sb, "Waiting for bots", 10, 10, 2, Color.LightGray);
            return;
        }

        var ww = MathF.Max(1, world.WorldWidth);
        var wh = MathF.Max(1, world.WorldHeight);
        var availW = _screenW - _padding * 2;
        var availH = _screenH - _padding * 2;
        var scale = MathF.Min(availW / ww, availH / wh);
        var offsetX = (_screenW - ww * scale) * 0.5f;
        var offsetY = (_screenH - wh * scale) * 0.5f;

        DrawRectOutline(sb,
            new Rectangle((int)offsetX, (int)offsetY, (int)(ww * scale), (int)(wh * scale)),
            Color.DimGray, 2);

        var entities = world.Entities.Values.ToArray();
        foreach (var e in entities)
        {
            var sx = offsetX + e.X * scale;
            var sy = offsetY + e.Y * scale;
            DrawEntity(sb, world, e, sx, sy);
        }

        DrawHud(sb, world, entities.Length);
    }

    private static Texture2D CreateCircleTexture(GraphicsDevice gd, int r)
    {
        var size = r * 2 + 2;
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        var cx = r + 0.5f;
        var cy = r + 0.5f;

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= r - 1) data[y * size + x] = Color.White;
            else if (d <= r) data[y * size + x] = Color.White * (r - d);
            else data[y * size + x] = Color.Transparent;
        }

        tex.SetData(data);
        return tex;
    }

    private void DrawGrid(SpriteBatch sb)
    {
        sb.Draw(_pixel, new Rectangle(0, 0, _screenW, _screenH), new Color(20, 24, 32));

        var grid = new Color(40, 46, 56);
        const int step = 80;
        for (var x = 0; x < _screenW; x += step)
            sb.Draw(_pixel, new Rectangle(x, 0, 1, _screenH), grid);
        for (var y = 0; y < _screenH; y += step)
            sb.Draw(_pixel, new Rectangle(0, y, _screenW, 1), grid);
    }

    private void DrawEntity(SpriteBatch sb, WorldState world, EntityView e, float sx, float sy)
    {
        var color = ResolveColor(e);
        if (!e.IsAlive) color = new Color(60, 60, 60);

        var dest = new Rectangle(
            (int)(sx - _circleRadius),
            (int)(sy - _circleRadius),
            _circleRadius * 2,
            _circleRadius * 2);
        sb.Draw(_circle, dest, color);

        if (world.IsMyBot(e.Id))
            DrawCircleOutline(sb, sx, sy, _circleRadius + 3, Color.Yellow);

        if (e.MaxHp > 0 && e.IsAlive)
        {
            var barW = _circleRadius * 2 + 4;
            const int barH = 3;
            var bx = (int)(sx - barW / 2f);
            var by = (int)(sy - _circleRadius - 8);
            sb.Draw(_pixel, new Rectangle(bx, by, barW, barH), Color.Black);
            var hpFrac = Math.Clamp(e.Hp / (float)e.MaxHp, 0, 1);
            sb.Draw(_pixel, new Rectangle(bx, by, (int)(barW * hpFrac), barH),
                hpFrac > 0.5f ? Color.LimeGreen : hpFrac > 0.2f ? Color.Yellow : Color.Red);
        }

        DrawText(sb, e.Name, sx - PixelFont.MeasureWidth(e.Name, 1) / 2f,
            sy + _circleRadius + 2, 1, Color.White);
    }

    private static Color ResolveColor(EntityView e)
    {
        if (TryParseHexColor(e.Color, out var c)) return c;
        var idx = (int)e.Kind;
        return idx >= 0 && idx < KindColor.Length ? KindColor[idx] : Color.White;
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        color = Color.White;
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length != 7) return false;
        var span = hex.AsSpan();
        if (byte.TryParse(span.Slice(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(span.Slice(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(span.Slice(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            color = new Color(r, g, b);
            return true;
        }

        return false;
    }

    private void DrawHud(SpriteBatch sb, WorldState world, int entityCount)
    {
        DrawText(sb, $"Entities:{entityCount}  World:{(int)world.WorldWidth}x{(int)world.WorldHeight}",
            10, 10, 2, Color.LightGray);
        DrawText(sb, "AdvancedMmorpgClient (dummy bots)",
            10, _screenH - 20, 2, new Color(140, 140, 140));
    }

    private void DrawText(SpriteBatch sb, string text, float x, float y, int scale, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cursorX = (int)x;
        var cursorY = (int)y;

        foreach (var c in text)
        {
            var glyph = PixelFont.Lookup(c);
            if (glyph is not null)
            {
                for (var gy = 0; gy < PixelFont.CharHeight; gy++)
                {
                    var row = glyph[gy];
                    for (var gx = 0; gx < PixelFont.CharWidth; gx++)
                    {
                        if ((row & (1 << (PixelFont.CharWidth - 1 - gx))) != 0)
                        {
                            sb.Draw(_pixel,
                                new Rectangle(cursorX + gx * scale, cursorY + gy * scale, scale, scale),
                                color);
                        }
                    }
                }
            }

            cursorX += (PixelFont.CharWidth + PixelFont.CharSpacing) * scale;
        }
    }

    private void DrawCircleOutline(SpriteBatch sb, float cx, float cy, int r, Color color)
    {
        const int seg = 32;
        for (var i = 0; i < seg; i++)
        {
            var a0 = i * MathF.Tau / seg;
            var a1 = (i + 1) * MathF.Tau / seg;
            var x0 = (int)(cx + MathF.Cos(a0) * r);
            var y0 = (int)(cy + MathF.Sin(a0) * r);
            var x1 = (int)(cx + MathF.Cos(a1) * r);
            var y1 = (int)(cy + MathF.Sin(a1) * r);
            DrawLine(sb, x0, y0, x1, y1, color);
        }
    }

    private void DrawLine(SpriteBatch sb, int x0, int y0, int x1, int y1, Color color)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;

        while (true)
        {
            sb.Draw(_pixel, new Rectangle(x0, y0, 1, 1), color);
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private void DrawRectOutline(SpriteBatch sb, Rectangle r, Color color, int thickness)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, thickness), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, thickness, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), color);
    }
}
