// Stand-in for Claude.exe in CI: records how the launcher started it, creates
// browser storage in its --user-data-dir like Chromium would, and shows a window.
using System;
using System.IO;
using System.Windows.Forms;

static class FakeClaude
{
    [STAThread]
    static void Main(string[] args)
    {
        string outp = Environment.GetEnvironmentVariable("FAKE_CLAUDE_OUT");
        if (!string.IsNullOrEmpty(outp))
            File.AppendAllText(outp, Environment.CommandLine + Environment.NewLine +
                "CLAUDE_USER_DATA_DIR=" + Environment.GetEnvironmentVariable("CLAUDE_USER_DATA_DIR") + Environment.NewLine);
        foreach (string a in args)
            if (a.StartsWith("--user-data-dir=")) Directory.CreateDirectory(Path.Combine(a.Substring(16), "IndexedDB"));

        Form f = new Form();
        f.Text = "Fake Claude";
        Timer t = new Timer();
        t.Interval = 120000;
        t.Tick += delegate { f.Close(); };
        t.Start();
        Application.Run(f);
    }
}
