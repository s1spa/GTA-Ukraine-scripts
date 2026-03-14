using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using ScriptedWpf.Core;
using System.Windows.Forms;

namespace ScriptedWpf.Cogs.Wires;

// ── Колір проводу ─────────────────────────────────────────────────────────────
enum WireColor
{
    Unknown,
    Red, Blue, Yellow, Black, Green,
    Purple, DarkBlue, DarkGreen, Teal, Olive
}

// ── Визначення кольору пікселя ────────────────────────────────────────────────
static class ColorMatcher
{
    public static WireColor MatchFromSamples(Color c, Dictionary<WireColor, List<PixelSample>> samples, int threshold = 75)
    {
        int bestDist = threshold;
        WireColor best = WireColor.Unknown;
        foreach (var (wc, pixList) in samples)
        {
            foreach (var px in pixList)
            {
                int dist = Dist(c.R, c.G, c.B, px.R, px.G, px.B);
                if (dist < bestDist) { bestDist = dist; best = wc; }
            }
        }
        return best;
    }

    static int Dist(int r1, int g1, int b1, int r2, int g2, int b2)
        => (r1 - r2) * (r1 - r2) + (g1 - g2) * (g1 - g2) + (b1 - b2) * (b1 - b2);
}

// ── Сканер екрану ─────────────────────────────────────────────────────────────
static class WireScanner
{
    // ── Допоміжні методи для побудови словників зразків ───────────────────────

    static Dictionary<WireColor, List<PixelSample>> BuildWireSamples(WiresConfig cfg)
    {
        var result = new Dictionary<WireColor, List<PixelSample>>();
        foreach (var (name, cs) in cfg.ColorSamples)
        {
            if (!Enum.TryParse<WireColor>(name, out var wc) || wc == WireColor.Unknown) continue;
            if (cs.Wire.Count == 0) continue;
            result[wc] = cs.Wire;
        }
        return result;
    }

    static Dictionary<WireColor, List<PixelSample>> BuildRingSamples(WiresConfig cfg)
    {
        var result = new Dictionary<WireColor, List<PixelSample>>();
        foreach (var (name, cs) in cfg.ColorSamples)
        {
            if (!Enum.TryParse<WireColor>(name, out var wc) || wc == WireColor.Unknown) continue;
            if (cs.Ring.Count == 0) continue;
            result[wc] = cs.Ring;
        }
        return result;
    }

    // ── Проводи ───────────────────────────────────────────────────────────────

    public static List<(Point pos, WireColor color)> FindTopWireTips(Bitmap bmp, Rectangle screen, WiresConfig cfg)
    {
        var wireSamples = BuildWireSamples(cfg);
        // Рахуємо середній Y з Wire-зразків верхніх кольорів (TopRingColors)
        var topWireYs = cfg.ColorSamples
            .Where(kv => TopRingColors.Contains(Enum.TryParse<WireColor>(kv.Key, out var wc) ? wc : WireColor.Unknown))
            .SelectMany(kv => kv.Value.Wire).Select(s => s.YPct).ToList();
        double topY = topWireYs.Count > 0 ? topWireYs.Average() : cfg.TopWireY;
        return ScanWireHLine(bmp, (int)(bmp.Height * topY),
            (int)(bmp.Width * cfg.ScanX1), (int)(bmp.Width * cfg.ScanX2),
            wireSamples, missingColor: WireColor.Black);
    }

    public static List<(Point pos, WireColor color)> FindBottomWireTips(Bitmap bmp, Rectangle screen, WiresConfig cfg)
    {
        var wireSamples = BuildWireSamples(cfg);
        // Рахуємо середній Y з Wire-зразків нижніх кольорів (BotRingColors)
        var botWireYs = cfg.ColorSamples
            .Where(kv => BotRingColors.Contains(Enum.TryParse<WireColor>(kv.Key, out var wc) ? wc : WireColor.Unknown))
            .SelectMany(kv => kv.Value.Wire).Select(s => s.YPct).ToList();
        double botY = botWireYs.Count > 0 ? botWireYs.Average() : cfg.BotWireY;
        return ScanWireHLine(bmp, (int)(bmp.Height * botY),
            (int)(bmp.Width * cfg.ScanX1), (int)(bmp.Width * cfg.ScanX2),
            wireSamples, missingColor: WireColor.DarkBlue);
    }

