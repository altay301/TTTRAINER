// FocusLock engine - blocks distracting sites 24/7 with fixed daily breaks.
//
// Architecture:
//   TTTRAINER.exe            -> UI (runs elevated, see Ui.cs)
//   TTTRAINER.exe --service  -> "FocusLock" Windows service (LocalSystem). Every 5 seconds:
//                               outside break hours it writes the block into the hosts file,
//                               during breaks it removes it; it also rewrites the state file if tampered with.
//   UI <-> service           -> \\.\pipe\FocusLockPipe (Administrators/SYSTEM only)
//
// Rules:
//   - Breaks: the first 10 minutes of every hour (07:00-07:10, 08:00-08:10, ...; local time).
//   - When a break ends, open browsers are closed so tabs from the break do not keep running.
//   - Adding sites is always allowed. Removing sites and uninstalling only during breaks.
//   - Service permissions: Administrators cannot stop/delete/reconfigure the service without
//     first changing its permissions. An emergency exit always exists: EMERGENCY-EXIT.txt (Safe Mode).
//
// Build: build.cmd (uses the .NET Framework csc.exe that ships with Windows, C# 5)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

public class LockState
{
    public List<string> Sites = new List<string>();
    public List<string> Breaks = new List<string>(Lock.DefaultBreaks);
}

public static class Lock
{
    public const string ServiceName = "FocusLock";
    public const string PipeName = "FocusLockPipe";
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusLock");
    public static readonly string StateFile = Path.Combine(Dir, "state.json");
    public static readonly string ExeCopy = Path.Combine(Dir, "FocusLock.exe");
    public static readonly string HostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
    const string MarkBeginPrefix = "# >>> FocusLock BEGIN";
    const string MarkBegin = MarkBeginPrefix + " (removed automatically when FocusLock is uninstalled)";
    const string MarkEnd = "# <<< FocusLock END";

    // SYSTEM: full control. Administrators: query/start/read+write permissions; NO stop / delete / change config.
    const string ServiceSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLOCRRCWD;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    // A 10-minute break at the top of every hour: 00:00-00:10, 01:00-01:10, ... 23:00-23:10
    public static readonly string[] DefaultBreaks = Enumerable.Range(0, 24)
        .Select(h => string.Format("{0:00}:00-{0:00}:10", h)).ToArray();

    public static readonly string[] DefaultSites = {
        "youtube.com", "youtu.be", "instagram.com", "tiktok.com", "twitter.com", "x.com",
        "facebook.com", "reddit.com", "twitch.tv", "netflix.com", "pinterest.com",
        "9gag.com", "eksisozluk.com", "kick.com"
    };

    // Browser policies that turn off DNS-over-HTTPS so browsers cannot bypass the hosts file
    static readonly object[][] DohPolicies = {
        new object[] { @"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off", RegistryValueKind.String },
        new object[] { @"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off", RegistryValueKind.String },
        new object[] { @"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off", RegistryValueKind.String },
        new object[] { @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0, RegistryValueKind.DWord },
        new object[] { @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1, RegistryValueKind.DWord },
    };

    // ---------- state ----------
    public static List<string> ToStringList(object s)
    {
        var list = new List<string>();
        if (s is string) list.Add((string)s);
        else if (s is IEnumerable) foreach (var o in (IEnumerable)s) if (o != null) list.Add(o.ToString());
        return list;
    }

    public static LockState Parse(string json)
    {
        var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        var st = new LockState();
        st.Sites = ToStringList(d.ContainsKey("Sites") ? d["Sites"] : null);
        var b = ToStringList(d.ContainsKey("Breaks") ? d["Breaks"] : null);
        if (b.Count > 0) st.Breaks = b;
        return st;
    }

    public static string Serialize(LockState st)
    {
        var d = new Dictionary<string, object>();
        d["Sites"] = st.Sites.ToArray();
        d["Breaks"] = st.Breaks.ToArray();
        return new JavaScriptSerializer().Serialize(d);
    }

    public static LockState Load()
    {
        try { return File.Exists(StateFile) ? Parse(File.ReadAllText(StateFile)) : null; }
        catch { return null; }
    }

    // ---------- break hours ----------
    static bool ParseWindow(string w, out int start, out int end)
    {
        start = end = 0;
        var m = Regex.Match(w ?? "", @"^\s*(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})\s*$");
        if (!m.Success) return false;
        start = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
        end = int.Parse(m.Groups[3].Value) * 60 + int.Parse(m.Groups[4].Value);
        if (end == 0) end = 1440; // "00:00" = midnight
        return true;
    }

    public static bool InBreak(LockState st, DateTime local)
    {
        int now = local.Hour * 60 + local.Minute;
        foreach (var w in st.Breaks)
        {
            int s, e;
            if (!ParseWindow(w, out s, out e)) continue;
            if (s < e ? (now >= s && now < e) : (now >= s || now < e)) return true;
        }
        return false;
    }

