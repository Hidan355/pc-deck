// PC Deck - app de bandeja: servidor do painel do celular + instalador.
// Compilado com o csc do .NET Framework (C# 5): build.ps1

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PC Deck")]
[assembly: AssemblyProduct("PC Deck")]
[assembly: AssemblyDescription("Painel estilo Stream Deck no celular")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace PCDeck {

static class Program {
    public const string Version = "1.1.0";

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args) {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (mode == "--uninstall") { Setup.Uninstall(); return; }
        if (mode == "--quit") { Signal(Names.Quit); return; }
        if (!Setup.IsInstalledCopy() && mode != "--portable") {
            if (mode == "--silent") Setup.Install(!Setup.IsInstalled || Setup.AutoStart, !Setup.IsInstalled || Setup.HasDesktopShortcut);
            else Application.Run(new InstallerForm());
            return;
        }

        bool first;
        using (var mutex = new Mutex(true, Names.Mutex, out first)) {
            if (!first) {
                // ja esta rodando: pede para a instancia aberta mostrar o editor / QR
                if (mode != "--background") Signal(mode == "--welcome" ? Names.Welcome : Names.OpenEditor);
                return;
            }
            var app = new TrayApp(mode);
            if (!app.Failed) Application.Run(app);
        }
    }

    public static bool Signal(string name) {
        try {
            using (var h = EventWaitHandle.OpenExisting(name)) h.Set();
            return true;
        } catch { return false; }
    }
}

static class Names {
    public const string Mutex = "Local\\PCDeck.Instance";
    public const string OpenEditor = "Local\\PCDeck.OpenEditor";
    public const string Welcome = "Local\\PCDeck.Welcome";
    public const string Quit = "Local\\PCDeck.Quit";
}

static class Paths {
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PC Deck");
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PC Deck");
    public static readonly string InstalledExe = Path.Combine(InstallDir, "PCDeck.exe");
    public static string Config { get { return Path.Combine(Data, "config.json"); } }
    public static string Backup { get { return Path.Combine(Data, "config.bak.json"); } }
    public static string Token { get { return Path.Combine(Data, "token.txt"); } }
    public static string LogFile { get { return Path.Combine(Data, "log.txt"); } }
    public static string LanguageFile { get { return Path.Combine(Data, "language.txt"); } }
}

// ---------- idioma (pt / en): escolhido no editor; sem escolha, segue o Windows ----------
static class L {
    public static string Current {
        get {
            try {
                if (File.Exists(Paths.LanguageFile)) {
                    string v = File.ReadAllText(Paths.LanguageFile).Trim().ToLowerInvariant();
                    if (v == "pt" || v == "en") return v;
                }
            } catch { }
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";
        }
        set {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.LanguageFile, value == "en" ? "en" : "pt", Encoding.ASCII);
        }
    }

    public static bool En { get { return Current == "en"; } }

    public static string T(string pt, string en) { return En ? en : pt; }
}

static class Log {
    static readonly object sync = new object();

    public static void Init() {
        try {
            Directory.CreateDirectory(Paths.Data);
            var fi = new FileInfo(Paths.LogFile);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
        } catch { }
    }

