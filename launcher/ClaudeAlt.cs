// claude-alt launcher + tool for Windows.
//
// claude-alt.ps1 compiles this one file twice:
//   claude-alt-launcher.exe  (/target:winexe)  what each clone's Start-menu shortcut runs
//   claude-alt-tool.exe      (/target:exe)     icons, shortcuts and probes for the script
//
// It must stay C# 5 (no $"", ?., nameof, out var): it is built with the csc.exe
// that ships inside .NET Framework 4.x on every Windows 10/11 machine, so users
// need no SDK and no downloaded binaries.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class ClaudeAlt
{
    const string BaseHex = "d97757";   // the stock Claude clay orange
    static bool Gui;                   // launcher mode: report errors in a message box

    [STAThread]
    static int Main(string[] argv)
    {
        string cmd = argv.Length > 0 ? argv[0] : "";
        Gui = cmd == "launch";
        try
        {
            Dictionary<string, string> o;
            List<string> extra;
            ParseArgs(argv, out o, out extra);
            switch (cmd)
            {
                case "launch":
                    return Launch(Req(o, "profile"), Req(o, "aumid"), Opt(o, "name", "Claude"), Opt(o, "icon", null), extra);
                case "resolve-exe":
                    Console.WriteLine(ResolveClaudeExe(o.ContainsKey("refresh")));
                    return 0;
                case "make-icon":
                    return MakeIcon(Req(o, "source"), Req(o, "out"), Req(o, "hex"));
                case "make-shortcut":
                    MakeShortcut(Req(o, "lnk"), Req(o, "profile"), Req(o, "aumid"), Req(o, "name"), Opt(o, "icon", null));
                    return 0;
                case "read-aumid":
                    Console.WriteLine(ReadShortcutAumid(Req(o, "lnk")) ?? "");
                    return 0;
                case "probe":
                    return Probe(Req(o, "profile"));
                case "diag-tag":
                    return DiagTag(Opt(o, "hwnd", null));
                default:
                    Console.Error.WriteLine(
                        "usage: launch --profile DIR --aumid ID [--name NAME] [--icon ICO] [-- ARGS...]\n" +
                        "       resolve-exe [--refresh]\n" +
                        "       make-icon --source EXE|ICO|PNG --out ICO --hex RRGGBB\n" +
                        "       make-shortcut --lnk LNK --profile DIR --aumid ID --name NAME [--icon ICO]\n" +
                        "       read-aumid --lnk LNK\n" +
                        "       probe --profile DIR");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Fail(e.Message);
            return 1;
        }
    }

    // ---------------------------------------------------------------- launch

    // Starts the official Claude.exe on its own profile, then stays resident
    // (one watcher per clone) tagging the clone's windows with the clone's
    // AppUserModelID, so the taskbar gives it its own button and icon instead
    // of grouping it under the main Claude.
    static int Launch(string profile, string aumid, string display, string icon, List<string> extra)
    {
        profile = Path.GetFullPath(profile).TrimEnd('\\');
        Directory.CreateDirectory(profile);
        string exe = ResolveClaudeExe(false);

        StringBuilder args = new StringBuilder(Quote("--user-data-dir=" + profile));
        foreach (string a in extra) args.Append(' ').Append(Quote(a));

        ProcessStartInfo psi = new ProcessStartInfo(exe, args.ToString());
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetDirectoryName(exe);
        // Same pairing as the macOS launcher: the env var moves Electron's own
        // paths, --user-data-dir moves Chromium's (IndexedDB, Local Storage...).
        psi.EnvironmentVariables["CLAUDE_USER_DATA_DIR"] = profile;
        Process.Start(psi);
        Log("launched " + exe + " " + args);

        bool owner;
        using (Mutex m = new Mutex(true, "Local\\claude-alt-watch-" + aumid, out owner))
        {
            if (!owner) return 0;   // a watcher for this clone is already running
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string relaunch = Quote(self) + " launch --profile " + Quote(profile) + " --aumid " + Quote(aumid) +
                              " --name " + Quote(display) + (icon != null ? " --icon " + Quote(icon) : "");
            // Claude is already up: a watcher failure only costs the taskbar
            // icon, so it is logged rather than shown.
            try { Watch(profile, aumid, display, icon, relaunch); }
            catch (Exception e) { Log("watcher stopped: " + e.Message); }
        }
        return 0;
    }

    static void Watch(string profile, string aumid, string display, string icon, string relaunch)
    {
        HashSet<IntPtr> tagged = new HashSet<IntPtr>();
        DateTime start = DateTime.UtcNow;
        int misses = 0;
        while (true)
        {
            List<int> pids = BrowserPids(profile);
            if (pids.Count == 0)
            {
                // give Claude a minute to come up, then leave once it has quit
                if ((DateTime.UtcNow - start).TotalSeconds > 60 && ++misses >= 3) return;
            }
            else
            {
                misses = 0;
                foreach (IntPtr h in TopWindows(pids, false))
                    if (!tagged.Contains(h) && TagWindow(h, aumid, display, icon, relaunch)) tagged.Add(h);
                tagged.RemoveWhere(delegate(IntPtr w) { return !IsWindow(w); });
            }
            Thread.Sleep(1500);
        }
    }

    static bool TagWindow(IntPtr hwnd, string aumid, string display, string icon, string relaunch)
    {
        IPropertyStore ps;
        Guid iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out ps) != 0 || ps == null) return false;
        try
        {
            // relaunch properties first: they only take effect alongside the ID
            if (icon != null) SetString(ps, PKEY_RelaunchIconResource, icon + ",0");
            SetString(ps, PKEY_RelaunchDisplayNameResource, display);
            SetString(ps, PKEY_RelaunchCommand, relaunch);
            SetString(ps, PKEY_AppUserModelId, aumid);
            return true;
        }
        catch (Exception e)
        {
            Log("tagging window " + hwnd + " failed: " + e.Message);
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(ps);
        }
    }

    // Browser (top-level) processes of the clone: Claude.exe started with this
    // profile's --user-data-dir. Chromium child processes carry --type=.
    static List<int> BrowserPids(string profile)
    {
        List<int> pids = new List<int>();
        using (ManagementObjectSearcher s = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'Claude.exe'"))
        {
            foreach (ManagementObject mo in s.Get())
            {
                string cl = mo["CommandLine"] as string;
                if (cl == null || cl.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (MatchesProfile(cl, profile)) pids.Add(Convert.ToInt32(mo["ProcessId"]));
                mo.Dispose();
            }
        }
        return pids;
    }

    // Kept identical to Test-ProfileArg in claude-alt.ps1.
    static bool MatchesProfile(string cmdline, string profile)
    {
        string pat = "--user-data-dir=\"?" + Regex.Escape(profile.TrimEnd('\\')) + "\\\\?(?=[\"\\s]|$)";
        return Regex.IsMatch(cmdline, pat, RegexOptions.IgnoreCase);
    }

    static List<IntPtr> TopWindows(List<int> pids, bool visibleOnly)
    {
        List<IntPtr> found = new List<IntPtr>();
        HashSet<int> set = new HashSet<int>(pids);
        EnumWindowsProc cb = delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (set.Contains((int)pid) && (!visibleOnly || IsWindowVisible(h))) found.Add(h);
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return found;
    }

    // ------------------------------------------------------------ resolve exe

    static string StateDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-alt"); }
    }

    // The path is looked up, never stored in a shortcut: the MSIX install lives
    // in a versioned WindowsApps folder that changes on every Claude update.
    static string ResolveClaudeExe(bool refresh)
    {
        string env = Environment.GetEnvironmentVariable("CLAUDE_ALT_EXE");
        if (!string.IsNullOrEmpty(env))
        {
            if (File.Exists(env)) return env;
            throw new Exception("CLAUDE_ALT_EXE points to a missing file: " + env);
        }
        string cache = Path.Combine(StateDir, "claude-exe.txt");
        if (!refresh && File.Exists(cache))
        {
            string c = File.ReadAllText(cache).Trim();
            if (c.Length > 0 && File.Exists(c)) return c;
        }
        string found = FromAppx();
        if (found == null)
        {
            string squirrel = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                           "AnthropicClaude", "claude.exe");
            if (File.Exists(squirrel)) found = squirrel;
        }
        if (found == null)
            throw new Exception("Claude Desktop not found: no MSIX package with app\\Claude.exe and no " +
                                "%LOCALAPPDATA%\\AnthropicClaude\\claude.exe. Install Claude Desktop, or set " +
                                "CLAUDE_ALT_EXE to its Claude.exe.");
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(cache, found);
        return found;
    }

    static string FromAppx()
    {
        const string script =
            "$p = @(Get-AppxPackage -Name Claude) + @(Get-AppxPackage -Name '*Claude*') | " +
            "Where-Object { $_.InstallLocation -and (Test-Path (Join-Path $_.InstallLocation 'app\\Claude.exe')) } | " +
            "Sort-Object Version -Descending | Select-Object -First 1; " +
            "if ($p) { Join-Path $p.InstallLocation 'app\\Claude.exe' }";
        ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        using (Process p = Process.Start(psi))
        {
            string outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(30000);
            return outp.Length > 0 && File.Exists(outp) ? outp : null;
        }
    }

    // ------------------------------------------------------------------ icon

    // Same remap as the macOS claude-alt: shift hue/sat/val of the warm
    // saturated field only, leaving the cream starburst alone.
    static int MakeIcon(string source, string outPath, string hex)
    {
        using (Bitmap bmp = LoadSource(source))
        {
            int changed = Recolor(bmp, BaseHex, hex.TrimStart('#'));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            WriteIco(outPath, bmp, new int[] { 256, 64, 48, 32, 24, 16 });
            Console.WriteLine("recolored " + changed + " px");
            if (changed == 0) Console.WriteLine("warning: no clay-orange pixels found; icon left as the source");
        }
        return 0;
    }

    static Bitmap LoadSource(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        Image img = null;
        if (ext == ".png" || ext == ".bmp")
        {
            img = Image.FromFile(path);
        }
        else if (ext == ".ico")
        {
            using (Icon ic = new Icon(path, 256, 256)) img = ic.ToBitmap();
        }
        else
        {
            IntPtr[] h = new IntPtr[1];
            uint[] ids = new uint[1];
            if (PrivateExtractIcons(path, 0, 256, 256, h, ids, 1, 0) > 0 && h[0] != IntPtr.Zero)
            {
                using (Icon ic = Icon.FromHandle(h[0])) img = ic.ToBitmap();
                DestroyIcon(h[0]);
            }
            else
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(path)) img = ic.ToBitmap();
            }
        }
        using (img) return Resize(img, 256);
    }

    static Bitmap Resize(Image img, int size)
    {
        Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(img, 0, 0, size, size);
        }
        return b;
    }

    static int Recolor(Bitmap bmp, string baseHex, string targetHex)
    {
        double bh, bs, bv, th, ts, tv;
        double[] bc = HexRgb(baseHex), tc = HexRgb(targetHex);
        RgbToHsv(bc[0], bc[1], bc[2], out bh, out bs, out bv);
        RgbToHsv(tc[0], tc[1], tc[2], out th, out ts, out tv);
        double sGain = bs > 0 ? ts / bs : 1.0, vGain = bv > 0 ? tv / bv : 1.0;
        const double S_LO = 0.20, S_HI = 0.40;

        Rectangle r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData d = bmp.LockBits(r, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] px = new byte[d.Stride * d.Height];
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        int changed = 0;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i + 3] == 0) continue;
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, rr = px[i + 2] / 255.0, hh, ss, vv;
            RgbToHsv(rr, g, b, out hh, out ss, out vv);
            if (!(ss > S_LO && (hh < 0.12 || hh > 0.92))) continue;
            double m = ss >= S_HI ? 1.0 : (ss - S_LO) / (S_HI - S_LO);
            double nr, ng, nb;
            HsvToRgb(Mod1(th + (hh - bh)), Math.Min(1.0, ss * sGain), Math.Min(1.0, vv * vGain), out nr, out ng, out nb);
            px[i + 2] = Clamp(px[i + 2] + (nr * 255 - px[i + 2]) * m);
            px[i + 1] = Clamp(px[i + 1] + (ng * 255 - px[i + 1]) * m);
            px[i] = Clamp(px[i] + (nb * 255 - px[i]) * m);
            changed++;
        }
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        return changed;
    }

    // ICO with one PNG-compressed entry per size (supported since Windows Vista).
    static void WriteIco(string path, Bitmap src, int[] sizes)
    {
        List<byte[]> pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (Bitmap b = Resize(src, s))
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)pngs[i].Length); w.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (byte[] p in pngs) w.Write(p);
        }
    }

    static double[] HexRgb(string hex)
    {
        return new double[] {
            Convert.ToInt32(hex.Substring(0, 2), 16) / 255.0,
            Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0,
            Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0 };
    }

    // Python colorsys semantics (h in [0,1)), so both tools recolor identically.
    static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        v = max;
        if (max == min) { h = 0; s = 0; return; }
        s = (max - min) / max;
        double rc = (max - r) / (max - min), gc = (max - g) / (max - min), bc = (max - b) / (max - min);
        if (r == max) h = bc - gc;
        else if (g == max) h = 2.0 + rc - bc;
        else h = 4.0 + gc - rc;
        h = Mod1(h / 6.0);
    }

    static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        if (s == 0) { r = g = b = v; return; }
        int i = (int)(h * 6.0);
        double f = h * 6.0 - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }

    static double Mod1(double x) { return ((x % 1.0) + 1.0) % 1.0; }
    static byte Clamp(double x) { return (byte)Math.Max(0, Math.Min(255, Math.Round(x))); }

    // -------------------------------------------------------------- shortcut

    // A Start-menu shortcut whose AppUserModelID matches the one the watcher
    // stamps on the clone's windows: the taskbar then shows this shortcut's
    // name and icon for the clone, and pinning it pins the clone.
    static void MakeShortcut(string lnk, string profile, string aumid, string display, string icon)
    {
        string launcher = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                                       "claude-alt-launcher.exe");
        string args = "launch --profile " + Quote(profile.TrimEnd('\\')) + " --aumid " + Quote(aumid) +
                      " --name " + Quote(display) + (icon != null ? " --icon " + Quote(icon) : "");
        IShellLinkW link = (IShellLinkW)new CShellLink();
        link.SetPath(launcher);
        link.SetArguments(args);
        link.SetWorkingDirectory(Path.GetDirectoryName(launcher));
        link.SetDescription(display);
        if (icon != null) link.SetIconLocation(icon, 0);
        IPropertyStore ps = (IPropertyStore)link;
        SetString(ps, PKEY_AppUserModelId, aumid);
        ps.Commit();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lnk)));
        ((IPersistFile)link).Save(lnk, true);
        Marshal.ReleaseComObject(link);
    }

    static string ReadShortcutAumid(string lnk)
    {
        IShellLinkW link = (IShellLinkW)new CShellLink();
        ((IPersistFile)link).Load(lnk, 0);
        string id = GetString((IPropertyStore)link, PKEY_AppUserModelId);
        Marshal.ReleaseComObject(link);
        return id;
    }

    // ----------------------------------------------------------------- probe

    // For claude-alt.ps1 doctor: the clone's browser pids and, per visible
    // window, the AppUserModelID it carries ("-" when none).
    static int Probe(string profile)
    {
        List<int> pids = BrowserPids(profile);
        foreach (int p in pids) Console.WriteLine("pid\t" + p);
        foreach (IntPtr h in TopWindows(pids, true))
        {
            string id = null;
            IPropertyStore ps;
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out ps) == 0 && ps != null)
            {
                try { id = GetString(ps, PKEY_AppUserModelId); }
                finally { Marshal.ReleaseComObject(ps); }
            }
            StringBuilder title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            Console.WriteLine("window\t0x" + h.ToString("x") + "\t" + (string.IsNullOrEmpty(id) ? "-" : id) + "\t" + title);
        }
        return pids.Count > 0 ? 0 : 3;
    }

    // Temporary diagnostic: which AppUserModel keys a window store accepts,
    // for a window of this process and (with --hwnd) another process's.
    static int DiagTag(string hwndHex)
    {
        List<IntPtr> targets = new List<IntPtr>();
        targets.Add(CreateWindowExW(0, "STATIC", "diag", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        if (hwndHex != null) targets.Add(new IntPtr(Convert.ToInt64(hwndHex.Replace("0x", ""), 16)));
        PropertyKey[] keys = { PKEY_AppUserModelId, PKEY_RelaunchCommand, PKEY_RelaunchDisplayNameResource, PKEY_RelaunchIconResource };
        string[] names = { "ID", "RelaunchCommand", "RelaunchDisplayName", "RelaunchIcon" };
        foreach (IntPtr h in targets)
        {
            IPropertyStore ps;
            Guid iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreForWindow(h, ref iid, out ps);
            Console.WriteLine("hwnd 0x" + h.ToString("x") + " store hr=0x" + hr.ToString("x"));
            if (hr != 0) continue;
            for (int i = 0; i < keys.Length; i++)
            {
                try { SetString(ps, keys[i], "diag.value"); Console.WriteLine("  set " + names[i] + " ok, read back " + GetString(ps, keys[i])); }
                catch (Exception e) { Console.WriteLine("  set " + names[i] + " FAILED " + e.Message); }
            }
            Marshal.ReleaseComObject(ps);
        }
        return 0;
    }

    // --------------------------------------------------------------- helpers

    static void ParseArgs(string[] argv, out Dictionary<string, string> o, out List<string> extra)
    {
        o = new Dictionary<string, string>();
        extra = new List<string>();
        for (int i = 1; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a == "--") { for (i++; i < argv.Length; i++) extra.Add(argv[i]); break; }
            if (!a.StartsWith("--")) throw new Exception("unexpected argument: " + a);
            string key = a.Substring(2);
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) o[key] = argv[++i];
            else o[key] = "";
        }
    }

    static string Req(Dictionary<string, string> o, string k)
    {
        string v;
        if (!o.TryGetValue(k, out v) || v.Length == 0) throw new Exception("missing --" + k);
        return v;
    }

    static string Opt(Dictionary<string, string> o, string k, string def)
    {
        string v;
        return o.TryGetValue(k, out v) && v.Length > 0 ? v : def;
    }

    // CommandLineToArgvW quoting rules.
    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new char[] { ' ', '\t', '"' }) < 0) return a;
        StringBuilder sb = new StringBuilder("\"");
        int bs = 0;
        foreach (char c in a)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(c);
            bs = 0;
        }
        return sb.Append('\\', bs * 2).Append('"').ToString();
    }

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.AppendAllText(Path.Combine(StateDir, "launcher.log"),
                DateTime.Now.ToString("s") + " [" + Process.GetCurrentProcess().Id + "] " + msg + Environment.NewLine);
        }
        catch { }
    }

    static void Fail(string msg)
    {
        Log("error: " + msg);
        Console.Error.WriteLine("claude-alt: " + msg);
        if (Gui) MessageBoxW(IntPtr.Zero, msg, "claude-alt", 0x10);
    }

    // ----------------------------------------------------- property stores

    static readonly Guid AppUserModelFmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    static readonly PropertyKey PKEY_RelaunchCommand = new PropertyKey(AppUserModelFmtid, 2);
    static readonly PropertyKey PKEY_RelaunchIconResource = new PropertyKey(AppUserModelFmtid, 3);
    static readonly PropertyKey PKEY_RelaunchDisplayNameResource = new PropertyKey(AppUserModelFmtid, 4);
    static readonly PropertyKey PKEY_AppUserModelId = new PropertyKey(AppUserModelFmtid, 5);

    const ushort VT_LPWSTR = 31;

    static void SetString(IPropertyStore ps, PropertyKey key, string value)
    {
        PropVariant pv = new PropVariant();
        pv.vt = VT_LPWSTR;
        pv.p = Marshal.StringToCoTaskMemUni(value);
        try { ps.SetValue(ref key, ref pv); }
        finally { PropVariantClear(ref pv); }
    }

    static string GetString(IPropertyStore ps, PropertyKey key)
    {
        PropVariant pv;
        ps.GetValue(ref key, out pv);
        try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
    }

    // PROPVARIANT: vt at 0, the value union at 8; sized for x64 (24 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    // ------------------------------------------------------------- P/Invoke

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ps);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
}