    /// Local time at which the break state changes next.
    public static DateTime NextChange(LockState st, DateTime local)
    {
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
        bool cur = InBreak(st, t);
        for (int i = 1; i <= 2 * 1440; i++)
            if (InBreak(st, t.AddMinutes(i)) != cur) return t.AddMinutes(i);
        return t.AddDays(2);
    }

    public static string BreaksText(LockState st)
    {
        if (st.Breaks.Count == DefaultBreaks.Length && DefaultBreaks.All(st.Breaks.Contains))
            return "the first 10 minutes of every hour (07:00–07:10, 08:00–08:10, ...)";
        return string.Join(", ", st.Breaks.Select(b => b.Replace("-", "–")));
    }

    // ---------- hosts / DoH ----------
    public static string NormalizeSite(string s)
    {
        string d = (s ?? "").Trim().ToLowerInvariant();
        d = Regex.Replace(d, "^[a-z]+://", "");
        d = Regex.Replace(d, "[/:?#].*$", "");
        d = Regex.Replace(d, @"^www\.", "");
        return d;
    }

    public static List<string> CleanSites(IEnumerable<string> sites)
    {
        return sites.Select(NormalizeSite).Where(s => s.Length > 0).Distinct().ToList();
    }

    static List<string> ExpandDomains(IEnumerable<string> sites)
    {
        var outList = new List<string>();
        foreach (var d in CleanSites(sites))
            foreach (var p in new[] { "", "www.", "m.", "mobile." })
                if (!outList.Contains(p + d)) outList.Add(p + d);
        return outList;
    }

    static List<string> HostsWithoutBlock()
    {
        var result = new List<string>();
        if (!File.Exists(HostsPath)) return result;
        bool inside = false;
        foreach (var l in File.ReadAllLines(HostsPath))
        {
            if (l.StartsWith(MarkBeginPrefix)) { inside = true; continue; }
            if (l == MarkEnd) { inside = false; continue; }
            if (!inside) result.Add(l);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
            result.RemoveAt(result.Count - 1);
        return result;
    }

    static bool WriteHosts(List<string> lines)
    {
        string text = string.Join("\r\n", lines) + "\r\n";
        string current = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
        if (current == text) return false;
        if (File.Exists(HostsPath))
        {
            var attr = File.GetAttributes(HostsPath);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(HostsPath, attr & ~FileAttributes.ReadOnly);
        }
        for (int i = 0; ; i++)
        {
            try { File.WriteAllText(HostsPath, text, Encoding.ASCII); return true; }
            catch { if (i >= 5) throw; Thread.Sleep(300); }
        }
    }

    static bool SetHostsBlock(List<string> domains)
    {
        var lines = HostsWithoutBlock();
        lines.Add("");
        lines.Add(MarkBegin);
        foreach (var d in domains) { lines.Add("0.0.0.0 " + d); lines.Add(":: " + d); }
        lines.Add(MarkEnd);
        return WriteHosts(lines);
    }

    static void SetDoh(bool enable)
    {
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            foreach (var p in DohPolicies)
            {
                if (enable)
                {
                    using (var k = hklm.CreateSubKey((string)p[0]))
                    {
                        object cur = k.GetValue((string)p[1]);
                        if (cur == null || cur.ToString() != p[2].ToString())
                            k.SetValue((string)p[1], p[2], (RegistryValueKind)p[3]);
                    }
                }
                else
                {
                    using (var k = hklm.OpenSubKey((string)p[0], true))
                        if (k != null) k.DeleteValue((string)p[1], false);
                }
            }
        }
    }