    // Скануємо ±halfH рядків навколо Y, накопичуємо голоси по X-кластерах
    static List<(Point pos, WireColor color)> ScanWireHLine(
        Bitmap bmp, int y, int x1, int x2,
        Dictionary<WireColor, List<PixelSample>> wireSamples,
        WireColor missingColor)
    {
        // Якщо зразків немає — повертаємо порожній список
        if (wireSamples.Count == 0)
            return new List<(Point, WireColor)>();

        y  = Math.Clamp(y,  0, bmp.Height - 1);
        x1 = Math.Clamp(x1, 0, bmp.Width  - 1);
        x2 = Math.Clamp(x2, 0, bmp.Width  - 1);

        const int halfH = 20;
        int yMin = Math.Max(0, y - halfH);
        int yMax = Math.Min(bmp.Height - 1, y + halfH);

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var votes = new int[x2 - x1 + 1];
        var colorVotesPerX = new Dictionary<WireColor, int>[x2 - x1 + 1];

        unsafe
        {
            byte* s = (byte*)data.Scan0;
            for (int row = yMin; row <= yMax; row++)
            {
                byte* rowPtr = s + row * data.Stride;
                for (int x = x1; x <= x2; x++)
                {
                    int bv = rowPtr[x * 4], gv = rowPtr[x * 4 + 1], rv = rowPtr[x * 4 + 2];
                    var wc = ColorMatcher.MatchFromSamples(Color.FromArgb(rv, gv, bv), wireSamples);
                    if (wc == WireColor.Unknown) continue;

                    int i = x - x1;
                    votes[i]++;
                    colorVotesPerX[i] ??= new Dictionary<WireColor, int>();
                    colorVotesPerX[i][wc] = colorVotesPerX[i].GetValueOrDefault(wc) + 1;
                }
            }
        }

        bmp.UnlockBits(data);

        var clusters = new List<(int x, WireColor color)>();
        int clStart = -1, sumX = 0, sumCount = 0;
        var colorVotes = new Dictionary<WireColor, int>();

        for (int i = 0; i <= votes.Length; i++)
        {
            bool hit = i < votes.Length && votes[i] > 0;
            if (hit)
            {
                if (clStart < 0) { clStart = i; colorVotes.Clear(); }
                sumX += i; sumCount++;
                if (colorVotesPerX[i] != null)
                    foreach (var (wc, cnt) in colorVotesPerX[i])
                        colorVotes[wc] = colorVotes.GetValueOrDefault(wc) + cnt;
            }
            else if (clStart >= 0)
            {
                if (sumCount >= 2)
                {
                    int cx = x1 + sumX / sumCount;
                    var dominant = colorVotes.MaxBy(kv => kv.Value).Key;
                    clusters.Add((cx, dominant));
                }
                clStart = -1; sumX = 0; sumCount = 0;
            }
        }

        var deduped = new List<(int x, WireColor color)>();
        foreach (var c in clusters)
        {
            if (deduped.Count > 0 && c.x - deduped[^1].x < 25) continue;
            deduped.Add(c);
        }

        var result = deduped.Select(c => (new Point(c.x, y), c.color)).ToList();

        if (result.Count == 4)
            result = InterpolateMissing(result, y, missingColor);

        return result;
    }

    static List<(Point pos, WireColor color)> InterpolateMissing(
        List<(Point pos, WireColor color)> found, int y, WireColor missingColor)
    {
        int step = (found[3].pos.X - found[0].pos.X) / 3;
        if (step <= 0) return found;

        for (int i = 0; i < found.Count - 1; i++)
        {
            int gap = found[i + 1].pos.X - found[i].pos.X;
            if (gap > step * 14 / 10)
            {
                int newX = (found[i].pos.X + found[i + 1].pos.X) / 2;
                found.Insert(i + 1, (new Point(newX, y), missingColor));
                return found;
            }
        }

        if (found[0].pos.X - (found[0].pos.X - step) > step / 2)
            found.Insert(0, (new Point(found[0].pos.X - step, y), missingColor));
        else
            found.Add((new Point(found[3].pos.X + step, y), missingColor));

        return found;
    }

    // ── Кільця — сканування прямокутної зони ──────────────────────────────────

    // Верхній ряд завжди містить ці 5 кольорів
    static readonly HashSet<WireColor> TopRingColors = new()
        { WireColor.Red, WireColor.Blue, WireColor.Yellow, WireColor.Black, WireColor.Green };
    // Нижній ряд завжди містить ці 5 кольорів
    static readonly HashSet<WireColor> BotRingColors = new()
        { WireColor.Purple, WireColor.DarkBlue, WireColor.DarkGreen, WireColor.Teal, WireColor.Olive };