    public static void Write(string msg) {
        lock (sync) {
            try { File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }
}

static class Res {
    public static byte[] Bytes(string name) {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) {
            if (s == null) throw new FileNotFoundException("Recurso ausente: " + name);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    public static string Text(string name) { return Encoding.UTF8.GetString(Bytes(name)); }

    public static Icon AppIcon(Size size) { return new Icon(new MemoryStream(Bytes("deck.ico")), size); }

    // botoes padrao de uma instalacao nova, no idioma atual
    public static byte[] DefaultConfig() { return Bytes(L.En ? "default-config.en.json" : "default-config.json"); }

    // paginas web com idioma, versao e dados trocados no servidor
    public static string Page(string name, params string[] pairs) {
        string html = Text(name).Replace("{{LANG}}", L.Current).Replace("{{VERSION}}", Program.Version);
        for (int i = 0; i + 1 < pairs.Length; i += 2) html = html.Replace(pairs[i], pairs[i + 1]);
        return html;
    }
}

// ---------- JSON (JavaScriptSerializer devolve Dictionary/object[]) ----------
static class J {
    public static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static object Get(object o, string k) {
        var d = o as IDictionary<string, object>;
        object v;
        return d != null && d.TryGetValue(k, out v) ? v : null;
    }

    public static string Str(object o, string k) {
        var v = Get(o, k);
        return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static int Int(object v, int def) {
        if (v == null) return def;
        try { return (int)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)); } catch { return def; }
    }

    public static object[] Arr(object v) {
        var a = v as object[];
        if (a != null) return a;
        var l = v as ArrayList;
        return l != null ? l.ToArray() : new object[0];
    }

    public static Dictionary<string, object> O(params object[] kv) {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
}

// ---------- "tocando agora" (WinRT via reflexao: o csc do .NET Framework nao referencia o SDK) ----------
class Media {
    static MethodInfo asTask, asStream;
    static Type mgrT, propsT, rasT;
    static bool available;

    readonly object sync = new object();
    object mgr;
    string artKey = "";
    byte[] art;

    static Type WinRT(string name, string asm) { return Type.GetType(name + ", " + asm + ", ContentType=WindowsRuntime", true); }

    static Media() {
        try {
            asTask = typeof(WindowsRuntimeSystemExtensions).GetMethods().First(m =>
                m.Name == "AsTask" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1");
            mgrT = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager", "Windows.Media.Control");
            propsT = WinRT("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties", "Windows.Media.Control");
            rasT = WinRT("Windows.Storage.Streams.IRandomAccessStreamWithContentType", "Windows.Storage.Streams");
            var inT = WinRT("Windows.Storage.Streams.IInputStream", "Windows.Storage.Streams");
            asStream = typeof(WindowsRuntimeStreamExtensions).GetMethod("AsStreamForRead", new[] { inT });
            available = true;
        } catch (Exception ex) { Log.Write("tocando agora indisponivel: " + ex.Message); }
    }

    static object Await(object op, Type t) {
        var task = (Task)asTask.MakeGenericMethod(t).Invoke(null, new[] { op });
        if (!task.Wait(1500)) throw new TimeoutException();
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    static object Prop(object o, string p) { return o.GetType().GetProperty(p).GetValue(o); }
    static object Call(object o, string m) { return o.GetType().GetMethod(m, Type.EmptyTypes).Invoke(o, null); }

    public Dictionary<string, object> Now() {
        if (!available) return null;
        lock (sync) {
            try {
                if (mgr == null) mgr = Await(mgrT.GetMethod("RequestAsync").Invoke(null, null), mgrT);
                var s = Call(mgr, "GetCurrentSession");
                if (s == null) return null;
                var p = Await(Call(s, "TryGetMediaPropertiesAsync"), propsT);
                string title = Convert.ToString(Prop(p, "Title"));
                string artist = Convert.ToString(Prop(p, "Artist"));
                string status = Convert.ToString(Prop(Call(s, "GetPlaybackInfo"), "PlaybackStatus"));
                string key = ((uint)(title + "|" + artist).GetHashCode()).ToString();
                if (key != artKey) {
                    artKey = key;
                    art = null;
                    var th = Prop(p, "Thumbnail");
                    if (th != null) {
                        try {
                            var ras = Await(th.GetType().GetMethod("OpenReadAsync").Invoke(th, null), rasT);
                            using (var st = (Stream)asStream.Invoke(null, new[] { ras })) {
                                var ms = new MemoryStream();
                                st.CopyTo(ms);
                                art = ms.ToArray();
                            }
                        } catch { }
                    }
                }
                return J.O("title", title, "artist", artist, "app", Convert.ToString(Prop(s, "SourceAppUserModelId")),
                           "playing", status == "Playing", "art", art != null ? key : null);
            } catch {
                mgr = null;
                return null;
            }
        }
    }

    public byte[] Art(string key) {
        lock (sync) return key == artKey ? art : null;
    }
}

// ---------- servidor ----------
class DeckServer {
    readonly Control ui;
    readonly Media media = new Media();
    readonly object cfgLock = new object();
    TcpListener listener;
    volatile bool running;
    object cfg;
    string cfgText;
    DateTime cfgStamp;
    List<Dictionary<string, object>> apps;

    public int Port = 8765;
    public string Token;

    public DeckServer(Control ui) { this.ui = ui; }

    public string PhoneUrl { get { return "http://" + LanIp() + ":" + Port + "/?t=" + Token; } }

    public string Start() {
        Directory.CreateDirectory(Paths.Data);
        if (!File.Exists(Paths.Config)) File.WriteAllBytes(Paths.Config, Res.DefaultConfig());
        if (!File.Exists(Paths.Token)) {
            const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
            var rnd = new Random();
            File.WriteAllText(Paths.Token, new string(Enumerable.Range(0, 12).Select(i => chars[rnd.Next(chars.Length)]).ToArray()), Encoding.ASCII);
        }
        Token = File.ReadAllText(Paths.Token).Trim();
        try { Config(); } catch (Exception ex) { return L.T("O arquivo de botões (config.json) está com erro:\n", "The buttons file (config.json) has an error:\n") + ex.Message; }
        Port = J.Int(J.Get(cfg, "port"), 8765);

        listener = new TcpListener(IPAddress.Any, Port);
        try { listener.Start(); } catch (SocketException) {
            return L.T("A porta " + Port + " já está sendo usada por outro programa.\n\nFeche esse programa ou troque \"port\" no config.json (menu do ícone → Abrir pasta dos dados).",
                       "Port " + Port + " is already in use by another program.\n\nClose that program or change \"port\" in config.json (tray menu → Open data folder).");
        }
        running = true;
        DeckSys.Cpu();
        var t = new Thread(Loop) { IsBackground = true };
        t.Start();
        Log.Write("servidor iniciado na porta " + Port);
        return null;
    }

    public void Stop() {
        running = false;
        try { listener.Stop(); } catch { }
    }

    void Loop() {
        while (running) {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { if (!running) return; continue; }
            ThreadPool.QueueUserWorkItem(_ => Serve(c));
        }
    }

    void Serve(TcpClient c) {
        try {
            var req = DeckHttp.Read(c);
            if (req != null) Handle(c, req);
        } catch (Exception ex) {
            Log.Write("erro: " + ex.Message);
            try { Json(c, 500, J.O("ok", false, "error", ex.Message)); } catch { }
        } finally {
            c.Close();
        }
    }

    // ---------- config com recarga automatica ----------
    object Config() {
        lock (cfgLock) {
            var stamp = File.GetLastWriteTimeUtc(Paths.Config);
            if (stamp != cfgStamp) {
                try {
                    var text = File.ReadAllText(Paths.Config, Encoding.UTF8);
                    cfg = J.Js.DeserializeObject(text);
                    cfgText = text;
                } catch (Exception ex) {
                    Log.Write("config.json com erro, mantendo a versao anterior: " + ex.Message);
                    if (cfg == null) throw;
                }
                cfgStamp = stamp;
            }
            return cfg;
        }
    }

    // ---------- respostas ----------
    static void Send(TcpClient c, int code, string type, byte[] body) { DeckHttp.Send(c, code, type, body); }
    static void Text(TcpClient c, int code, string type, string s) { Send(c, code, type, Encoding.UTF8.GetBytes(s)); }
    static void Json(TcpClient c, int code, object o) { Text(c, code, "application/json; charset=utf-8", J.Js.Serialize(o)); }
    static void Html(TcpClient c, string s) { Text(c, 200, "text/html; charset=utf-8", s); }

    void Handle(TcpClient c, DeckRequest req) {
        bool isLocal = req.Remote == "127.0.0.1";
        bool authed = req.H("X-Token") == Token || req.Q("t") == Token;

        switch (req.Path) {
            case "/": Html(c, Res.Page("index.html")); return;
            case "/manifest.json": Send(c, 200, "application/manifest+json", Res.Bytes("manifest.json")); return;
            case "/qr":
                if (!isLocal) { Text(c, 403, "text/plain", "Forbidden"); return; }
                Html(c, Res.Page("qr.html", "{{URL}}", PhoneUrl));
                return;
            case "/editor":
                if (!isLocal) { Text(c, 403, "text/plain", "Forbidden"); return; }
                Html(c, Res.Page("editor.html", "{{TOKEN}}", Token, "{{URL}}", PhoneUrl));
                return;
        }

        // editor: so no proprio PC e com o token no cabecalho (bloqueia sites tentando chamar a API)
        if (req.Path.StartsWith("/api/editor/")) {
            if (!isLocal || req.H("X-Token") != Token) { Json(c, 403, J.O("ok", false, "error", "somente no PC")); return; }
            EditorApi(c, req);
            return;
        }

        if (!authed) { Json(c, 401, J.O("ok", false, "error", "token invalido")); return; }

        switch (req.Path) {
            case "/api/config": {
                Config();
                string text;
                lock (cfgLock) text = cfgText;
                Text(c, 200, "application/json; charset=utf-8", text);
                break;
            }
            case "/api/state": {
                object vol = null, muted = null;
                try { vol = DeckAudio.Get(); muted = DeckAudio.Muted(); } catch { }
                Config();
                Json(c, 200, J.O("ok", true, "cfgv", cfgStamp.Ticks.ToString(), "lang", L.Current, "cpu", DeckSys.Cpu(), "ram", DeckSys.Ram(),
                                 "volume", vol, "muted", muted, "media", media.Now()));
                break;
            }
            case "/api/art": {
                var bytes = media.Art(req.Q("k"));
                if (bytes != null) Send(c, 200, bytes.Length > 0 && bytes[0] == 0x89 ? "image/png" : "image/jpeg", bytes);
                else Text(c, 404, "text/plain", "sem capa");
                break;
            }
            case "/api/press": {
                var body = J.Js.DeserializeObject(req.Body);
                var pages = J.Arr(J.Get(Config(), "pages"));
                int pi = J.Int(J.Get(body, "page"), -1), bi = J.Int(J.Get(body, "button"), -1);
                object page = pi >= 0 && pi < pages.Length ? pages[pi] : null;
                var buttons = J.Arr(J.Get(page, "buttons"));
                object btn = bi >= 0 && bi < buttons.Length ? buttons[bi] : null;
                if (btn == null) { Json(c, 404, J.O("ok", false, "error", "botao nao encontrado")); return; }
                Log.Write("toque: " + J.Str(page, "name") + " > " + J.Str(btn, "label"));
                var actions = J.Get(btn, "actions") != null ? J.Arr(J.Get(btn, "actions")) : (J.Get(btn, "action") != null ? new[] { J.Get(btn, "action") } : new object[0]);
                foreach (var a in actions) Actions.Run(a);
                Json(c, 200, J.O("ok", true));
                break;
            }
            case "/api/media": {
                string cmd = J.Str(J.Js.DeserializeObject(req.Body), "cmd");
                if (cmd != "play_pause" && cmd != "next" && cmd != "prev" && cmd != "stop") { Json(c, 400, J.O("ok", false, "error", "comando invalido")); return; }
                DeckInput.Combo(cmd);
                Json(c, 200, J.O("ok", true));
                break;
            }
            case "/api/volume": {
                var body = J.Js.DeserializeObject(req.Body);
                Actions.Run(J.O("type", "volume", "set", J.Get(body, "set"), "mute", J.Get(body, "mute")));
                Json(c, 200, J.O("ok", true, "volume", DeckAudio.Get(), "muted", DeckAudio.Muted()));
                break;
            }
            default:
                Json(c, 404, J.O("ok", false, "error", "rota desconhecida"));
                break;
        }
    }

    void EditorApi(TcpClient c, DeckRequest req) {
        switch (req.Path) {
            case "/api/editor/save": {
                try { J.Js.DeserializeObject(req.Body); } catch { Json(c, 400, J.O("ok", false, "error", "JSON invalido")); return; }
                lock (cfgLock) {
                    File.Copy(Paths.Config, Paths.Backup, true);
                    File.WriteAllText(Paths.Config, req.Body, new UTF8Encoding(false));
                }
                Config();
                Log.Write("editor: botoes salvos");
                Json(c, 200, J.O("ok", true));
                break;
            }
            case "/api/editor/test": {
                foreach (var a in J.Arr(J.Get(J.Js.DeserializeObject(req.Body), "actions"))) if (a != null) Actions.Run(a);
                Json(c, 200, J.O("ok", true));
                break;
            }
            case "/api/editor/pick": {
                bool folder = req.Q("kind") == "folder";
                string path = (string)ui.Invoke(new Func<string>(() => PickPath(folder)));
                Json(c, 200, J.O("ok", true, "path", path));
                break;
            }
            case "/api/editor/info": {
                Json(c, 200, J.O("ok", true, "net", NetInfo(), "port", Port, "url", PhoneUrl, "host", Environment.MachineName,
                                 "autostart", Setup.AutoStart, "version", Program.Version));
                break;
            }
            case "/api/editor/language": {
                string lang = J.Str(J.Js.DeserializeObject(req.Body), "lang");
                if (lang != "pt" && lang != "en") { Json(c, 400, J.O("ok", false, "error", "lang")); return; }
                L.Current = lang;
                Log.Write("idioma: " + lang);
                Json(c, 200, J.O("ok", true, "lang", lang));
                break;
            }
            case "/api/editor/autostart": {
                Setup.AutoStart = J.Get(J.Js.DeserializeObject(req.Body), "on") as bool? == true;
                Json(c, 200, J.O("ok", true, "autostart", Setup.AutoStart));
                break;
            }
            case "/api/editor/apps": {
                if (apps == null) apps = (List<Dictionary<string, object>>)ui.Invoke(new Func<List<Dictionary<string, object>>>(ListApps));
                Json(c, 200, J.O("ok", true, "apps", apps));
                break;
            }
            default:
                Json(c, 404, J.O("ok", false, "error", "rota desconhecida"));
                break;
        }
    }

    // roda na thread da interface (STA)
    static string PickPath(bool folder) {
        using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1) }) {
            owner.Show();
            owner.Activate();
            if (folder) {
                using (var dlg = new FolderBrowserDialog()) return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
            }
            string filter = L.T("Programas e atalhos|*.exe;*.lnk;*.bat;*.cmd;*.url;*.ps1|Todos os arquivos|*.*",
                                "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.url;*.ps1|All files|*.*");
            using (var dlg = new OpenFileDialog { Filter = filter, DereferenceLinks = false }) {
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
            }
        }
    }

    // apps do menu Iniciar (inclui os da Microsoft Store), igual ao Get-StartApps
    static List<Dictionary<string, object>> ListApps() {
        var list = new List<Dictionary<string, object>>();
        try {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items()) {
                string name = item.Name, id = item.Path;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add(J.O("name", name, "id", id));
            }
        } catch (Exception ex) { Log.Write("lista de apps: " + ex.Message); }
        return list.OrderBy(a => (string)a["name"], StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string LanIp() {
        var net = NetInfo();
        return net != null ? (string)net["ip"] : "127.0.0.1";
    }

    // placa de rede com gateway (a que fala com o roteador): IP, roteador e MAC para a reserva
    static Dictionary<string, object> NetInfo() {
        try {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var gw = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var ua = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gw == null || ua == null) continue;
                bool dhcp = false;
                try { dhcp = props.GetIPv4Properties().IsDhcpEnabled; } catch { }
                string mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                return J.O("ip", ua.Address.ToString(), "gateway", gw.Address.ToString(), "mac", mac, "adapter", ni.Name,
                           "wifi", ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, "dhcp", dhcp);
            }
        } catch { }
        return null;
    }
}

// ---------- acoes dos botoes ----------
static class Actions {
    public static void Run(object a) {
        string type = J.Str(a, "type");
        switch (type) {
            case "keys": DeckInput.Combo(J.Str(a, "keys") ?? ""); break;
            case "media": DeckInput.Combo(J.Str(a, "key") ?? ""); break;
            case "text": DeckInput.Type(J.Str(a, "text") ?? ""); break;
            case "wait": Thread.Sleep(Math.Max(0, Math.Min(60000, J.Int(J.Get(a, "ms"), 0)))); break;
            case "open": {
                var psi = new ProcessStartInfo(J.Str(a, "target")) { UseShellExecute = true };
                string args = J.Str(a, "args");
                if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
                Process.Start(psi);
                break;
            }
            case "run": {
                string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(J.Str(a, "cmd") ?? ""));
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + enc);
                if (J.Get(a, "show") is bool && (bool)J.Get(a, "show")) psi.UseShellExecute = true;
                else { psi.UseShellExecute = false; psi.CreateNoWindow = true; }
                Process.Start(psi);
                break;
            }
            case "volume": {
                object set = J.Get(a, "set"), step = J.Get(a, "step"), mute = J.Get(a, "mute");
                if (set != null) DeckAudio.Set(J.Int(set, 0));
                if (step != null) DeckAudio.Set(DeckAudio.Get() + J.Int(step, 0));
                if (mute is string && (string)mute == "toggle") DeckAudio.Mute(!DeckAudio.Muted());
                else if (mute is bool) DeckAudio.Mute((bool)mute);
                break;
            }
            case "page": break; // tratado no celular
            default: throw new Exception(L.T("Tipo de ação desconhecido: ", "Unknown action type: ") + type);
        }
    }
}

