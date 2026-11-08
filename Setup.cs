// MiniDAW-Setup.exe: installs MiniDAW for the current Windows user (no admin rights needed), with a Start-menu shortcut,
// an optional desktop shortcut, and an entry in Settings → Apps to uninstall it. release.ps1 embeds MiniDAW.exe, README.md
// and banana.png in it.
//   MiniDAW-Setup.exe                    the install window
//   MiniDAW-Setup.exe /update            used by MiniDAW's Update button: waits for it to close, installs quietly, starts it again
//   MiniDAW-Setup.exe /uninstall [/quiet] what Settings → Apps runs (the installer keeps a copy of itself in the install folder)
// Build: csc /target:winexe /win32icon:banana.ico /resource:MiniDAW.exe,MiniDAW.exe /resource:README.md,README.md
//            /resource:banana.png,banana.png /out:MiniDAW-Setup.exe Setup.cs
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    // Not readonly: the installer test points them at a temporary folder and registry key.
    static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MiniDAW");
    static string StartLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MiniDAW.lnk");
    static string DeskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "MiniDAW.lnk");
    static string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MiniDAW";
    public static string Exe { get { return Path.Combine(Dir, "MiniDAW.exe"); } }
    public static string Folder { get { return Dir; } }

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            if (mode == "/uninstall") return Uninstall(args.Contains("/quiet"));
            if (mode == "/update")
            {
                for (int i = 0; i < 300 && Running(); i++) Thread.Sleep(100);   // MiniDAW starts us just before it closes
                Install(File.Exists(DeskLnk));
                Process.Start(Exe);
                return 0;
            }
            Application.EnableVisualStyles();
            Application.Run(new SetupForm());
            return 0;
        }
        catch (Exception e)   // anything left: say what failed instead of vanishing (a half-copied exe is never left: see Put)
        {
            MessageBox.Show("MiniDAW setup failed:\n" + e.Message, "MiniDAW setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static string version;
    public static string Version()   // the embedded MiniDAW.exe's version, e.g. "1.1" (read once: an assembly loads only once per process)
    {
        if (version != null) return version;
        using (var s = Res("MiniDAW.exe")) using (var m = new MemoryStream())
        {
            s.CopyTo(m);
            var v = Assembly.ReflectionOnlyLoad(m.ToArray()).GetName().Version;
            return version = v.Build > 0 ? v.ToString(3) : v.ToString(2);
        }
    }

    public static bool Running()   // is the installed copy open? (its file can't be replaced then)
    {
        return Process.GetProcessesByName("MiniDAW").Any(p =>
        {
            try { return string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase); }
            catch (System.ComponentModel.Win32Exception) { return false; }   // another user's process
            catch (InvalidOperationException) { return false; }             // it just exited
        });
    }

    public static bool Installed() { return File.Exists(Exe); }

    public static void Install(bool desktop)
    {
        Directory.CreateDirectory(Dir);
        Put("MiniDAW.exe", Exe);
        Put("README.md", Path.Combine(Dir, "README.md"));
        string self = Assembly.GetExecutingAssembly().Location, keep = Path.Combine(Dir, "MiniDAW-Setup.exe");   // kept as the uninstaller
        if (!string.Equals(self, keep, StringComparison.OrdinalIgnoreCase)) File.Copy(self, keep, true);
        Shortcut(StartLnk);
        if (desktop) Shortcut(DeskLnk);
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "MiniDAW");
            k.SetValue("DisplayVersion", Version());
            k.SetValue("Publisher", "MiniDAW");
            k.SetValue("DisplayIcon", Exe);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + keep + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + keep + "\" /uninstall /quiet");
            k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024));
        }
    }

    static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove MiniDAW from this computer?\n\nYour projects and WAV files are not touched.", "MiniDAW", MessageBoxButtons.OKCancel) != DialogResult.OK) return 1;
        if (Running()) { if (!quiet) MessageBox.Show("Close MiniDAW first, then try again.", "MiniDAW"); return 1; }
        Remove();
        if (!quiet) MessageBox.Show("MiniDAW was removed.", "MiniDAW");
        return 0;
    }

    public static void Remove()
    {
        foreach (var f in new[] { StartLnk, DeskLnk, Exe, Path.Combine(Dir, "README.md") }) if (File.Exists(f)) File.Delete(f);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        // This uninstaller runs from the install folder, so a hidden cmd deletes it once we've exited. rmdir without /s only
        // removes the folder if nothing else is left in it.
        string keep = Path.Combine(Dir, "MiniDAW-Setup.exe");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /q \"" + keep + "\" & rmdir \"" + Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    static Stream Res(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }

    static void Put(string resource, string path)   // write next to the target, then swap, so a failed copy never leaves a broken file
    {
        string tmp = path + ".new";
        using (var s = Res(resource)) using (var d = File.Create(tmp)) s.CopyTo(d);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    static void Shortcut(string lnk)   // through the WScript.Shell COM object that ships with Windows
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        object s = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        foreach (var kv in new[] { new[] { "TargetPath", Exe }, new[] { "WorkingDirectory", Dir }, new[] { "IconLocation", Exe + ",0" }, new[] { "Description", "MiniDAW music studio" } })
            s.GetType().InvokeMember(kv[0], BindingFlags.SetProperty, null, s, new object[] { kv[1] });
        s.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
    }

    public static Image Logo() { using (var s = Res("banana.png")) using (var i = Image.FromStream(s)) return new Bitmap(i); }
}

