// A control that draws ASCII art of any size scaled to fit its area, centered.
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

public class ArtView : Control
{
    string[] lines = new string[0];
    Font fitted;
    Size fittedFor;
    SizeF cell;

    public ArtView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        Cursor = Cursors.Hand;
    }

    public string Art
    {
        set { lines = Prepare(value); DropFont(); Invalidate(); }
    }

    /// Normalizes lines: tabs -> spaces; strips leading/trailing blank lines and the common left indent.
    public static string[] Prepare(string t)
    {
        var ls = (t ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\t", "    ")
            .Split('\n').Select(l => l.TrimEnd()).ToList();
        while (ls.Count > 0 && ls[0].Length == 0) ls.RemoveAt(0);
        while (ls.Count > 0 && ls[ls.Count - 1].Length == 0) ls.RemoveAt(ls.Count - 1);
        int indent = ls.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        return ls.Select(l => l.Length >= indent ? l.Substring(indent) : "").ToArray();
    }

    void DropFont()
    {
        if (fitted != null) { fitted.Dispose(); fitted = null; }
    }

    static SizeF Cell(Graphics g, Font f)
    {
        var w = g.MeasureString(new string('M', 40), f, PointF.Empty, StringFormat.GenericTypographic).Width / 40f;
        return new SizeF(w, f.GetHeight(g));
    }

    /// Finds the largest Consolas size that fits the area (at most 24pt).
    Font Fit(Graphics g, int cols, int rows)
    {
        if (fitted != null && fittedFor == ClientSize) return fitted;
        DropFont();
        float size;
        using (var probe = new Font("Consolas", 10f))
        {
            var c = Cell(g, probe);
            size = 10f * Math.Min(ClientSize.Width / (c.Width * cols), ClientSize.Height / (c.Height * rows));
        }
        size = Math.Max(2f, Math.Min(24f, size));
        while (true)
        {
            var f = new Font("Consolas", size);
            var c = Cell(g, f);
            if ((c.Width * cols <= ClientSize.Width && c.Height * rows <= ClientSize.Height) || size <= 2f)
            {
                fitted = f; fittedFor = ClientSize; cell = c;
                return f;
            }
            f.Dispose();
            size -= 0.25f;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        // Grayscale antialiasing: ClearType caused color fringes on thin strokes (bluish "|")
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (lines.Length == 0 || ClientSize.Width < 10 || ClientSize.Height < 10) return;
        int cols = Math.Max(1, lines.Max(l => l.Length));
        int rows = lines.Length;
        var f = Fit(e.Graphics, cols, rows);
        float x0 = (ClientSize.Width - cell.Width * cols) / 2f;
        float y0 = (ClientSize.Height - cell.Height * rows) / 2f;
        using (var brush = new SolidBrush(ForeColor))
            for (int i = 0; i < rows; i++)
                e.Graphics.DrawString(lines[i], f, brush, x0, y0 + i * cell.Height, StringFormat.GenericTypographic);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DropFont();
        base.Dispose(disposing);
    }
}

/// A text box that captures pasting (Ctrl+V, Shift+Insert or right-click > Paste).
public class PasteBox : TextBox
{
    public event Action<string> Pasted;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0302) // WM_PASTE
        {
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (t != null && Pasted != null) Pasted(t);
            return;
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert))
        {
            var m = Message.Create(Handle, 0x0302, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref m);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