// ---------- icone da bandeja ----------
class TrayApp : ApplicationContext {
    readonly Control ui = new Control();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly DeckServer server;
    public bool Failed;

    public TrayApp(string mode) {
        ui.CreateControl();
        var handle = ui.Handle; // garante o handle para Invoke
        Log.Init();

        server = new DeckServer(ui);
        string err = server.Start();
        if (err != null) {
            MessageBox.Show(err, "PC Deck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Failed = true;
            return;
        }

        // o menu e montado a cada abertura: assim segue o idioma escolhido no editor
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripSeparator()); // sem itens o Opening nao dispara
        menu.Opening += (s, e) => { BuildMenu(menu); e.Cancel = false; };

        tray.Icon = Res.AppIcon(SystemInformation.SmallIconSize);
        tray.Text = L.T("PC Deck - rodando", "PC Deck - running");
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenEditor(); };
        tray.Visible = true;

        Listen(Names.OpenEditor, OpenEditor);
        Listen(Names.Welcome, Welcome);
        Listen(Names.Quit, Quit);

        if (mode == "--welcome") Welcome();
        else if (mode != "--background") OpenEditor();
    }

    void BuildMenu(ContextMenuStrip menu) {
        menu.Items.Clear();
        var open = new ToolStripMenuItem(L.T("Abrir editor dos botões", "Open button editor"), null, (s, e) => OpenEditor());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem(L.T("Conectar celular (QR code)", "Connect phone (QR code)"), null, (s, e) => OpenQr()));
        menu.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem(L.T("Iniciar com o Windows", "Start with Windows")) { Checked = Setup.AutoStart };
        auto.Click += (s, e) => { Setup.AutoStart = !Setup.AutoStart; };
        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripMenuItem(L.T("Abrir pasta dos dados", "Open data folder"), null, (s, e) => Process.Start("explorer.exe", "\"" + Paths.Data + "\"")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("Sair do PC Deck", "Quit PC Deck"), null, (s, e) => Quit()));
        tray.Text = L.T("PC Deck - rodando", "PC Deck - running");
    }

    void Listen(string name, Action act) {
        var h = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var t = new Thread(() => {
            while (true) {
                h.WaitOne();
                try { ui.BeginInvoke(act); } catch { }
            }
        }) { IsBackground = true };
        t.Start();
    }

    void Welcome() {
        tray.ShowBalloonTip(8000, L.T("PC Deck está rodando", "PC Deck is running"),
            L.T("Ele fica no ícone perto do relógio. Escaneie o QR code com o celular para conectar.",
                "It lives in the icon near the clock. Scan the QR code with your phone to connect."), ToolTipIcon.Info);
        OpenQr();
    }

    void OpenQr() { Process.Start("http://127.0.0.1:" + server.Port + "/qr"); }

    void OpenEditor() {
        string url = "http://127.0.0.1:" + server.Port + "/editor";
        string edge = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
        }.FirstOrDefault(File.Exists);
        if (edge != null) Process.Start(edge, "--app=" + url + " --window-size=1440,900");
        else Process.Start(url);
    }

    void Quit() {
        tray.Visible = false;
        server.Stop();
        Log.Write("encerrado");
        ExitThread();
    }
}

}