    public static (List<(Point center, WireColor color)> top, List<(Point center, WireColor color)> bottom)
        FindRings(Bitmap bmp, WiresConfig cfg)
    {
        var allSamples = BuildRingSamples(cfg);
        var topSamples = allSamples.Where(kv => TopRingColors.Contains(kv.Key))
                                   .ToDictionary(kv => kv.Key, kv => kv.Value);
        var botSamples = allSamples.Where(kv => BotRingColors.Contains(kv.Key))
                                   .ToDictionary(kv => kv.Key, kv => kv.Value);

        double midZonePct = (cfg.RingTopY + cfg.RingBotY) / 2.0;

        // Рахуємо середній Y верхнього і нижнього рядів з Ring-зразків
        var topYs = cfg.ColorSamples
            .Where(kv => TopRingColors.Contains(Enum.TryParse<WireColor>(kv.Key, out var wc) ? wc : WireColor.Unknown))
            .SelectMany(kv => kv.Value.Ring).Select(s => s.YPct).ToList();
        var botYs = cfg.ColorSamples
            .Where(kv => BotRingColors.Contains(Enum.TryParse<WireColor>(kv.Key, out var wc) ? wc : WireColor.Unknown))
            .SelectMany(kv => kv.Value.Ring).Select(s => s.YPct).ToList();

        double topYPct = topYs.Count > 0 ? topYs.Average() : cfg.RingTopY;
        double botYPct = botYs.Count > 0 ? botYs.Average() : cfg.RingBotY;

        int ringTopY = (int)(bmp.Height * topYPct);
        int ringBotY = (int)(bmp.Height * botYPct);
        const int halfH = 40;

        var top = ScanRingArea(bmp,
            ringTopY,
            (int)(bmp.Width * cfg.RingTopX1),
            (int)(bmp.Width * cfg.RingTopX2),
            halfH,
            topSamples, missingColor: WireColor.Black);

        var bot = ScanRingArea(bmp,
            ringBotY,
            (int)(bmp.Width * cfg.RingBotX1),
            (int)(bmp.Width * cfg.RingBotX2),
            halfH,
            botSamples, missingColor: WireColor.DarkBlue);

        return (top, bot);
    }

