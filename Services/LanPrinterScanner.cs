// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>本机已安装、且判定为网络打印机的条目。</summary>
    public sealed class LanInstalledPrinter
    {
        public string name { get; set; } = "";
        public string driver { get; set; } = "";
        public string port { get; set; } = "";
        public string location { get; set; } = "";
        public string comment { get; set; } = "";
        public bool isDefault { get; set; }
    }

    /// <summary>网络上发现的一台打印设备（尚未安装到本地）。</summary>
    public sealed class LanPrinterDevice
    {
        public string source { get; set; } = "";   // ssdp | wsd | mdns
        public string name { get; set; } = "";
        public string address { get; set; } = "";
        public int port { get; set; }
        public string protocol { get; set; } = ""; // ipp | ipps | raw | wsd | upnp
        public string model { get; set; } = "";
    }

    public sealed class LanScanResult
    {
        public List<LanInstalledPrinter> installedNetwork { get; set; } = new List<LanInstalledPrinter>();
        public List<LanPrinterDevice> discovered { get; set; } = new List<LanPrinterDevice>();
        public List<string> methods { get; set; } = new List<string>();
        public int elapsedMs { get; set; }
        public string note { get; set; } = "";
        // 主动探测时记录用户给定网段解析出的目标地址数，便于 UI 说明扫描规模。
        public int probedHosts { get; set; }
        // 主动探测跳过了多少超限地址（网段过大时按上限截断）。
        public int skippedHosts { get; set; }
    }

    /// <summary>
    /// 局域网打印机只读发现：WMI 已安装网络打印机 + SSDP/WSD + mDNS/DNS-SD（IPP/IPPS/raw）。
    /// 只发送标准发现报文并读取应答，不建立打印连接、不安装驱动、不修改系统配置。
    /// 每一路独立超时、失败静默跳过，任何一路异常都不影响其余路与主打印链路。
    /// </summary>
    public sealed class LanPrinterScanner
    {
        private static readonly Regex NetPortPattern = new Regex(
            @"^(IP_|WSD-|\\\\|\d{1,3}(\.\d{1,3}){3})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private sealed class MdnsService
        {
            public string Type;
            public string Proto;
        }

        /// <summary>
        /// 执行一次扫描。methods 取 installed / ssdp / mdns / active 的任意子集；timeoutMs 为每路网络发现超时。
        /// active 为按指定网段的 TCP 端口只读探测：必须显式声明且给出 subnets 才会执行，默认不启用。
        /// subnets 支持逗号/分号分隔的 IPv4 单地址、CIDR（192.168.1.0/24）、范围（192.168.1.1-192.168.1.50）。
        /// maxHosts 限制单次目标地址数（1-4096），防止超大网段造成资源占用。
        /// </summary>
        public LanScanResult Scan(int timeoutMs, HashSet<string> methods, string subnets = null, int maxHosts = 512)
        {
            if (timeoutMs < 500) timeoutMs = 500;
            if (timeoutMs > 8000) timeoutMs = 8000;
            if (maxHosts < 1) maxHosts = 1;
            if (maxHosts > 4096) maxHosts = 4096;

            var sw = Stopwatch.StartNew();
            var result = new LanScanResult
            {
                note = "发现结果仅用于识别设备；如需使用，请在 Windows 中将该打印机添加为网络打印机并安装厂商官方驱动。"
            };
            var chosen = new HashSet<string>(methods ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            // 默认三种被动/只读发现；active 必须显式声明，绝不默认探测网段。
            if (chosen.Count == 0) chosen = new HashSet<string>(new[] { "installed", "ssdp", "mdns" }, StringComparer.OrdinalIgnoreCase);

            if (chosen.Contains("installed"))
            {
                try { result.installedNetwork = ScanInstalledNetwork(); }
                catch (Exception ex) { Tools.Logger("局域网扫描：WMI 读取失败 " + ex.Message); }
            }
            if (chosen.Contains("ssdp"))
            {
                try { result.discovered.AddRange(SsdpDiscover(Math.Min(timeoutMs, 1800))); }
                catch (Exception ex) { Tools.Logger("局域网扫描：SSDP/WSD 发现失败 " + ex.Message); }
            }
            if (chosen.Contains("mdns"))
            {
                try { result.discovered.AddRange(MdnsDiscover(Math.Min(timeoutMs, 1800))); }
                catch (Exception ex) { Tools.Logger("局域网扫描：mDNS 发现失败 " + ex.Message); }
            }
            if (chosen.Contains("active"))
            {
                try
                {
                    var budget = Math.Min(Math.Max(timeoutMs * 2, 1500), 8000);
                    var active = ProbeActive(subnets, maxHosts, budget,
                        out var probed, out var skipped);
                    result.probedHosts = probed;
                    result.skippedHosts = skipped;
                    result.discovered.AddRange(active);
                    if (probed == 0)
                        result.note += " 未解析到有效的目标网段，主动探测已跳过。";
                    else
                        result.note += " 主动探测仅检测常见打印端口是否开放，不发送任何打印数据，端口开放不代表可立即使用。";
                }
                catch (Exception ex) { Tools.Logger("局域网扫描：主动探测失败 " + ex.Message); }
            }

            result.discovered = Dedupe(result.discovered);
            result.methods = chosen.OrderBy(x => x).ToList();
            sw.Stop();
            result.elapsedMs = (int)sw.ElapsedMilliseconds;
            return result;
        }

        // 主动探测：对指定 IPv4 网段的常见打印端口做 TCP 连接测试，只握手、不发送任何字节。
        // 每地址只记录第一个开放的端口，减少连接数；整体受 budget 取消，并发受限。
        private static readonly int[] ProbePorts = { 9100, 631, 515 };
        private static readonly string[] ProbeProtocols = { "raw", "ipp", "lpd" };

        private static List<LanPrinterDevice> ProbeActive(string subnets, int maxHosts, int budgetMs,
            out int probed, out int skipped)
        {
            probed = 0;
            skipped = 0;
            var found = new ConcurrentBag<LanPrinterDevice>();
            var targets = ParseTargets(subnets, maxHosts, out skipped);
            probed = targets.Count;
            if (targets.Count == 0) return found.ToList();

            using (var cts = new CancellationTokenSource(budgetMs))
            {
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 64,
                    CancellationToken = cts.Token
                };
                try
                {
                    Parallel.ForEach(targets, options, ip =>
                    {
                        if (cts.IsCancellationRequested) return;
                        var address = ip.ToString();
                        for (var i = 0; i < ProbePorts.Length; i++)
                        {
                            if (cts.IsCancellationRequested) return;
                            if (!IsPortOpen(ip, ProbePorts[i], 180)) continue;
                            found.Add(new LanPrinterDevice
                            {
                                source = "active",
                                name = address,
                                address = address,
                                port = ProbePorts[i],
                                protocol = ProbeProtocols[i],
                                model = ""
                            });
                            break; // 每地址只记一个端口，降低对设备的连接压力。
                        }
                    });
                }
                catch (OperationCanceledException) { /* 到达时间预算，返回已发现结果 */ }
            }
            return found.OrderBy(x => x.address).ToList();
        }

        /// <summary>TCP 只握手探测端口，不发送任何数据。超时按毫秒计。</summary>
        private static bool IsPortOpen(IPAddress ip, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync(ip, port);
                    if (!task.Wait(timeoutMs)) return false;
                    return client.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>解析目标网段为 IPv4 地址列表：单 IP / CIDR / 起止范围，去重并按上限截断。</summary>
        private static List<IPAddress> ParseTargets(string subnets, int maxHosts, out int skipped)
        {
            skipped = 0;
            var targets = new List<IPAddress>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(subnets)) return targets;

            var tokens = subnets.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (targets.Count >= maxHosts) break;
                try
                {
                    IEnumerable<uint> addresses;
                    long segmentSize;
                    if (token.IndexOf('/') > 0)
                    {
                        var parts = token.Split('/');
                        var baseIp = IPAddress.Parse(parts[0]);
                        if (baseIp.AddressFamily != AddressFamily.InterNetwork) continue;
                        var prefix = int.Parse(parts[1]);
                        if (prefix < 0 || prefix > 32) continue;
                        segmentSize = prefix == 32 ? 1L : (1L << (32 - prefix));
                        addresses = EnumerateCidr(IpToUInt(baseIp), prefix);
                    }
                    else if (token.IndexOf('-') > 0)
                    {
                        var parts = token.Split('-');
                        var from = IPAddress.Parse(parts[0]);
                        var to = IPAddress.Parse(parts[1]);
                        if (from.AddressFamily != AddressFamily.InterNetwork ||
                            to.AddressFamily != AddressFamily.InterNetwork) continue;
                        var start = IpToUInt(from);
                        var end = IpToUInt(to);
                        if (start > end) { var tmp = start; start = end; end = tmp; }
                        segmentSize = (long)end - start + 1;
                        addresses = EnumerateRange(start, end);
                    }
                    else
                    {
                        var single = IPAddress.Parse(token);
                        if (single.AddressFamily != AddressFamily.InterNetwork) continue;
                        segmentSize = 1;
                        addresses = new[] { IpToUInt(single) };
                    }

                    // 只枚举到上限；未枚举到的部分计入 skipped。超大网段不会惰性迭代到底。
                    var enumerated = 0;
                    foreach (var value in addresses)
                    {
                        if (targets.Count >= maxHosts) break;
                        enumerated++;
                        var ip = UIntToIp(value);
                        if (seen.Add(ip.ToString())) targets.Add(ip);
                    }
                    if (enumerated < segmentSize)
                        skipped += (int)Math.Min(int.MaxValue, segmentSize - enumerated);
                }
                catch { /* 单段格式非法，忽略该段 */ }
            }
            return targets;
        }

        private static IEnumerable<uint> EnumerateCidr(uint baseIp, int prefix)
        {
            if (prefix == 32) { yield return baseIp; yield break; }
            var mask = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
            var network = baseIp & mask;
            var broadcast = network | ~mask;
            var start = network + 1; // 跳过网络地址
            var end = broadcast - 1; // 跳过广播地址
            if (prefix >= 31) { start = network; end = broadcast; } // /31、/32 两端都可分配
            for (var v = start; v <= end; v++) yield return v;
        }

        private static IEnumerable<uint> EnumerateRange(uint start, uint end)
        {
            for (var v = start; v <= end; v++) yield return v;
        }

        private static uint IpToUInt(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress UIntToIp(uint value) => new IPAddress(new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        });

        /// <summary>WMI：本机已安装且为网络打印机（Network=true 或端口具备网络特征）。</summary>
        private static List<LanInstalledPrinter> ScanInstalledNetwork()
        {
            var list = new List<LanInstalledPrinter>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name,DriverName,PortName,Network,Default,Location,Comment FROM Win32_Printer"))
            using (var items = searcher.Get())
            {
                foreach (ManagementObject o in items)
                {
                    try
                    {
                        var port = (o["PortName"] as string) ?? "";
                        var isNetwork = false;
                        try { isNetwork = o["Network"] is bool b && b; } catch { }
                        if (!isNetwork && !NetPortPattern.IsMatch(port)) continue;

                        list.Add(new LanInstalledPrinter
                        {
                            name = (o["Name"] as string) ?? "",
                            driver = (o["DriverName"] as string) ?? "",
                            port = port,
                            location = (o["Location"] as string) ?? "",
                            comment = (o["Comment"] as string) ?? "",
                            isDefault = o["Default"] is bool d && d
                        });
                    }
                    catch { /* 单条读取失败忽略 */ }
                    finally { o.Dispose(); }
                }
            }
            return list;
        }

        /// <summary>
        /// SSDP/WSD：向 239.255.255.250:1900 发 M-SEARCH，收集声明为打印机的设备。
        /// 响应是单播回源端口，无需加入组播组；短超时后返回已收到的应答。
        /// </summary>
        private static List<LanPrinterDevice> SsdpDiscover(int timeoutMs)
        {
            var found = new Dictionary<string, LanPrinterDevice>(StringComparer.OrdinalIgnoreCase);
            var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.ReceiveTimeout = timeoutMs;

                var searches = new[]
                {
                    "urn:schemas-upnp-org:device:Printer:1",       // UPnP 打印机
                    "urn:schemas-xmlsoap-org:ws:2005:04:discovery" // WSD 设备发现
                };
                foreach (var st in searches)
                {
                    var req = "M-SEARCH * HTTP/1.1\r\n" +
                              "HOST: 239.255.255.250:1900\r\n" +
                              "MAN: \"ssdp:discover\"\r\n" +
                              "MX: 1\r\n" +
                              "ST: " + st + "\r\n\r\n";
                    try { udp.SendTo(Encoding.ASCII.GetBytes(req), endpoint); }
                    catch (Exception ex) { Tools.Logger("SSDP 发送失败: " + ex.Message); }
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (!udp.Poll(150000, SelectMode.SelectRead)) continue; // 150ms
                        var buf = new byte[8192];
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        var n = udp.ReceiveFrom(buf, ref remote);
                        if (n <= 0) continue;
                        ParseSsdpResponse(Encoding.UTF8.GetString(buf, 0, n),
                            ((IPEndPoint)remote).Address.ToString(), found);
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) { Tools.Logger("SSDP 应答解析失败: " + ex.Message); }
                }
            }
            return found.Values.ToList();
        }

        private static void ParseSsdpResponse(string text, string remoteIp, Dictionary<string, LanPrinterDevice> found)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
            }
            var st = GetHeader(headers, "ST") + " " + GetHeader(headers, "USN") + " " + GetHeader(headers, "SERVER");
            var isPrinter = st.IndexOf("printer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            st.IndexOf("printing", StringComparison.OrdinalIgnoreCase) >= 0;
            var isWsd = st.IndexOf("ws:2005:04", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        st.IndexOf("deviceservice", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isPrinter && !isWsd) return;

            string address = remoteIp;
            var xaddrs = GetHeader(headers, "X-Addrs");
            var location = GetHeader(headers, "LOCATION");
            var via = string.IsNullOrEmpty(xaddrs) ? location : xaddrs;
            if (!string.IsNullOrEmpty(via))
            {
                if (Uri.TryCreate(via, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                    address = uri.Host;
            }
            var key = address + "|" + (isPrinter ? "upnp" : "wsd");
            if (found.ContainsKey(key)) return;
            found[key] = new LanPrinterDevice
            {
                source = isPrinter ? "ssdp" : "wsd",
                name = string.IsNullOrEmpty(GetHeader(headers, "SERVER"))
                    ? (isPrinter ? "UPnP Printer" : "WSD Device")
                    : GetHeader(headers, "SERVER"),
                address = address,
                protocol = isPrinter ? "upnp" : "wsd",
                model = GetHeader(headers, "USN")
            };
        }

        private static string GetHeader(Dictionary<string, string> headers, string key)
            => headers.TryGetValue(key, out var v) ? v : "";

        /// <summary>
        /// mDNS/DNS-SD：查询 _ipp/_ipps/_pdl-datastream._tcp.local，解析 PTR/SRV/A/TXT。
        /// 使用 QU（单播应答）位，绑定临时端口即可收应答，不占用 5353、不需要加入组播组。
        /// </summary>
        private static List<LanPrinterDevice> MdnsDiscover(int timeoutMs)
        {
            var found = new List<LanPrinterDevice>();
            var services = new List<MdnsService>
            {
                new MdnsService { Type = "_ipp._tcp.local", Proto = "ipp" },
                new MdnsService { Type = "_ipps._tcp.local", Proto = "ipps" },
                new MdnsService { Type = "_pdl-datastream._tcp.local", Proto = "raw" } // JetDirect / 打印盒子 9100
            };
            var query = BuildPtrQuery(services.Select(s => s.Type).ToArray());
            var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            var state = new DnsState();

            using (var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                udp.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.ReceiveTimeout = timeoutMs;
                try { udp.SendTo(query, endpoint); }
                catch (Exception ex) { Tools.Logger("mDNS 发送失败: " + ex.Message); return found; }

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (!udp.Poll(150000, SelectMode.SelectRead)) continue;
                        var buf = new byte[4096];
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        var n = udp.ReceiveFrom(buf, ref remote);
                        // PTR/SRV/A/TXT 常分散在多个 UDP 包，必须跨包累积后再关联。
                        if (n > 12) AccumulateDnsResponse(buf, n, services, state);
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex) { Tools.Logger("mDNS 应答解析失败: " + ex.Message); }
                }
            }
            return AssembleDnsDevices(state);
        }

        /// <summary>mDNS 跨包累积状态：服务实例→协议/端口/主机名/型号，主机名或实例名→IP。</summary>
        private sealed class DnsState
        {
            public readonly Dictionary<string, string> Ptr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Tuple<int, string>> Srv = new Dictionary<string, Tuple<int, string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Addr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static byte[] BuildPtrQuery(string[] serviceTypes)
        {
            var body = new List<byte>();
            // 每个 question 必须自带 QNAME + QTYPE + QCLASS；QCLASS 置 QU 位(0x8000) 要求单播应答。
            foreach (var svc in serviceTypes)
            {
                body.AddRange(DnsName.Encode(svc));
                body.Add(0); body.Add(12);       // QTYPE=PTR
                body.Add(0x80); body.Add(1);     // QCLASS=IN | QU
            }

            // ID=0 FLAGS=0 QDCOUNT=N AN/NS/AR=0
            var packet = new List<byte>
            {
                0, 0, 0, 0,
                (byte)(serviceTypes.Length >> 8), (byte)serviceTypes.Length,
                0, 0, 0, 0, 0, 0
            };
            packet.AddRange(body);
            return packet.ToArray();
        }

        /// <summary>解析单个 mDNS 应答包，把 PTR/SRV/A/TXT 记录累积进 state（不在此组装）。</summary>
        private static void AccumulateDnsResponse(byte[] buf, int len,
            IEnumerable<MdnsService> services, DnsState state)
        {
            var qd = (buf[4] << 8) | buf[5];
            var an = (buf[6] << 8) | buf[7];
            var ns = (buf[8] << 8) | buf[9];
            var ar = (buf[10] << 8) | buf[11];

            var reader = new DnsName(buf);
            var pos = 12;
            for (var i = 0; i < qd && pos < len; i++)
            {
                pos = reader.Read(pos, out _);
                if (pos + 4 > len) return;
                pos += 4;
            }

            // A/SRV 记录常位于 Additional 区，Answer + Authority + Additional 全部解析。
            for (var i = 0; i < an + ns + ar && pos < len; i++)
            {
                pos = reader.Read(pos, out var name);
                if (pos + 10 > len) break;
                var type = (buf[pos] << 8) | buf[pos + 1];
                var rdLength = (buf[pos + 8] << 8) | buf[pos + 9];
                var dataStart = pos + 10;
                var dataEnd = Math.Min(len, dataStart + rdLength);

                if (type == 12 && dataStart < dataEnd) // PTR: rdata = instance 名
                {
                    reader.Read(dataStart, out var instance);
                    var proto = services.FirstOrDefault(s => name.IndexOf(s.Type, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (proto != null && !string.IsNullOrEmpty(instance)) state.Ptr[instance] = proto.Proto;
                }
                else if (type == 33 && dataStart + 6 <= dataEnd) // SRV: pri(2) weight(2) port(2) target(name)
                {
                    var port = (buf[dataStart + 4] << 8) | buf[dataStart + 5];
                    reader.Read(dataStart + 6, out var target);
                    state.Srv[name] = Tuple.Create(port, target);
                }
                else if (type == 1 && dataStart + 4 <= dataEnd) // A: owner=主机名或实例名
                {
                    state.Addr[name] = new IPAddress(new[] { buf[dataStart], buf[dataStart + 1], buf[dataStart + 2], buf[dataStart + 3] }).ToString();
                }
                else if (type == 16 && dataStart < dataEnd) // TXT: 长度前缀串，取 ty= 作为型号
                {
                    var p2 = dataStart;
                    while (p2 < dataEnd)
                    {
                        var l = buf[p2++];
                        if (l == 0 || p2 + l > dataEnd) break;
                        var s = Encoding.UTF8.GetString(buf, p2, l);
                        p2 += l;
                        if (s.StartsWith("ty=", StringComparison.OrdinalIgnoreCase))
                        {
                            state.Txt[name] = s.Substring(3);
                            break;
                        }
                    }
                }
                pos = dataEnd;
            }
        }

        /// <summary>跨包解析完成后，把 PTR 实例与 SRV/A/TXT 关联成设备列表。</summary>
        private static List<LanPrinterDevice> AssembleDnsDevices(DnsState state)
        {
            var found = new List<LanPrinterDevice>();
            foreach (var kv in state.Ptr)
            {
                var instance = kv.Key;
                var displayName = instance.Split('.')[0].TrimEnd();
                if (displayName.Length == 0) displayName = instance;
                string ip = "";
                var port = 0;

                if (state.Srv.TryGetValue(instance, out var s))
                {
                    port = s.Item1;
                    // A 记录通常挂在 SRV target 主机名下，也可能直接挂在实例名下。
                    if (state.Addr.TryGetValue(s.Item2, out var byTarget)) ip = byTarget;
                    if (string.IsNullOrEmpty(ip) && state.Addr.TryGetValue(instance, out var byInstance)) ip = byInstance;
                }
                // 无 SRV 时尝试直接以实例名取 A（部分简化实现）。
                if (string.IsNullOrEmpty(ip) && state.Addr.TryGetValue(instance, out var direct)) ip = direct;
                state.Txt.TryGetValue(instance, out var model);

                found.Add(new LanPrinterDevice
                {
                    source = "mdns",
                    name = displayName,
                    address = ip,
                    port = port,
                    protocol = kv.Value,
                    model = model ?? ""
                });
            }
            return found;
        }

        private static List<LanPrinterDevice> Dedupe(List<LanPrinterDevice> devices)
        {
            var result = new List<LanPrinterDevice>();
            foreach (var d in devices.OrderBy(x => x.source).ThenBy(x => x.name))
            {
                if (string.IsNullOrWhiteSpace(d.name) && string.IsNullOrWhiteSpace(d.address)) continue;
                var same = result.FirstOrDefault(x =>
                    (!string.IsNullOrEmpty(d.address) && string.Equals(x.address, d.address, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(x.name, d.name, StringComparison.OrdinalIgnoreCase));
                if (same == null) result.Add(d);
                else if (string.IsNullOrEmpty(same.address) && !string.IsNullOrEmpty(d.address)) same.address = d.address;
            }
            return result;
        }

        /// <summary>DNS 名字读写：支持 RFC1035 压缩指针，避免 mDNS 应答解析失败。</summary>
        private sealed class DnsName
        {
            private readonly byte[] _buf;
            public DnsName(byte[] buf) { _buf = buf; }

            public static byte[] Encode(string name)
            {
                var bytes = new List<byte>();
                foreach (var label in name.Split('.'))
                {
                    var b = Encoding.ASCII.GetBytes(label);
                    bytes.Add((byte)b.Length);
                    bytes.AddRange(b);
                }
                bytes.Add(0);
                return bytes.ToArray();
            }

            /// <summary>从 pos 读一个名字，返回结束位置（供继续解析 RR）。</summary>
            public int Read(int pos, out string name)
            {
                var parts = new List<string>();
                var end = -1;
                var jumps = 0;
                while (pos < _buf.Length && jumps < 16)
                {
                    var len = _buf[pos];
                    if ((len & 0xC0) == 0xC0)
                    {
                        if (pos + 1 >= _buf.Length) break;
                        if (end < 0) end = pos + 2;
                        pos = ((len & 0x3F) << 8) | _buf[pos + 1];
                        jumps++;
                        continue;
                    }
                    pos++;
                    if (len == 0) { if (end < 0) end = pos; break; }
                    if (pos + len > _buf.Length) break;
                    parts.Add(Encoding.UTF8.GetString(_buf, pos, len));
                    pos += len;
                }
                name = string.Join(".", parts);
                return end < 0 ? pos : end;
            }
        }
    }
}
