using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

// ---------- HTTP minimo sobre TcpListener (nao exige admin / urlacl) ----------
public class DeckRequest {
    public string Method = "";
    public string Path = "";
    public string Query = "";
    public string Body = "";
    public string Remote = "";
    public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Q(string key) {
        foreach (var part in Query.Split('&')) {
            int i = part.IndexOf('=');
            if (i > 0 && part.Substring(0, i) == key)
                return Uri.UnescapeDataString(part.Substring(i + 1).Replace('+', ' '));
        }
        return null;
    }

    public string H(string key) {
        string v;
        return Headers.TryGetValue(key, out v) ? v : null;
    }
}

public static class DeckHttp {
    public static DeckRequest Read(TcpClient c) {
        var s = c.GetStream();
        s.ReadTimeout = 3000;
        var head = new MemoryStream();
        int state = 0;
        while (true) {
            int b = s.ReadByte();
            if (b < 0) return null;
            head.WriteByte((byte)b);
            if (b == '\r' && (state == 0 || state == 2)) state++;
            else if (b == '\n' && (state == 1 || state == 3)) state++;
            else state = 0;
            if (state == 4) break;
            if (head.Length > 16384) return null;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var r = new DeckRequest();
        r.Method = first[0];
        int qi = first[1].IndexOf('?');
        r.Path = qi >= 0 ? first[1].Substring(0, qi) : first[1];
        r.Query = qi >= 0 ? first[1].Substring(qi + 1) : "";
        for (int i = 1; i < lines.Length; i++) {
            int ci = lines[i].IndexOf(':');
            if (ci > 0) r.Headers[lines[i].Substring(0, ci).Trim()] = lines[i].Substring(ci + 1).Trim();
        }
        int len = 0;
        var cl = r.H("Content-Length");
        if (cl != null) int.TryParse(cl, out len);
        if (len > 0 && len < 1048576) {
            var buf = new byte[len];
            int got = 0;
            while (got < len) {
                int n = s.Read(buf, got, len - got);
                if (n <= 0) break;
                got += n;
            }
            r.Body = Encoding.UTF8.GetString(buf, 0, got);
        }
        r.Remote = ((System.Net.IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
        return r;
    }

    public static void Send(TcpClient c, int code, string type, byte[] body) {
        string reason = code == 200 ? "OK" : code == 401 ? "Unauthorized" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error";
        string h = "HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: " + type +
                   "\r\nContent-Length: " + body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        var s = c.GetStream();
        var hb = Encoding.ASCII.GetBytes(h);
        s.Write(hb, 0, hb.Length);
        s.Write(body, 0, body.Length);
        s.Flush();
    }
}

// ---------- Teclado (SendInput) ----------
public static class DeckInput {
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

    static readonly HashSet<ushort> Extended = new HashSet<ushort> {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90,
        0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3
    };

    static readonly Dictionary<string, ushort> Names = new Dictionary<string, ushort> {
        {"ctrl",0x11},{"control",0x11},{"shift",0x10},{"alt",0x12},{"win",0x5B},{"windows",0x5B},
        {"enter",0x0D},{"esc",0x1B},{"escape",0x1B},{"tab",0x09},{"space",0x20},{"backspace",0x08},
        {"delete",0x2E},{"del",0x2E},{"insert",0x2D},{"home",0x24},{"end",0x23},{"pgup",0x21},{"pgdn",0x22},
        {"up",0x26},{"down",0x28},{"left",0x25},{"right",0x27},{"printscreen",0x2C},{"prtsc",0x2C},
        {"capslock",0x14},{"numlock",0x90},{"menu",0x5D},
        {"minus",0xBD},{"equals",0xBB},{"comma",0xBC},{"period",0xBE},{"semicolon",0xBA},{"slash",0xC1},
        {"play_pause",0xB3},{"next",0xB0},{"prev",0xB1},{"stop",0xB2},
        {"mute",0xAD},{"vol_down",0xAE},{"vol_up",0xAF}
    };

    public static ushort Vk(string name) {
        string n = name.Trim().ToLowerInvariant();
        ushort vk;
        if (Names.TryGetValue(n, out vk)) return vk;
        if (n.Length == 1 && n[0] >= 'a' && n[0] <= 'z') return (ushort)(0x41 + n[0] - 'a');
        if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') return (ushort)(0x30 + n[0] - '0');
        if (n.StartsWith("num") && n.Length == 4 && char.IsDigit(n[3])) return (ushort)(0x60 + n[3] - '0');
        int f;
        if (n.StartsWith("f") && int.TryParse(n.Substring(1), out f) && f >= 1 && f <= 24) return (ushort)(0x6F + f);
        throw new ArgumentException("Tecla desconhecida: " + name);
    }

    static INPUT Key(ushort vk, bool up) {
        var i = new INPUT();
        i.type = 1;
        i.u.ki.wVk = vk;
        i.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
        i.u.ki.dwFlags = (up ? KEYUP : 0) | (Extended.Contains(vk) ? EXTENDED : 0);
        return i;
    }

    static void Send(List<INPUT> list) {
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }

    // "ctrl+shift+esc", "play_pause", "win+d"
    public static void Combo(string combo) {
        var vks = new List<ushort>();
        foreach (var part in combo.Split('+')) if (part.Trim().Length > 0) vks.Add(Vk(part));
        var list = new List<INPUT>();
        foreach (var vk in vks) list.Add(Key(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) list.Add(Key(vks[i], true));
        Send(list);
    }

    public static void Type(string text) {
        var list = new List<INPUT>();
        foreach (char ch in text) {
            if (ch == '\r') continue;
            if (ch == '\n') { list.Add(Key(0x0D, false)); list.Add(Key(0x0D, true)); continue; }
            foreach (bool up in new[] { false, true }) {
                var i = new INPUT();
                i.type = 1;
                i.u.ki.wScan = ch;
                i.u.ki.dwFlags = UNICODE | (up ? KEYUP : 0);
                list.Add(i);
            }
        }
        Send(list);
    }
}

// ---------- CPU / RAM ----------
public static class DeckSys {
    [StructLayout(LayoutKind.Sequential)]
    struct MEMSTATUS { public uint len, load; public ulong total, avail, totalPage, availPage, totalVirt, availVirt, availExt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    static long lastIdle, lastTotal;

    public static int Cpu() {
        long idle, kernel, user;
        GetSystemTimes(out idle, out kernel, out user);
        long total = kernel + user;
        long dIdle = idle - lastIdle, dTotal = total - lastTotal;
        lastIdle = idle; lastTotal = total;
        if (dTotal <= 0) return 0;
        return (int)Math.Round(100.0 * (1.0 - (double)dIdle / dTotal));
    }

    public static int Ram() {
        var m = new MEMSTATUS();
        m.len = (uint)Marshal.SizeOf(typeof(MEMSTATUS));
        GlobalMemoryStatusEx(ref m);
        return (int)m.load;
    }
}

// ---------- Volume mestre (Core Audio) ----------
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr p);
    int UnregisterControlChangeNotify(IntPtr p);
    int GetChannelCount(out int n);
    int SetMasterVolumeLevel(float db, [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
    int SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
    int GetMasterVolumeLevel(out float db);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint ch, float db, [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
    int SetChannelVolumeLevelScalar(uint ch, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
    int GetChannelVolumeLevel(uint ch, out float db);
    int GetChannelVolumeLevelScalar(uint ch, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

public static class DeckAudio {
    static IAudioEndpointVolume Endpoint() {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice dev;
        Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out dev));
        var iid = typeof(IAudioEndpointVolume).GUID;
        object o;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out o));
        return (IAudioEndpointVolume)o;
    }

    public static int Get() {
        float f;
        Endpoint().GetMasterVolumeLevelScalar(out f);
        return (int)Math.Round(f * 100);
    }

    public static void Set(int v) {
        v = Math.Max(0, Math.Min(100, v));
        Endpoint().SetMasterVolumeLevelScalar(v / 100f, Guid.Empty);
    }

    public static bool Muted() {
        bool m;
        Endpoint().GetMute(out m);
        return m;
    }

    public static void Mute(bool m) {
        Endpoint().SetMute(m, Guid.Empty);
    }
}
