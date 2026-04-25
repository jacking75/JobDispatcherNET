using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AdvancedMmorpgClient;

/// <summary>
/// 부감 시점 렌더러. 월드 좌표 → 화면 좌표 매핑.
/// 모든 엔티티가 한 화면에 다 보이지만 너무 좁게 모이지 않도록
/// 월드 비율에 맞춰 자동 스케일.
/// </summary>
public sealed class Renderer
{
    private readonly Texture2D _pixel;
    private readonly Texture2D _circle;
    private readonly int _circleRadius;
    private readonly WorldState _world;
    private readonly int _screenW, _screenH;
    private readonly int _padding;

    private static readonly Color[] KindColor =
    {
        new(120, 180, 255),  // Player
        new(124, 252,   0),  // Slime
        new(139,  69,  19),  // Goblin
        new(160, 160, 160),  // Wolf
        new(245, 245, 220),  // Skeleton
        new(255,  20, 147),  // Boss
    };

    public Renderer(GraphicsDevice gd, WorldState world, int screenW, int screenH, int padding = 40)
    {
        _world = world;
        _screenW = screenW;
        _screenH = screenH;
        _padding = padding;

        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _circleRadius = 14;
        _circle = CreateCircleTexture(gd, _circleRadius);
    }

    private static Texture2D CreateCircleTexture(GraphicsDevice gd, int r)
    {
        int size = r * 2 + 2;
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        float cx = r + 0.5f, cy = r + 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - cx, dy = y - cy;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float edge = r;
            if (d <= edge - 1) data[y * size + x] = Color.White;
            else if (d <= edge) data[y * size + x] = Color.White * (edge - d);
            else data[y * size + x] = Color.Transparent;
        }
        tex.SetData(data);
        return tex;
    }

    public void Draw(SpriteBatch sb, GameTime gameTime)
    {
        // 배경 그리드
        DrawGrid(sb);

        // 월드 → 화면 매핑
        float ww = MathF.Max(1, _world.WorldWidth);
        float wh = MathF.Max(1, _world.WorldHeight);
        float availW = _screenW - _padding * 2;
        float availH = _screenH - _padding * 2;
        float scale = MathF.Min(availW / ww, availH / wh);
        float offsetX = (_screenW - ww * scale) * 0.5f;
        float offsetY = (_screenH - wh * scale) * 0.5f;

        // 월드 경계
        DrawRectOutline(sb,
            new Rectangle((int)offsetX, (int)offsetY, (int)(ww * scale), (int)(wh * scale)),
            Color.DimGray, 2);

        var entities = _world.Entities.Values.ToArray();

        // 본체
        foreach (var e in entities)
        {
            float sx = offsetX + e.X * scale;
            float sy = offsetY + e.Y * scale;
            DrawEntity(sb, e, sx, sy);
        }

        // HUD
        DrawHud(sb, entities.Length);
    }

    private void DrawGrid(SpriteBatch sb)
    {
        var bg = new Color(20, 24, 32);
        sb.Draw(_pixel, new Rectangle(0, 0, _screenW, _screenH), bg);

        var grid = new Color(40, 46, 56);
        const int step = 80;
        for (int x = 0; x < _screenW; x += step)
            sb.Draw(_pixel, new Rectangle(x, 0, 1, _screenH), grid);
        for (int y = 0; y < _screenH; y += step)
            sb.Draw(_pixel, new Rectangle(0, y, _screenW, 1), grid);
    }

    private void DrawEntity(SpriteBatch sb, EntityView e, float sx, float sy)
    {
        var color = ResolveColor(e);
        if (!e.IsAlive) color = new Color(60, 60, 60);

        // 내 봇이면 노란 외곽
        bool mine = _world.IsMyBot(e.Id);

        // 원
        var dest = new Rectangle(
            (int)(sx - _circleRadius),
            (int)(sy - _circleRadius),
            _circleRadius * 2,
            _circleRadius * 2);
        sb.Draw(_circle, dest, color);

        if (mine)
            DrawCircleOutline(sb, sx, sy, _circleRadius + 3, Color.Yellow);

        // HP 바
        if (e.MaxHp > 0 && e.IsAlive)
        {
            int barW = _circleRadius * 2 + 4;
            int barH = 3;
            int bx = (int)(sx - barW / 2f);
            int by = (int)(sy - _circleRadius - 8);
            sb.Draw(_pixel, new Rectangle(bx, by, barW, barH), Color.Black);
            float hpFrac = Math.Clamp(e.Hp / (float)e.MaxHp, 0, 1);
            sb.Draw(_pixel, new Rectangle(bx, by, (int)(barW * hpFrac), barH),
                hpFrac > 0.5f ? Color.LimeGreen : (hpFrac > 0.2f ? Color.Yellow : Color.Red));
        }

        // 이름
        var label = e.Name;
        DrawText(sb, label, sx - PixelFont.MeasureWidth(label, 1) / 2f, sy + _circleRadius + 2, 1, Color.White);
    }

    private Color ResolveColor(EntityView e)
    {
        // 서버 SPAWN 패킷에 색상이 실려 오면 그것을 우선, 없으면 종류 기반 fallback.
        if (TryParseHexColor(e.Color, out var c)) return c;
        int idx = (int)e.Kind;
        if (idx >= 0 && idx < KindColor.Length) return KindColor[idx];
        return Color.White;
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

    private void DrawHud(SpriteBatch sb, int entityCount)
    {
        DrawText(sb, $"Entities:{entityCount}  World:{(int)_world.WorldWidth}x{(int)_world.WorldHeight}",
            10, 10, 2, Color.LightGray);
        DrawText(sb, "AdvancedMmorpgClient (dummy bots)",
            10, _screenH - 20, 2, new Color(140, 140, 140));
    }

    private void DrawText(SpriteBatch sb, string text, float x, float y, int scale, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        int cursorX = (int)x;
        int cursorY = (int)y;
        foreach (char c in text)
        {
            var glyph = PixelFont.Lookup(c);
            if (glyph is not null)
            {
                for (int gy = 0; gy < PixelFont.CharHeight; gy++)
                {
                    byte row = glyph[gy];
                    for (int gx = 0; gx < PixelFont.CharWidth; gx++)
                    {
                        // MSB가 좌측
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
        // 간단한 다각형 근사 (32각형)
        const int seg = 32;
        for (int i = 0; i < seg; i++)
        {
            float a0 = i * MathF.Tau / seg;
            float a1 = (i + 1) * MathF.Tau / seg;
            int x0 = (int)(cx + MathF.Cos(a0) * r);
            int y0 = (int)(cy + MathF.Sin(a0) * r);
            int x1 = (int)(cx + MathF.Cos(a1) * r);
            int y1 = (int)(cy + MathF.Sin(a1) * r);
            DrawLine(sb, x0, y0, x1, y1, color);
        }
    }

    private void DrawLine(SpriteBatch sb, int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy_ = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            sb.Draw(_pixel, new Rectangle(x0, y0, 1, 1), color);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy_; }
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
