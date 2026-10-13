using System;
using System.Net;
using System.Net.Sockets;

namespace WSLCManager.Services;

/// <summary>
/// 创建网络前的**本地**校验。
///
/// 定位：只拦「一眼就是错的」输入，目标是把 wslc 的报错翻译成人话，**不是**重新实现一个
/// CIDR 校验器 —— 与上游重复实现只会慢慢漂移，真正合法与否最终由 wslc 说了算。
///
/// 放在 Services 层而不是窗口类里，是为了让探针能直接跑（窗口类是 WPF，探针编不进去）。
/// </summary>
public static class NetworkValidator
{
    /// <summary>
    /// 校验通过返回 true；否则 why 里是一句可直接展示给用户的中文。
    /// </summary>
    public static bool Validate(string name, string subnet, string gateway, out string why)
    {
        why = "";
        var nm = (name ?? "").Trim();
        var sub = (subnet ?? "").Trim();
        var gw = (gateway ?? "").Trim();

        if (nm.Length == 0) { why = "请先填网络名称"; return false; }

        foreach (var ch in nm)
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.'))
            { why = "网络名称只能用字母、数字、下划线、连字符和点"; return false; }

        if (nm.Equals("bridge", StringComparison.OrdinalIgnoreCase) ||
            nm.Equals("host", StringComparison.OrdinalIgnoreCase) ||
            nm.Equals("none", StringComparison.OrdinalIgnoreCase))
        { why = $"「{nm}」是 wslc 自带网络，换个名字"; return false; }

        if (!TryParseCidr(sub, out var netIp, out var prefix, out var subWhy))
        {
            why = subWhy;
            return false;
        }

        if (sub.Length == 0 && gw.Length > 0)
        {
            // 只填网关不填网段：wslc 会自己挑网段，手填的网关几乎必然落在网段外
            why = "填了网关就要把网段一起填上，否则网关会落在 wslc 自动分配的网段之外";
            return false;
        }

        if (gw.Length > 0)
        {
            if (!IPAddress.TryParse(gw, out var gwIp) ||
                gwIp.AddressFamily != AddressFamily.InterNetwork)
            { why = "网关要写成 IPv4 地址，例如 172.30.0.1"; return false; }

            if (netIp != null && !InSameSubnet(netIp, gwIp, prefix))
            { why = $"网关 {gw} 不在网段 {sub} 内，容器会连不上"; return false; }
        }

        return true;
    }

    /// <summary>解析 CIDR。subnet 留空算通过（= 让 wslc 自动分配），此时 ip 返回 null。</summary>
    public static bool TryParseCidr(string subnet, out IPAddress ip, out int prefix, out string why)
    {
        ip = null; prefix = 0; why = "";
        var s = (subnet ?? "").Trim();
        if (s.Length == 0) return true;

        var slash = s.IndexOf('/');
        if (slash <= 0 || slash == s.Length - 1)
        { why = "网段要写成 CIDR 形式，例如 172.30.0.0/24"; return false; }

        if (!IPAddress.TryParse(s.Substring(0, slash).Trim(), out ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
        { why = "网段里的地址要是 IPv4，例如 172.30.0.0/24"; return false; }

        if (!int.TryParse(s.Substring(slash + 1).Trim(), out prefix) || prefix < 1 || prefix > 32)
        { why = "网段掩码要在 1~32 之间，例如 /24"; return false; }

        return true;
    }

    public static bool InSameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4) return false;

        uint mask = prefix >= 32 ? 0xFFFFFFFFu : (0xFFFFFFFFu << (32 - prefix));
        uint ua = ((uint)x[0] << 24) | ((uint)x[1] << 16) | ((uint)x[2] << 8) | x[3];
        uint ub = ((uint)y[0] << 24) | ((uint)y[1] << 16) | ((uint)y[2] << 8) | y[3];
        return (ua & mask) == (ub & mask);
    }
}