class SetupForm : Form
{
    static readonly Color Bg = Color.FromArgb(28, 32, 38), Txt = Color.FromArgb(230, 233, 238), Dim = Color.FromArgb(138, 147, 163),
        Raised = Color.FromArgb(44, 50, 61), Line = Color.FromArgb(58, 66, 79), On = Color.FromArgb(255, 159, 58);
    readonly Image logo = Setup.Logo();
    readonly Font big = new Font("Segoe UI", 20, FontStyle.Bold);
    readonly CheckBox desk = new CheckBox { Text = "Add a desktop shortcut", Checked = true, AutoSize = true, Location = new Point(132, 128) };
    readonly Button go, cancel;
    readonly string version = Setup.Version();
    string status;
    bool done;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public SetupForm()
    {
        Text = "MiniDAW " + version + " setup"; ClientSize = new Size(500, 210); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true;
        BackColor = Bg; ForeColor = Txt; Font = new Font("Segoe UI", 9);
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        status = Setup.Installed() ? "MiniDAW is already installed here; this replaces it with version " + version + "." : "No admin rights needed. Your projects are never touched.";
        Controls.Add(desk);
        go = Btn(Setup.Installed() ? "Reinstall" : "Install", 300, true); cancel = Btn("Cancel", 400, false);
        go.Click += (s, e) => Go(); cancel.Click += (s, e) => Close();
        AcceptButton = go; CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)   // dark title bar on Windows 10 20H1+ and 11
    {
        base.OnHandleCreated(e); int on = 1;
        try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }

    Button Btn(string text, int x, bool accent)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, 166, 86, 30), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = accent ? On : Raised, ForeColor = accent ? Color.FromArgb(28, 22, 14) : Txt };
        b.FlatAppearance.BorderColor = accent ? On : Line;
        if (accent) b.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        Controls.Add(b); return b;
    }

    void Go()
    {
        if (done) { Process.Start(Setup.Exe); Close(); return; }
        if (Setup.Running()) { status = "MiniDAW is open: close it, then click " + go.Text + " again."; Invalidate(); return; }
        go.Enabled = cancel.Enabled = desk.Enabled = false; status = "Installing…"; Refresh();
        try { Setup.Install(desk.Checked); }
        catch (IOException e) { Failed(e); return; }
        catch (UnauthorizedAccessException e) { Failed(e); return; }
        done = true; status = "Installed. Find it in the Start menu" + (desk.Checked ? " and on your desktop." : ".");
        go.Text = "Start"; cancel.Text = "Close"; go.Enabled = cancel.Enabled = true; desk.Visible = false; Invalidate();
    }

    void Failed(Exception e) { status = "Couldn't install: " + e.Message; go.Enabled = cancel.Enabled = desk.Enabled = true; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(On)) g.FillRectangle(b, 0, 0, ClientSize.Width, 3);
        g.DrawImage(logo, 20, 24, 96, 96);
        using (var t = new SolidBrush(Txt)) g.DrawString("MiniDAW " + version, big, t, 126, 22);
        using (var d = new SolidBrush(Dim))
        {
            g.DrawString("Installs to " + Setup.Folder, Font, d, new RectangleF(130, 66, 360, 34));
            g.DrawString(status, Font, done ? new SolidBrush(Color.FromArgb(110, 220, 140)) : d, new RectangleF(130, 98, 360, 30));
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) { logo.Dispose(); big.Dispose(); } base.Dispose(disposing); }
}
