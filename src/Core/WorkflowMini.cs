using System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>
/// What the small workflow window shows: the folder and mode for new work, its own conversations, the ones
/// open in VS Code or a terminal, and what the line will go to (a reply, or a copy of an open conversation).
/// </summary>
internal sealed record WorkflowMiniData(string Folder, string Mode, IReadOnlyList<ClaudeSession> Sessions, ClaudeSession? ReplyTo,
    IReadOnlyList<LiveSession> Live, LiveSession? Resume);

/// <summary>
/// The small workflow window, as compact as the overlay: one line to type an instruction, the folder and
/// permission mode under it, and — only while there are conversations — one short row per conversation
/// with its status, the latest step or reply, and the buttons that matter now (allow/deny, stop, reply,
/// close). Under them, conversations open in VS Code or a terminal that are working or were active in
/// the last two hours, each with 「接手」 to continue a copy of it here. Rows slide in; the status ring turns
/// while Claude works.
/// Actions: folder, mode, detail, hide, allow:{i}, deny:{i}, stop:{i}, reply:{i}, close:{i}, open:{i}, resume:{i}.
/// </summary>
internal static class WorkflowMini
{
    public const float Width = 400;
    const float M = 12, RowH = 76, MaxRows = 5, LiveH = 46, MaxLive = 4;

    /// <summary>Draws the window and returns its height; <paramref name="input"/> is where the host puts its text box.</summary>
    public static float Render(IPaint p, PageState state, WorkflowMiniData d, out RectangleF input)
    {
        state.BeginFrame();
        var m = state.Motion;
        var shown = d.Sessions.Reverse().Take((int)MaxRows).ToList();
        var live = d.Live.Take((int)MaxLive).ToList();
        float height = 92 + shown.Count * (RowH + 8) + (d.Sessions.Count > MaxRows ? 24 : 0)
            + (live.Count > 0 ? 28 + live.Count * (LiveH + 6) : 0) + (shown.Count + live.Count > 0 ? 4 : 0);
        var all = new RectangleF(0, 0, Width, height);
        p.Gradient(all, PageKit.PageTop, PageKit.PageBottom, 0, vertical: true);
        p.Circle(new PointF(Width * 0.85f, -20), 120, PageKit.WithAlpha(PageKit.Violet, 22));
        p.Circle(new PointF(Width * 0.1f, height), 110, PageKit.WithAlpha(PageKit.Indigo, 18));
        PageKit.GlassSheen(p, all, 0, 0.6f);
        p.Gradient(new RectangleF(0, 0, Width, 3), PageKit.Orange, PageKit.Violet);

        // The instruction line.
        PageKit.Icon(p, IconKind.Prompt, new RectangleF(M + 2, 18, 22, 22), PageKit.Orange, 2.2f);
        var field = new RectangleF(M + 34, 12, Width - M * 2 - 34, 36);
        PageKit.Field(p, field, m.HoverOf("input", field), sheen: false);
        state.Hits.Add(new Hit(field, "input"));
        input = RectangleF.Inflate(field, -12, -8);
        if (d.ReplyTo != null || d.Resume != null)
        {
            // Replying or continuing another conversation: a small tag says so.
            string tag = d.ReplyTo != null ? "回覆" : "接手";
            float tw = p.Measure(tag, 8.5f, bold: true) + 14;
            p.Fill(new RectangleF(field.Right - tw - 6, field.Y + 8, tw, 20), PageKit.WithAlpha(d.ReplyTo != null ? PageKit.Green : PageKit.Violet, 90), 10);
            p.Text(tag, new RectangleF(field.Right - tw - 6, field.Y + 8, tw, 20), 8.5f, PageKit.Text, bold: true, align: TextAlign.Center, middle: true);
            input.Width -= tw + 8;
        }

        // Folder, permission mode, the full view and hide.
        float y = 56, x = M + 34;
        x = Chip(p, state, "folder", IconKind.Folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(d.Folder)) + " ▾", x, y, Width * 0.42f) + 8;
        var mode = PermissionModes.Of(d.Mode);
        Chip(p, state, "mode", IconKind.Check, "權限：" + mode.Name + " ▾", x, y, Width * 0.36f, d.Mode == PermissionModes.Everything ? PageKit.Hex(0xFB7185) : null);
        float right = Width - M;
        right = Chip(p, state, "hide", null, "✕", right - 24, y, 24) - 24 - 4;
        Chip(p, state, "detail", IconKind.External, "詳細", right - 48, y, 52);

