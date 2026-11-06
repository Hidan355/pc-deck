// Instalacao por usuario (sem administrador): %LOCALAPPDATA%\Programs\PC Deck

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PCDeck {

static class Setup {
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCDeck";
    const string RunName = "PC Deck";

    static string Programs { get { return Environment.GetFolderPath(Environment.SpecialFolder.Programs); } }
    static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }

    public static bool IsInstalledCopy() {
        try { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool IsInstalled { get { return File.Exists(Paths.InstalledExe); } }

    public static bool AutoStart {
        get {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }
        set {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
                if (value) k.SetValue(RunName, "\"" + Paths.InstalledExe + "\" --background");
                else k.DeleteValue(RunName, false);
            }
        }
    }

    public static bool HasDesktopShortcut { get { return File.Exists(Path.Combine(Desktop, "PC Deck.lnk")); } }

    public static void Install(bool autoStart, bool desktop) {
        bool update = IsInstalled;
        StopRunning();

        Directory.CreateDirectory(Paths.InstallDir);
        File.Copy(Application.ExecutablePath, Paths.InstalledExe, true);
        // botoes e token ficam em %APPDATA%: uma atualizacao nao mexe neles
        Directory.CreateDirectory(Paths.Data);
        if (!File.Exists(Paths.Config)) File.WriteAllBytes(Paths.Config, Res.Bytes("default-config.json"));

        Shortcut(Path.Combine(Programs, "PC Deck.lnk"));
        string deskLnk = Path.Combine(Desktop, "PC Deck.lnk");
        if (desktop) Shortcut(deskLnk); else TryDelete(deskLnk);

        AutoStart = autoStart;

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey)) {
            k.SetValue("DisplayName", "PC Deck");
            k.SetValue("DisplayVersion", Program.Version);
            k.SetValue("Publisher", "PC Deck");
            k.SetValue("DisplayIcon", Paths.InstalledExe);
            k.SetValue("InstallLocation", Paths.InstallDir);
            k.SetValue("UninstallString", "\"" + Paths.InstalledExe + "\" --uninstall");
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(Paths.InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        // QR code so na primeira instalacao; numa atualizacao o celular ja esta conectado
        Process.Start(Paths.InstalledExe, update ? "--background" : "--welcome");
    }

    public static void Uninstall() {
        if (MessageBox.Show("Desinstalar o PC Deck deste computador?", "PC Deck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        StopRunning();
        AutoStart = false;
        TryDelete(Path.Combine(Programs, "PC Deck.lnk"));
        TryDelete(Path.Combine(Desktop, "PC Deck.lnk"));
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        bool wipe = MessageBox.Show("Apagar também seus botões e configurações?\n\nSe escolher Não, eles voltam quando você instalar de novo.",
            "PC Deck", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        // o exe nao consegue apagar a si mesmo: o cmd apaga a pasta depois que ele fecha
        string cmd = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Paths.InstallDir + "\"";
        if (wipe) cmd += " & rmdir /s /q \"" + Paths.Data + "\"";
        Hidden("cmd.exe", cmd, false);

        MessageBox.Show("PC Deck removido.", "PC Deck", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    static void StopRunning() {
        Program.Signal(Names.Quit);
        for (int i = 0; i < 25; i++) {
            try { Mutex.OpenExisting(Names.Mutex).Dispose(); } catch (WaitHandleCannotBeOpenedException) { break; } catch { break; }
            Thread.Sleep(200);
        }
        int me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("PCDeck")) {
            if (p.Id != me) try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    static void Shortcut(string lnk) {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic s = shell.CreateShortcut(lnk);
        s.TargetPath = Paths.InstalledExe;
        s.WorkingDirectory = Paths.InstallDir;
        s.IconLocation = Paths.InstalledExe + ",0";
        s.Description = "Painel de botões no celular para controlar o PC";
        s.Save();
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static void Hidden(string exe, string args, bool wait = true) {
        try {
            var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (wait) p.WaitForExit(5000);
        } catch { }
    }
}

class InstallerForm : Form {
    readonly CheckBox chkAuto, chkDesk;
    readonly Button btnInstall, btnCancel;
    readonly Label status;

    public InstallerForm() {
        bool update = Setup.IsInstalled;
        Text = update ? "Atualizar PC Deck" : "Instalar PC Deck";
        Icon = Res.AppIcon(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 330);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.5F);

        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Color.FromArgb(18, 22, 31) };
        var logo = new PictureBox { Image = Res.AppIcon(new Size(64, 64)).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(24, 20, 56, 56), BackColor = Color.Transparent };
        var title = new Label { Text = "PC Deck", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18F), AutoSize = true, Location = new Point(92, 20), BackColor = Color.Transparent };
        var ver = new Label { Text = "versão " + Program.Version, ForeColor = Color.FromArgb(138, 147, 166), AutoSize = true, Location = new Point(96, 56), BackColor = Color.Transparent };
        header.Controls.AddRange(new Control[] { logo, title, ver });

        var desc = new Label {
            Text = update
                ? "Uma versão do PC Deck já está instalada. Ela será atualizada e seus botões serão mantidos."
                : "Transforma seu celular num painel de botões para controlar o PC: mídia, volume, apps, atalhos e macros.",
            Location = new Point(24, 112), Size = new Size(452, 44)
        };

        chkAuto = new CheckBox { Text = "Iniciar junto com o Windows", Checked = !update || Setup.AutoStart, AutoSize = true, Location = new Point(26, 166) };
        chkDesk = new CheckBox { Text = "Criar atalho na área de trabalho", Checked = !update || Setup.HasDesktopShortcut, AutoSize = true, Location = new Point(26, 194) };

        var where = new Label {
            Text = "Instala só para o seu usuário, sem precisar de administrador:\n" + Paths.InstallDir,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5F), Location = new Point(24, 228), Size = new Size(452, 36)
        };

        status = new Label { ForeColor = Color.FromArgb(92, 62, 230), AutoSize = true, Location = new Point(24, 290) };
        btnCancel = new Button { Text = "Cancelar", Size = new Size(96, 32), Location = new Point(380, 282), DialogResult = DialogResult.Cancel };
        btnInstall = new Button {
            Text = update ? "Atualizar" : "Instalar", Size = new Size(110, 32), Location = new Point(262, 282),
            BackColor = Color.FromArgb(124, 92, 255), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 9.5F)
        };
        btnInstall.FlatAppearance.BorderSize = 0;
        btnInstall.Click += (s, e) => DoInstall();

        Controls.AddRange(new Control[] { header, desc, chkAuto, chkDesk, where, status, btnInstall, btnCancel });
        AcceptButton = btnInstall;
        CancelButton = btnCancel;
    }

    void DoInstall() {
        btnInstall.Enabled = btnCancel.Enabled = false;
        status.Text = "Instalando...";
        Refresh();
        try {
            Setup.Install(chkAuto.Checked, chkDesk.Checked);
            Close();
        } catch (Exception ex) {
            status.Text = "";
            MessageBox.Show("Não foi possível instalar:\n" + ex.Message, "PC Deck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            btnInstall.Enabled = btnCancel.Enabled = true;
        }
    }
}

}
