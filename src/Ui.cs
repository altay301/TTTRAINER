// TTTRAINER UI - a minimal terminal-style window.
// Left:  ASCII art slides. Click the art -> "<  -  +  >" appears below it
//        (previous / delete this art (press twice) / add a new art by pasting / next).
// Right: TTTRAINER, site line ( > ____  [+]/[-] ), START. Buttons are plain clickable text.
// Feedback is color, not text: the ">" turns green = added, yellow = removed, red = not done.
// START is cyan when not installed, green while running. Right-click START during a break -> uninstall.
// Text uses the embedded IBM 3270 font; colors follow the Windows PowerShell console.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;

public class MainForm : Form
{
    // Windows PowerShell console colors (Windows Terminal "Campbell Powershell" scheme + PSReadLine highlights)
    static readonly Color PsBackground = Color.FromArgb(1, 36, 86);     // PowerShell blue
    static readonly Color PasteBackground = Color.FromArgb(0, 26, 64);
    static readonly Color PsForeground = Color.FromArgb(204, 204, 204); // default text   -> ">" and [+]
    static readonly Color PsBrightWhite = Color.FromArgb(242, 242, 242);// typed site
    static readonly Color PsRed = Color.FromArgb(231, 72, 86);          // error text     -> art, failure, "-"
    static readonly Color PsGreen = Color.FromArgb(22, 198, 12);        // keyword        -> "+", added, START (running)
    static readonly Color PsYellow = Color.FromArgb(249, 241, 165);     // command        -> TTTRAINER, removed
    static readonly Color PsCyan = Color.FromArgb(58, 150, 221);        // string         -> START (not installed)
    static readonly Color PsGray = Color.FromArgb(118, 118, 118);       // operator       -> "<" ">"

    // The IBM 3270 font is embedded in the exe (src/fonts/3270-Regular.ttf, BSD license: 3270-LICENSE.txt).
    // It is never installed into Windows; it is loaded from memory while the program runs.
    [DllImport("gdi32.dll")]
    static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    static readonly System.Drawing.Text.PrivateFontCollection EmbeddedFonts = new System.Drawing.Text.PrivateFontCollection();
    static readonly FontFamily Ibm3270 = LoadEmbeddedFont("font3270.ttf");