    // ---------- helpers ----------
    public static int Run(string exe, string args, out string output)
    {
        var psi = new ProcessStartInfo(exe, args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (var p = Process.Start(psi))
        {
            p.StandardInput.Close();
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
    }

    static void RunOrThrow(string exe, string args, string what)
    {
        string o;
        if (Run(exe, args, out o) != 0) throw new Exception(what + ":\n" + o.Trim());
    }

    static void FlushDns() { string o; try { Run("ipconfig.exe", "/flushdns", out o); } catch { } }

    static readonly string[] BrowserProcesses = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };
    public static readonly string EventLog = Path.Combine(Dir, "events.log");

    /// Closes every open browser so tabs opened during a break do not keep running after it.
    /// Browsers offer to restore the tabs on next start; blocked sites stay blocked.
    public static int CloseBrowsers()
    {
        int closed = 0;
        foreach (var name in BrowserProcesses)
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); closed++; } catch { }
                finally { p.Dispose(); }
            }
        return closed;
    }

    public static void Log(string line)
    {
        try
        {
            if (File.Exists(EventLog) && new FileInfo(EventLog).Length > 100 * 1024) File.Delete(EventLog);
            File.AppendAllText(EventLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch { }
    }

    public static bool ServiceInstalled()
    {
        try { using (var sc = new ServiceController(ServiceName)) { var s = sc.Status; return true; } }
        catch { return false; }
    }

    public static bool ServiceRunning()
    {
        try { using (var sc = new ServiceController(ServiceName)) return sc.Status == ServiceControllerStatus.Running; }
        catch { return false; }
    }

    public static void EnsureServiceRunning()
    {
        using (var sc = new ServiceController(ServiceName))
        {
            if (sc.Status == ServiceControllerStatus.Running) return;
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
    }

    // ---------- service side ----------
    /// Every 5 s: make sure the state file is intact; block outside breaks, unblock during breaks.
    public static void ApplyState(LockState st, bool inBreak)
    {
        string json = Serialize(st);
        string cur = null;
        try { if (File.Exists(StateFile)) cur = File.ReadAllText(StateFile); } catch { }
        if (cur != json) { Directory.CreateDirectory(Dir); File.WriteAllText(StateFile, json, new UTF8Encoding(false)); }

        bool changed = inBreak ? WriteHosts(HostsWithoutBlock()) : SetHostsBlock(ExpandDomains(st.Sites));
        if (changed) FlushDns();
        SetDoh(true);
    }

    /// Removes FocusLock completely and marks the service for deletion (runs as SYSTEM).
    public static void Finish()
    {
        string o;
        try { if (WriteHosts(HostsWithoutBlock())) FlushDns(); } catch { }
        try { SetDoh(false); } catch { }
        try { File.Delete(StateFile); } catch { }
        try { Run("icacls.exe", "\"" + Dir + "\" /reset /T /C /Q", out o); } catch { }
        try { Run("sc.exe", "delete " + ServiceName, out o); } catch { }
    }

    // ---------- UI side ----------
    /// Install: copy the exe to ProgramData, write the state, create + lock down + start the service.
    public static void Install(LockState st)
    {
        string o;
        if (Directory.Exists(Dir))
        {
            try { Run("takeown.exe", "/F \"" + Dir + "\" /A /R", out o); } catch { }
            try { Run("icacls.exe", "\"" + Dir + "\" /reset /T /C /Q", out o); } catch { }
        }
        Directory.CreateDirectory(Dir);

        string self = Application.ExecutablePath;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(ExeCopy), StringComparison.OrdinalIgnoreCase))
            for (int i = 0; ; i++)
            {
                try { File.Copy(self, ExeCopy, true); break; }
                catch { if (i >= 10) throw; Thread.Sleep(300); }
            }
        File.WriteAllText(StateFile, Serialize(st), new UTF8Encoding(false));

        try { Run("schtasks.exe", "/Delete /TN FocusLock /F", out o); } catch { } // scheduled task from the first version

        RunOrThrow("sc.exe", "create " + ServiceName + " binPath= \"\\\"" + ExeCopy + "\\\" --service\" start= auto" +
            " DisplayName= \"FocusLock Site Blocker\"", "Could not create the service");
        try
        {
            Run("sc.exe", "description " + ServiceName +
                " \"Blocks distracting sites 24/7 with fixed daily breaks.\"", out o);
            Run("sc.exe", "failure " + ServiceName + " reset= 86400 actions= restart/5000/restart/5000/restart/5000", out o);
            RunOrThrow("sc.exe", "sdset " + ServiceName + " " + ServiceSddl, "Could not set service permissions");
            // Folder: SYSTEM full control, Administrators/Users read-only. Files inherit from the folder.
            RunOrThrow("icacls.exe", "\"" + Dir + "\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F" +
                " *S-1-5-32-544:(OI)(CI)RX *S-1-5-32-545:(OI)(CI)RX /C /Q", "Could not set folder permissions");
            RunOrThrow("icacls.exe", "\"" + Path.Combine(Dir, "*") + "\" /reset /T /C /Q", "Could not set file permissions");
            try { EnsureServiceRunning(); }
            catch (Exception ex)
            {
                throw new Exception("Could not start the service: " +
                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
            }
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    /// Undoes a half-finished install.
    static void Rollback()
    {
        string o;
        try { Run("sc.exe", "sdset " + ServiceName + " D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)", out o); } catch { }
        try { Run("sc.exe", "stop " + ServiceName, out o); } catch { }
        try { Run("sc.exe", "delete " + ServiceName, out o); } catch { }
        try { Run("icacls.exe", "\"" + Dir + "\" /reset /T /C /Q", out o); } catch { }
        try { File.Delete(StateFile); } catch { }
        try { if (WriteHosts(HostsWithoutBlock())) FlushDns(); } catch { }
        try { SetDoh(false); } catch { }
    }

    public static Dictionary<string, object> Send(Dictionary<string, object> req)
    {
        var js = new JavaScriptSerializer();
        using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
        {
            c.Connect(5000);
            var w = new StreamWriter(c, new UTF8Encoding(false));
            w.AutoFlush = true;
            var r = new StreamReader(c, Encoding.UTF8);
            w.WriteLine(js.Serialize(req));
            string line = r.ReadLine();
            return js.Deserialize<Dictionary<string, object>>(line ?? "{\"ok\":false,\"message\":\"The service did not respond.\"}");
        }
    }
}

public class FocusService : ServiceBase
{
    LockState state;
    readonly object sync = new object();
    volatile bool stopping, uninstallRequested;
    readonly AutoResetEvent wake = new AutoResetEvent(false);

    public FocusService() { ServiceName = Lock.ServiceName; CanStop = true; CanShutdown = true; }

    protected override void OnStart(string[] args)
    {
        for (int i = 0; i < 10 && state == null; i++) { state = Lock.Load(); if (state == null) Thread.Sleep(500); }
        new Thread(Loop) { IsBackground = true }.Start();
        new Thread(PipeLoop) { IsBackground = true }.Start();
    }

    protected override void OnStop() { stopping = true; wake.Set(); }
    protected override void OnShutdown() { stopping = true; wake.Set(); }

    void Loop()
    {
        bool? lastBreak = null; // unknown right after the service starts: never close browsers on startup
        while (!stopping)
        {
            bool finished = false, breakEnded = false;
            try
            {
                lock (sync)
                {
                    if (state == null || uninstallRequested) finished = true;
                    else
                    {
                        bool inBreak = Lock.InBreak(state, DateTime.Now);
                        Lock.ApplyState(state, inBreak); // block is back in hosts before browsers close
                        breakEnded = lastBreak == true && !inBreak;
                        lastBreak = inBreak;
                    }
                }
            }
            catch { }
            if (breakEnded) Lock.Log("break ended, closed " + Lock.CloseBrowsers() + " browser processes");
            if (finished)
            {
                Lock.Finish();
                ThreadPool.QueueUserWorkItem(delegate { try { Stop(); } catch { } });
                return;
            }
            wake.WaitOne(5000);
        }
    }

    void PipeLoop()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stopping)
        {
            try
            {
                using (var server = new NamedPipeServerStream(Lock.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, ps))
                {
                    server.WaitForConnection();
                    var r = new StreamReader(server, Encoding.UTF8);
                    var w = new StreamWriter(server, new UTF8Encoding(false));
                    w.AutoFlush = true;
                    w.WriteLine(Handle(r.ReadLine()));
                    try { server.WaitForPipeDrain(); } catch { }
                }
            }
            catch { Thread.Sleep(1000); }
        }
    }

    string Handle(string line)
    {
        var js = new JavaScriptSerializer();
        var resp = new Dictionary<string, object>();
        resp["ok"] = false;
        try
        {
            var req = js.Deserialize<Dictionary<string, object>>(line ?? "{}");
            string cmd = req.ContainsKey("cmd") ? req["cmd"] as string : null;
            lock (sync)
            {
                if (state == null) { resp["message"] = "FocusLock is not active."; return js.Serialize(resp); }
                bool inBreak = Lock.InBreak(state, DateTime.Now);
                string onlyInBreak = "This is only allowed during break hours (" + Lock.BreaksText(state) + ").";

                if (cmd == "save")
                {
                    var sites = Lock.CleanSites(Lock.ToStringList(req.ContainsKey("sites") ? req["sites"] : null));
                    var removed = state.Sites.Where(s => !sites.Contains(s)).ToList();
                    if (removed.Count > 0 && !inBreak)
                        resp["message"] = "Removing sites: " + onlyInBreak + "\n\nTried to remove: " + string.Join(", ", removed) +
                                          "\n\nNothing was saved.";
                    else if (sites.Count == 0)
                        resp["message"] = "The list cannot be empty. To stop FocusLock, uninstall it during a break.";
                    else
                    {
                        state.Sites = sites;
                        Lock.ApplyState(state, inBreak);
                        resp["ok"] = true;
                        resp["message"] = "Site list saved (" + sites.Count + " sites).";
                    }
                }
                else if (cmd == "uninstall")
                {
                    if (!inBreak) resp["message"] = "Uninstalling: " + onlyInBreak;
                    else
                    {
                        uninstallRequested = true;
                        wake.Set();
                        resp["ok"] = true;
                        resp["message"] = "FocusLock will be removed completely in a few seconds.";
                    }
                }
                else resp["message"] = "Unknown command.";
            }
        }
        catch (Exception ex) { resp["message"] = "Error: " + ex.Message; }
        return js.Serialize(resp);
    }
}