    // Скануємо прямокутну зону навколо лінії кілець,
    // знаходимо кластери по X і беремо домінуючий колір кожного кластера.
    static List<(Point center, WireColor color)> ScanRingArea(
        Bitmap bmp, int y, int x1, int x2, int halfH,
        Dictionary<WireColor, List<PixelSample>> ringSamples,
        WireColor missingColor)
    {
        x1 = Math.Clamp(x1, 0, bmp.Width  - 1);
        x2 = Math.Clamp(x2, 0, bmp.Width  - 1);
        if (x1 > x2) { int t = x1; x1 = x2; x2 = t; }

        int yMin = Math.Max(0, y - halfH);
        int yMax = Math.Min(bmp.Height - 1, y + halfH);

        // Якщо немає зразків — повертаємо 5 Unknown позицій (рівномірно по X)
        if (ringSamples.Count == 0)
        {
            var empty = new List<(Point, WireColor)>();
            int step = x2 > x1 ? (x2 - x1) / 4 : 0;
            for (int i = 0; i < 5; i++)
                empty.Add((new Point(x1 + i * step, y), missingColor));
            return empty;
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        // votes[x-x1] = кількість "влучень" по всіх кольорах
        var votes = new int[x2 - x1 + 1];
        // colorVotesPerX[x-x1][color] = кількість влучень для цього кольору
        var colorVotesPerX = new Dictionary<WireColor, int>[x2 - x1 + 1];

        unsafe
        {
            byte* s = (byte*)data.Scan0;
            for (int row = yMin; row <= yMax; row++)
            {
                byte* rowPtr = s + row * data.Stride;
                for (int x = x1; x <= x2; x++)
                {
                    int bv = rowPtr[x * 4], gv = rowPtr[x * 4 + 1], rv = rowPtr[x * 4 + 2];
                    var wc = ColorMatcher.MatchFromSamples(Color.FromArgb(rv, gv, bv), ringSamples, threshold: 1500);
                    if (wc == WireColor.Unknown) continue;

                    int i = x - x1;
                    votes[i]++;
                    colorVotesPerX[i] ??= new Dictionary<WireColor, int>();
                    colorVotesPerX[i][wc] = colorVotesPerX[i].GetValueOrDefault(wc) + 1;
                }
            }
        }

        bmp.UnlockBits(data);

        // Знаходимо кластери по X, зберігаємо вагу (totalVotes)
        var clusters = new List<(int cx, WireColor color, int weight)>();
        int clStart = -1, sumX = 0, sumCount = 0, totalVotes = 0;
        var colorVotes = new Dictionary<WireColor, int>();

        for (int i = 0; i <= votes.Length; i++)
        {
            bool hit = i < votes.Length && votes[i] > 0;
            if (hit)
            {
                if (clStart < 0) { clStart = i; colorVotes.Clear(); totalVotes = 0; }
                sumX += i; sumCount++;
                totalVotes += votes[i];
                if (colorVotesPerX[i] != null)
                    foreach (var (wc, cnt) in colorVotesPerX[i])
                        colorVotes[wc] = colorVotes.GetValueOrDefault(wc) + cnt;
            }
            else if (clStart >= 0)
            {
                if (sumCount >= 2)
                {
                    int cx = x1 + sumX / sumCount;
                    var dominant = colorVotes.MaxBy(kv => kv.Value).Key;
                    clusters.Add((cx, dominant, totalVotes));
                }
                clStart = -1; sumX = 0; sumCount = 0; totalVotes = 0;
            }
        }

        // Дедупліція кластерів < 30px (залишаємо важчий)
        var deduped = new List<(int cx, WireColor color, int weight)>();
        foreach (var c in clusters)
        {
            if (deduped.Count > 0 && c.cx - deduped[^1].cx < 30)
            {
                if (c.weight > deduped[^1].weight)
                    deduped[^1] = c;
                continue;
            }
            deduped.Add(c);
        }

        var result = deduped.Select(c => (new Point(c.cx, y), c.color, c.weight)).ToList();

        // Якщо знайшли більше 5 — застосовуємо grid-фільтрацію з урахуванням ваги кластерів
        if (result.Count > 5)
            result = FilterToGrid(result, 5, y);

        // Замінюємо Unknown → missingColor (темне кільце без зразків)
        for (int i = 0; i < result.Count; i++)
        {
            if (result[i].color == WireColor.Unknown)
                result[i] = (result[i].Item1, missingColor, result[i].weight);
        }

        return result.Select(r => (r.Item1, r.color)).ToList();
    }

    // Вибираємо підмножину з count елементів, що найкраще утворює рівномірну сітку.
    // Score = відхилення від рівної сітки - бонус за сумарну вагу (щоб відкидати слабкі хибні кластери)
    static List<(Point center, WireColor color, int weight)> FilterToGrid(
        List<(Point center, WireColor color, int weight)> items, int count, int y)
    {
        if (items.Count <= count) return items;

        var xs = items.Select(i => i.center.X).ToArray();
        var ws = items.Select(i => i.weight).ToArray();
        int maxW = ws.Max();
        int n = items.Count;
        int bestMask = 0;
        double bestScore = double.MaxValue;

        void Recurse(int start, int chosen, int mask)
        {
            if (chosen == count)
            {
                var sel = new List<int>();
                int sumW = 0;
                for (int b = 0; b < n; b++)
                    if ((mask & (1 << b)) != 0) { sel.Add(xs[b]); sumW += ws[b]; }
                sel.Sort();
                double step = (sel[^1] - sel[0]) / (double)(count - 1);
                if (step < 10) return;
                double gridErr = 0;
                for (int k = 0; k < count; k++)
                    gridErr += Math.Abs(sel[k] - (sel[0] + k * step));
                // Штраф за слабкі кластери: нормалізуємо вагу відносно maxW*count
                double weightPenalty = (maxW * count - sumW) * 0.5;
                double score = gridErr + weightPenalty;
                if (score < bestScore) { bestScore = score; bestMask = mask; }
                return;
            }
            for (int i = start; i <= n - (count - chosen); i++)
                Recurse(i + 1, chosen + 1, mask | (1 << i));
        }
        Recurse(0, 0, 0);

        var result = new List<(Point center, WireColor color, int weight)>();
        for (int b = 0; b < n; b++)
            if ((bestMask & (1 << b)) != 0) result.Add(items[b]);
        return result;
    }
}

// ── Drag Input ────────────────────────────────────────────────────────────────
static class DragInput
{
    public static void Drag(int fromX, int fromY, int toX, int toY, Action<string> log)
    {
        WinApi.SetCursorPos(fromX, fromY);
        Thread.Sleep(25);
        WinApi.mouse_event(0x0002, 0, 0, 0, 0); // mousedown
        Thread.Sleep(40);

        int steps = 15;
        for (int i = 1; i <= steps; i++)
        {
            int x = fromX + (toX - fromX) * i / steps;
            int y = fromY + (toY - fromY) * i / steps;
            WinApi.SetCursorPos(x, y);
            Thread.Sleep(9);
        }

        Thread.Sleep(25);
        WinApi.mouse_event(0x0004, 0, 0, 0, 0); // mouseup
        Thread.Sleep(50);
    }
}