    static FontFamily LoadEmbeddedFont(string resource)
    {
        try
        {
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream(resource))
            {
                if (s == null) return null;
                var data = new byte[s.Length];
                s.Read(data, 0, data.Length);
                IntPtr mem = Marshal.AllocCoTaskMem(data.Length); // must stay alive for the whole run
                Marshal.Copy(data, 0, mem, data.Length);
                uint n = 0;
                AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref n); // for GDI (Label/TextBox)
                EmbeddedFonts.AddMemoryFont(mem, data.Length);                    // for GDI+
                return EmbeddedFonts.Families.Length > 0 ? EmbeddedFonts.Families[0] : null;
            }
        }
        catch { return null; }
    }

    static readonly string FallbackFont = FontInstalled("Cascadia Code") ? "Cascadia Code" : "Consolas";

    static bool FontInstalled(string name)
    {
        try
        {
            using (var fc = new System.Drawing.Text.InstalledFontCollection())
                return fc.Families.Any(f => f.Name == name);
        }
        catch { return false; }
    }

    ArtView artView;
    PasteBox pasteBox;
    Label navPrev, navDel, navAdd, navNext;
    bool delArmed;
    Timer delTimer;
    Label prompt, btnToggle, btnStart;
    TextBox txtSite;
    Timer timer, flashTimer, navTimer;
    List<string> pending = new List<string>(Lock.DefaultSites); // site list before installing

    // Original drawing: Tung Tung Tung Sahur (a wooden log holding a bat)
    static readonly string[] SahurArt = {
        "           .----------.             ___",
        "          /  ~~~~~~~~  \\           /   \\",
        "         |              |          |   |",
        "         |   .-.   .-.  |          |   |",
        "         |   |@|   |@|  |          |   |",
        "         |   '-'   '-'  |          |   |",
        "         |              |           \\ /",
        "         |     .--.     |            |",
        "         |     '--'     |            |",
        "         |              |____        |",
        "    _____|  |   |   |   |    \\______(O)",
        "   /     |              |            |",
        "  /  ____|  |   |   |   |           (_)",
        " /  /    |              |",
        "(__/     |  |   |   |   |",
        "         |              |",
        "         |  |   |   |   |",
        "          \\____________/",
        "            |  |    |  |",
        "            |  |    |  |",
        "            |  |    |  |",
        "          __|  |    |  |__",
        "         (_____|    |_____)",
    };

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // dark title bar
        // Windows 11: PowerShell-blue title bar with white caption text (COLORREF = 0x00BBGGRR)
        int caption = (PsBackground.B << 16) | (PsBackground.G << 8) | PsBackground.R, text = 0x00F2F2F2;
        try { DwmSetWindowAttribute(Handle, 35, ref caption, 4); DwmSetWindowAttribute(Handle, 36, ref text, 4); } catch { }
    }

    static Font Mono(float size, FontStyle style = FontStyle.Regular)
    {
        return Ibm3270 != null ? new Font(Ibm3270, size, style) : new Font(FallbackFont, size, style);
    }

    // Like a console: every piece of text has the same size and weight
    const float TextSize = 14F;
    static readonly Font ConsoleFont = Mono(TextSize);
    static readonly Font ConsoleFontLink = Mono(TextSize, FontStyle.Underline);

    /// Clickable console text: no button chrome; underlined on hover like a terminal link.
    static Label TextButton(string text, Color color)
    {
        var l = new Label { Text = text, Font = ConsoleFont, ForeColor = color, BackColor = PsBackground,
            Cursor = Cursors.Hand, AutoSize = true, Padding = Padding.Empty, Margin = Padding.Empty };
        l.MouseEnter += delegate { l.Font = ConsoleFontLink; };
        l.MouseLeave += delegate { l.Font = ConsoleFont; };
        return l;
    }

    // ---------- ASCII art slides ----------
    // Key: a fixed id for built-in / next-to-exe art (written to hidden.txt when deleted), the file path for added art.
    class Slide { public string Text; public string File; public string Key; }

    static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TTTRAINER");
    static readonly string ArtDir = Path.Combine(AppDataDir, "arts");
    static readonly string SlideFile = Path.Combine(AppDataDir, "slide.txt");
    static readonly string HiddenFile = Path.Combine(AppDataDir, "hidden.txt");

    List<Slide> slides = new List<Slide>();
    int slideIndex;

    static HashSet<string> Hidden()
    {
        try { if (File.Exists(HiddenFile)) return new HashSet<string>(File.ReadAllLines(HiddenFile)); } catch { }
        return new HashSet<string>();
    }

    void LoadSlides()
    {
        slides.Clear();
        var hidden = Hidden();
        if (!hidden.Contains("builtin:sahur"))
            slides.Add(new Slide { Text = string.Join("\n", SahurArt), Key = "builtin:sahur" });
        try
        {
            string f = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "sahur.txt");
            if (File.Exists(f) && !hidden.Contains("exe:sahur.txt"))
                slides.Add(new Slide { Text = File.ReadAllText(f), Key = "exe:sahur.txt" });
        }
        catch { }
        try
        {
            if (Directory.Exists(ArtDir))
                foreach (var f in Directory.GetFiles(ArtDir, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    slides.Add(new Slide { Text = File.ReadAllText(f), File = f, Key = f });
        }
        catch { }
    }

    void ShowSlide(int i)
    {
        if (slides.Count == 0) { slideIndex = 0; artView.Art = ""; return; } // everything deleted: empty area
        slideIndex = ((i % slides.Count) + slides.Count) % slides.Count;
        artView.Art = slides[slideIndex].Text;
        try { Directory.CreateDirectory(AppDataDir); File.WriteAllText(SlideFile, slideIndex.ToString()); } catch { }
    }

    void SetNav(bool visible)
    {
        navPrev.Visible = navDel.Visible = navAdd.Visible = navNext.Visible = visible;
        DisarmDelete();
        navTimer.Stop();
        if (visible) navTimer.Start(); // hides itself after a few idle seconds
    }

    void OnArtClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) SetNav(!navPrev.Visible);
    }

    // "-" turns into "x" on the first press (for 3 s); the second press deletes the art on screen.
    void OnDeleteClick()
    {
        if (slides.Count == 0) return;
        if (!delArmed)
        {
            delArmed = true;
            navDel.Text = "x";
            delTimer.Stop();
            delTimer.Start();
            navTimer.Stop();
            navTimer.Start();
            return;
        }
        var s = slides[slideIndex];
        try
        {
            if (s.File != null) File.Delete(s.File);
            else
            {
                Directory.CreateDirectory(AppDataDir);
                File.AppendAllText(HiddenFile, s.Key + Environment.NewLine);
            }
        }
        catch { }
        LoadSlides();
        ShowSlide(slideIndex);
        SetNav(true);
    }

    void DisarmDelete()
    {
        delArmed = false;
        if (navDel != null) navDel.Text = "-";
        if (delTimer != null) delTimer.Stop();
    }

    void OpenPasteBox()
    {
        SetNav(false);
        pasteBox.Clear();
        pasteBox.Visible = true;
        pasteBox.BringToFront();
        pasteBox.Focus();
    }

    void ClosePasteBox()
    {
        pasteBox.Visible = false;
        pasteBox.Clear();
        txtSite.Focus();
    }

    void AddArt(string text)
    {
        if (ArtView.Prepare(text).All(l => l.Trim().Length == 0)) return;
        try
        {
            Directory.CreateDirectory(ArtDir);
            string f = Path.Combine(ArtDir, "art_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
            File.WriteAllText(f, text, new UTF8Encoding(false));
            ClosePasteBox();
            LoadSlides();
            ShowSlide(slides.FindIndex(s => s.File == f));
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "TTTRAINER"); }
    }

    // ---------- window ----------
    public MainForm()
    {
        SuspendLayout();
        Text = "TTTRAINER";
        BackColor = PsBackground;
        Font = Mono(10F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 372);

        // Left: art + navigation
        artView = new ArtView { Location = new Point(16, 16), Size = new Size(318, 300), ForeColor = PsRed, BackColor = PsBackground };
        artView.MouseUp += (s, e) => OnArtClick(e);

        navPrev = TextButton("<", PsGray);
        navDel = TextButton("-", PsRed);
        navAdd = TextButton("+", PsGreen);
        navNext = TextButton(">", PsGray);
        navPrev.Location = new Point(125, 326);
        navDel.Location = new Point(155, 326);
        navAdd.Location = new Point(185, 326);
        navNext.Location = new Point(215, 326);
        navPrev.Click += delegate { ShowSlide(slideIndex - 1); SetNav(true); };
        navNext.Click += delegate { ShowSlide(slideIndex + 1); SetNav(true); };
        navDel.Click += delegate { OnDeleteClick(); };
        navAdd.Click += delegate { OpenPasteBox(); };

        pasteBox = new PasteBox { Location = new Point(16, 16), Size = new Size(318, 340), Multiline = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, Visible = false,
            BackColor = PasteBackground, ForeColor = PsRed, BorderStyle = BorderStyle.None, Font = Mono(8F) };
        pasteBox.Pasted += t => AddArt(t);
        pasteBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; ClosePasteBox(); }
            else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; AddArt(pasteBox.Text); }
        };

        // Right: like console lines, all the same size, left-aligned
        int x = 380;
        var title = new Label { Location = new Point(x, 120), AutoSize = true, Text = "TTTRAINER",
            ForeColor = PsYellow, Font = ConsoleFont };

        prompt = new Label { Location = new Point(x, 168), AutoSize = true, Text = ">", ForeColor = PsForeground,
            Font = ConsoleFont };
        txtSite = new TextBox { Location = new Point(x + 22, 170), Width = 260, BackColor = PsBackground, ForeColor = PsBrightWhite,
            BorderStyle = BorderStyle.None, Font = ConsoleFont };
        txtSite.TextChanged += delegate { UpdateToggle(); };
        txtSite.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Toggle(); } };
        btnToggle = TextButton("[+]", PsForeground);
        btnToggle.Location = new Point(x + 290, 168);
        btnToggle.Click += delegate { Toggle(); };

        btnStart = TextButton("START", PsCyan);
        btnStart.Location = new Point(x, 216);
        btnStart.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) OnStart();
            else if (e.Button == MouseButtons.Right) OnUninstall();
        };

        Controls.AddRange(new Control[] { pasteBox, artView, navPrev, navDel, navAdd, navNext, title, prompt, txtSite, btnToggle, btnStart });

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();

        delTimer = new Timer { Interval = 3000 };
        delTimer.Tick += delegate { DisarmDelete(); };
        navTimer = new Timer { Interval = 6000 };
        navTimer.Tick += delegate { SetNav(false); };
        SetNav(false);

        LoadSlides();
        int saved = 0;
        try { if (File.Exists(SlideFile)) int.TryParse(File.ReadAllText(SlideFile).Trim(), out saved); } catch { }
        ShowSlide(saved < slides.Count ? saved : 0);

        flashTimer = new Timer { Interval = 700 };
        flashTimer.Tick += delegate { flashTimer.Stop(); prompt.ForeColor = PsForeground; };
        timer = new Timer { Interval = 2000 };
        timer.Tick += delegate { PaintStart(); };
        timer.Start();
        PaintStart();
        UpdateToggle();
        ActiveControl = txtSite;
    }

    static bool Installed() { return Lock.ServiceInstalled(); }

    List<string> CurrentSites()
    {
        if (Installed()) { var st = Lock.Load(); if (st != null) return st.Sites; }
        return pending;
    }

    void Flash(Color c)
    {
        prompt.ForeColor = c;
        flashTimer.Stop();
        flashTimer.Start();
    }

    void UpdateToggle()
    {
        string s = Lock.NormalizeSite(txtSite.Text);
        btnToggle.Text = s.Length > 0 && CurrentSites().Contains(s) ? "[-]" : "[+]";
    }

    /// START has a fixed color: cyan when not installed, green while running. It does not blink.
    void PaintStart()
    {
        btnStart.ForeColor = Installed() ? PsGreen : PsCyan;
    }

    void Toggle()
    {
        string s = Lock.NormalizeSite(txtSite.Text);
        if (s.Length == 0) { Flash(PsRed); return; }
        bool remove = CurrentSites().Contains(s);

        if (!Installed())
        {
            if (remove) pending.Remove(s); else pending.Add(s);
            Flash(remove ? PsYellow : PsGreen);
            txtSite.Clear();
            return;
        }

        try
        {
            Cursor = Cursors.WaitCursor;
            var list = new List<string>(CurrentSites());
            if (remove) list.Remove(s); else list.Add(s);
            Lock.EnsureServiceRunning();
            var req = new Dictionary<string, object>();
            req["cmd"] = "save";
            req["sites"] = list.ToArray();
            var resp = Lock.Send(req);
            bool ok = resp.ContainsKey("ok") && resp["ok"] is bool && (bool)resp["ok"];
            if (ok) { Flash(remove ? PsYellow : PsGreen); txtSite.Clear(); }
            else Flash(PsRed); // e.g. trying to remove a site outside break hours
        }
        catch { Flash(PsRed); }
        finally { Cursor = Cursors.Default; UpdateToggle(); }
    }

    void OnStart()
    {
        if (Installed()) return;
        if (pending.Count == 0) { Flash(PsRed); return; }

        string msg = string.Format("{0} sites will be blocked 24/7. They will only be open every day during {1}.\n\n" +
            "Removing sites and uninstalling TTTRAINER will only be possible during break hours. Are you sure?",
            pending.Count, Lock.BreaksText(new LockState()));
        if (MessageBox.Show(this, msg, "TTTRAINER", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        try
        {
            Cursor = Cursors.WaitCursor;
            var st = new LockState();
            st.Sites = Lock.CleanSites(pending);
            Lock.Install(st);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "TTTRAINER", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; PaintStart(); UpdateToggle(); }
    }

    void OnUninstall()
    {
        var st = Lock.Load();
        if (!Installed() || st == null || !Lock.InBreak(st, DateTime.Now)) return; // break hours only
        if (MessageBox.Show(this, "Uninstall TTTRAINER? All sites will be unblocked.", "TTTRAINER",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Cursor = Cursors.WaitCursor;
            Lock.EnsureServiceRunning();
            var req = new Dictionary<string, object>();
            req["cmd"] = "uninstall";
            Lock.Send(req);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "TTTRAINER"); }
        finally { Cursor = Cursors.Default; PaintStart(); }
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new FocusService());
            return 0;
        }
        if (args.Any(a => string.Equals(a, "--enforce", StringComparison.OrdinalIgnoreCase)))
            return 0; // left over from the first version's scheduled task

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