        float ry = 92;
        for (int i = 0; i < shown.Count; i++)
        {
            Row(p, state, shown[i], d.Sessions.Count - 1 - i, new RectangleF(M, ry, Width - M * 2, RowH), d.ReplyTo == shown[i]);
            ry += RowH + 8;
        }
        if (d.Sessions.Count > MaxRows)
        {
            p.Text($"還有 {d.Sessions.Count - MaxRows} 個工作，按「詳細」查看", new RectangleF(M, ry - 4, Width - M * 2, 20), 8.5f, PageKit.Muted, align: TextAlign.Center);
            ry += 24;
        }
        if (live.Count > 0)
        {
            p.Text("VS Code／終端機裡開著的對話　接手會另開分身，原對話不受影響", new RectangleF(M + 2, ry, Width - M * 2, 22), 8.5f, PageKit.Muted, middle: true);
            ry += 28;
            for (int i = 0; i < live.Count; i++)
            {
                Live(p, state, live[i], i, new RectangleF(M, ry, Width - M * 2, LiveH), d.Resume?.SessionId == live[i].SessionId);
                ry += LiveH + 6;
            }
        }
        return height;
    }

    /// <summary>A conversation open elsewhere: title, where and how recently, and 「接手」.</summary>
    static void Live(IPaint p, PageState state, LiveSession s, int index, RectangleF r, bool chosen)
    {
        var m = state.Motion;
        var area = RectangleF.Inflate(r, 6, 12);
        float enter = Motion.Out(m.In("live:" + s.SessionId, area, 80, 420));
        using var entering = PageKit.Enter(p, enter, 0, -8);
        var color = s.Busy ? PageKit.Violet : PageKit.Muted;
        p.Gradient(r, PageKit.WithAlpha(PageKit.PanelTop, 190), PageKit.WithAlpha(PageKit.PanelBottom, 210), 12, vertical: true);
        PageKit.GlassSheen(p, r, 12, 0.6f);
        p.Stroke(r, chosen ? PageKit.WithAlpha(PageKit.Violet, 230) : PageKit.Hex(0x343A66), chosen ? 1.8f : 1.1f, 12);

        // A still ring: these can stay busy for hours elsewhere, and the window must not keep redrawing for them.
        var dot = new PointF(r.X + 16, r.Y + 15);
        p.Circle(dot, 4, color);
        if (s.Busy)
            p.Circle(dot, 8, null, PageKit.WithAlpha(color, 170), 1.6f);

        string action = "resume:" + index;
        string text = chosen ? "已選" : "接手";
        float bw = PageKit.ButtonWidth(p, text, null, 9f);
        var b = new RectangleF(r.Right - 8 - bw, r.Y + (r.Height - 26) / 2, bw, 26);
        var (h, sweep) = state.Over(action, b);
        PageKit.Button(p, state.Hits, action, b, text, null, chosen, h, sweep, points: 9f);

        p.Text(s.Title, new RectangleF(r.X + 30, r.Y + 4, b.Left - r.X - 36, 22), 9.5f, PageKit.Text, bold: true, middle: true);
        var ago = DateTimeOffset.UtcNow - s.Active;
        string when = s.Busy ? "工作中" : ago.TotalMinutes < 1 ? "剛剛" : $"{(int)ago.TotalMinutes} 分鐘前";
        p.Text($"{s.From} · {Path.GetFileName(Path.TrimEndingDirectorySeparator(s.Folder))} · {when}", new RectangleF(r.X + 30, r.Y + 24, b.Left - r.X - 36, 18), 8.5f, PageKit.Muted, middle: true);
    }

    /// <summary>A small text link with an optional icon; returns its right edge.</summary>
    static float Chip(IPaint p, PageState state, string action, IconKind? icon, string text, float x, float y, float maxWidth, Color? color = null)
    {
        float textWidth = Math.Min(p.Measure(text, 9f), maxWidth - (icon == null ? 8 : 26));
        var r = new RectangleF(x, y, textWidth + (icon == null ? 8 : 26), 24);
        float hover = state.Motion.HoverOf(action, r);
        if (hover > 0)
            p.Fill(r, Color.FromArgb((int)(28 * hover), 255, 255, 255), 8);
        var ink = PageKit.Mix(color ?? PageKit.Muted, Color.White, hover * 0.6f);
        if (icon is { } kind)
            PageKit.Icon(p, kind, new RectangleF(r.X + 6, r.Y + 5, 14, 14), ink, 1.4f);
        p.Text(text, new RectangleF(r.X + (icon == null ? 4 : 24), r.Y, textWidth + 4, r.Height), 9f, ink, middle: true);
        state.Hits.Add(new Hit(r, action));
        return r.Right;
    }

    static void Row(IPaint p, PageState state, ClaudeSession s, int index, RectangleF r, bool target)
    {
        var m = state.Motion;
        var area = RectangleF.Inflate(r, 6, 16);
        // Each new conversation slides in from the top.
        float enter = Motion.Out(m.In("job:" + s.Started.UtcTicks, area, 0, 420));
        using var entering = PageKit.Enter(p, enter, 0, -12);
        var (word, color) = s.State switch
        {
            JobState.Starting => ("啟動中", PageKit.Orange),
            JobState.Working => ("工作中", PageKit.Violet),
            JobState.NeedsPermission => ("等你允許", PageKit.Hex(0xFBBF24)),
            JobState.Waiting => ("等你回覆", PageKit.Green),
            JobState.Stopped => ("已結束", PageKit.Muted),
            _ => ("已中斷", PageKit.Hex(0xFB7185)),
        };
        string open = "open:" + index;
        state.Hits.Add(new Hit(r, open));
        float hover = m.HoverOf(open, area);
        PageKit.Glow(p, r, 14, color, (int)((s.State == JobState.NeedsPermission ? 120 : 40) + 50 * hover));
        p.Gradient(r, PageKit.WithAlpha(PageKit.Mix(PageKit.PanelTop, color, 0.18f), 230), PageKit.WithAlpha(PageKit.PanelBottom, 235), 14, vertical: true);
        PageKit.GlassSheen(p, r, 14, 0.8f);
        p.Stroke(r, PageKit.WithAlpha(color, target ? 230 : (int)(120 + 80 * hover)), target ? 1.8f : 1.2f, 14);

        // Status: a ring that turns while Claude works.
        var dot = new PointF(r.X + 18, r.Y + 20);
        p.Circle(dot, 4.5f, color);
        if (s.Running)
            p.Arc(dot, 9, m.Spin(s.State != JobState.NeedsPermission, RectangleF.Inflate(new RectangleF(dot.X, dot.Y, 0, 0), 12, 12)), 250, PageKit.WithAlpha(color, 170), 1.8f);
        p.Text(word, new RectangleF(r.X + 32, r.Y + 9, 70, 22), 9.5f, color, bold: true, middle: true);

        // Buttons that matter in this state.
        (string Action, string Text, bool Primary)[] buttons = s.State switch
        {
            JobState.NeedsPermission => [("deny:" + index, "拒絕", false), ("allow:" + index, "允許", true)],
            JobState.Starting or JobState.Working => [("stop:" + index, "停止", false)],
            JobState.Waiting => [("close:" + index, "關閉", false), ("reply:" + index, "回覆", true)],
            _ => [("close:" + index, "關閉", false)],
        };
        float bx = r.Right - 10;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var (action, text, primary) = buttons[i];
            float bw = PageKit.ButtonWidth(p, text, null, 9.5f);
            bx -= bw;
            var b = new RectangleF(bx, r.Y + (r.Height - 30) / 2, bw, 30);
            var (h, sweep) = state.Over(action, b);
            PageKit.Button(p, state.Hits, action, b, text, null, primary, h, sweep, points: 9.5f);
            bx -= 6;
        }

        p.Text(s.Title, new RectangleF(r.X + 100, r.Y + 9, bx - r.X - 104, 22), 10f, PageKit.Text, bold: true, middle: true);
        // The latest step, the question, or the start of the reply.
        string latest;
        Color ink;
        if (s.Ask is { } ask)
            (latest, ink) = ("Claude 想要" + ask.Summary, PageKit.Hex(0xFBBF24));
        else
        {
            var (lines, live) = s.Snapshot();
            var last = lines.LastOrDefault(l => l.Kind != JobLineKind.User);
            latest = live.Length > 0 ? live : last?.Text ?? "準備中…";
            ink = last?.Kind == JobLineKind.Error ? PageKit.Hex(0xFB7185) : last?.Kind == JobLineKind.Claude || live.Length > 0 ? PageKit.Hex(0xE6E2F8) : PageKit.Muted;
        }
        p.Text(latest.Replace('\n', ' '), new RectangleF(r.X + 14, r.Y + 38, bx - r.X - 18, 30), 9f, ink, maxLines: 2);
    }
}
