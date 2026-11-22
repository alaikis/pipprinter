// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    public class TonerLevel
    {
        public string Host { get; set; } = "";
        public string Cartridge { get; set; } = "";
        public int LevelPercent { get; set; }
        public bool Low { get; set; }
    }

    /// <summary>
    /// 墨量监控：SNMP v1 GetRequest 查询标准打印机 MIB 的碳粉余量
    /// （1.3.6.1.2.1.43.11.1.1.9 = prtMarkerSuppliesLevel，.6 = 描述，.8 = 最大容量）。
    /// 纯 UDP 手写 ASN.1 BER 编码，不引入第三方 SNMP 库；查询失败静默返回空列表。
    /// 后台轮询由 PrintServices/Program 接线，阈值来自配置 TonerLowThreshold。
    /// </summary>
    public static class TonerMonitorService
    {
        private static readonly Dictionary<string, List<TonerLevel>> Cache =
            new Dictionary<string, List<TonerLevel>>();

        /// <summary>查询指定 host 的墨量；host 为空时返回缓存的最近一次结果。</summary>
        public static List<TonerLevel> Query(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                lock (Cache) return Cache.Values.SelectMany(x => x).ToList();
            }
            try
            {
                var levels = SnmpToner(host, "public", 1500);
                lock (Cache) Cache[host] = levels;
                return levels;
            }
            catch (Exception ex)
            {
                Tools.Logger("墨量查询失败(" + host + "): " + ex.Message);
                lock (Cache) return Cache.ContainsKey(host) ? Cache[host] : new List<TonerLevel>();
            }
        }

        /// <summary>SNMP 轮询所有已发现的网络打印机（从缓存的墨量 host 列表）。</summary>
        public static void PollAll()
        {
            List<string> hosts;
            lock (Cache) hosts = Cache.Keys.ToList();
            foreach (var h in hosts) Query(h);
        }

        // ------------------------------------------------ SNMP v1 最小实现

        private static List<TonerLevel> SnmpToner(string host, string community, int timeoutMs)
        {
            var result = new List<TonerLevel>();
            // 标准 OID：1.3.6.1.2.1.43.11.1.1.9.1.1（第一个耗材余量）
            // 同时尝试几个常见索引，兼容不同厂商
            for (int idx = 1; idx <= 4; idx++)
            {
                var levelOid = "1.3.6.1.2.1.43.11.1.1.9.1." + idx;
                var maxOid = "1.3.6.1.2.1.43.11.1.1.8.1." + idx;
                var descOid = "1.3.6.1.2.1.43.11.1.1.6.1." + idx;
                var level = SnmpGetInt(host, community, levelOid, timeoutMs);
                if (level < 0) continue;
                var max = SnmpGetInt(host, community, maxOid, timeoutMs);
                var desc = SnmpGetString(host, community, descOid, timeoutMs);
                var pct = max > 0 ? (int)Math.Round(level * 100.0 / max) : level;
                pct = Math.Max(0, Math.Min(100, pct));
                var threshold = SettingsStore.Instance.Current.TonerLowThreshold;
                result.Add(new TonerLevel
                {
                    Host = host,
                    Cartridge = string.IsNullOrEmpty(desc) ? "耗材 " + idx : desc,
                    LevelPercent = pct,
                    Low = threshold > 0 && pct < threshold
                });
            }
            return result;
        }

        private static int SnmpGetInt(string host, string community, string oid, int timeoutMs)
        {
            var raw = SnmpGet(host, community, oid, timeoutMs);
            if (raw == null || raw.Length < 2) return -1;
            // BER INTEGER: 0x02 len value
            if (raw[0] == 0x02)
            {
                var len = raw[1];
                long val = 0;
                for (int i = 0; i < len && i + 2 < raw.Length; i++)
                    val = (val << 8) | raw[i + 2];
                return (int)val;
            }
            return -1;
        }

        private static string SnmpGetString(string host, string community, string oid, int timeoutMs)
        {
            var raw = SnmpGet(host, community, oid, timeoutMs);
            if (raw == null || raw.Length < 2 || raw[0] != 0x04) return "";
            var len = (int)raw[1];
            if (len > raw.Length - 2) len = raw.Length - 2;
            return System.Text.Encoding.UTF8.GetString(raw, 2, len);
        }

        /// <summary>发送 SNMP v1 GetRequest，返回响应中的 varbind value 的 BER 编码（含类型字节）。</summary>
        private static byte[] SnmpGet(string host, string community, string oid, int timeoutMs)
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                try { ip = Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork); }
                catch { return null; }
            }
            if (ip == null) return null;

            var packet = BuildSnmpGet(community, oid);
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;
            var ep = new IPEndPoint(ip, 161);
            udp.Send(packet, packet.Length, ep);
            var recv = udp.Receive(ref ep);
            return ExtractVarbindValue(recv);
        }

        private static byte[] BuildSnmpGet(string community, string oid)
        {
            var comm = System.Text.Encoding.ASCII.GetBytes(community ?? "public");
            var oidBytes = EncodeOid(oid);
            // varbind: SEQUENCE { OID, NULL }
            var nullValue = new byte[] { 0x05, 0x00 };
            var varbindBody = Concat(oidBytes, nullValue);
            var varbind = Tlv(0x30, varbindBody);
            var varbindList = Tlv(0x30, varbind);
            // GetRequest PDU: SEQUENCE { request-id INTEGER(0), error 0, error-index 0, varbindlist }
            var pduBody = Concat(
                Tlv(0x02, new byte[] { 0x01 }), // request id
                Tlv(0x02, new byte[] { 0x00 }), // error
                Tlv(0x02, new byte[] { 0x00 }), // error index
                varbindList);
            var pdu = Tlv(0xA0, pduBody);
            var msgBody = Concat(
                Tlv(0x02, new byte[] { 0x01 }), // version v1 = 0 (encoded as 0)
                Tlv(0x04, comm),
                pdu);
            return Tlv(0x30, msgBody);
        }

        private static byte[] EncodeOid(string oid)
        {
            var parts = (oid ?? "").Trim().Split('.').Where(x => !string.IsNullOrEmpty(x))
                .Select(x => int.Parse(x)).ToList();
            if (parts.Count < 2) parts = new List<int> { 1, 3 };
            var body = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
            for (int i = 2; i < parts.Count; i++) body.AddRange(EncodeBase128(parts[i]));
            return Tlv(0x06, body.ToArray());
        }

        private static IEnumerable<byte> EncodeBase128(int value)
        {
            if (value < 0) yield return 0;
            var stack = new Stack<byte>();
            int v = value;
            stack.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) stack.Push((byte)((v & 0x7F) | 0x80));
            while (stack.Count > 0) yield return stack.Pop();
        }

        private static byte[] ExtractVarbindValue(byte[] response)
        {
            try
            {
                // 简单扫描：找到最后一个 varbind 中的 value（OID 之后的 TLV）
                // 定位 0x30 varbindlist → 0x30 varbind → OID(0x06) → value
                int p = 0;
                if (response[p++] != 0x30) return null;
                SkipLen(response, ref p);
                if (response[p++] != 0x02) return null; // version
                SkipLen(response, ref p);
                if (response[p++] != 0x04) return null; // community
                int commLen = ReadLen(response, ref p);
                p += commLen;
                if (response[p++] != 0xA2) return null; // GetResponse PDU
                SkipLen(response, ref p);
                p += 6; // request-id + error + error-index (3 个 INTEGER，短编码)
                if (p >= response.Length || response[p++] != 0x30) return null; // varbind list
                SkipLen(response, ref p);
                if (p >= response.Length || response[p++] != 0x30) return null; // varbind
                SkipLen(response, ref p);
                if (p >= response.Length || response[p++] != 0x06) return null; // OID
                SkipLen(response, ref p);
                if (p >= response.Length) return null;
                var type = response[p++];
                var len = ReadLen(response, ref p);
                if (p + len > response.Length) return null;
                var value = new byte[len + 2];
                value[0] = type; value[1] = (byte)len;
                Array.Copy(response, p, value, 2, len);
                return value;
            }
            catch { return null; }
        }

        private static int ReadLen(byte[] b, ref int p)
        {
            var first = b[p++];
            if ((first & 0x80) == 0) return first;
            var n = first & 0x7F;
            int len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | b[p++];
            return len;
        }

        private static void SkipLen(byte[] b, ref int p) => ReadLen(b, ref p);

        private static byte[] Tlv(byte type, byte[] body)
        {
            var len = body.Length;
            if (len < 128)
                return Concat(new byte[] { type, (byte)len }, body);
            var lenBytes = new List<byte>();
            int l = len;
            while (l > 0) { lenBytes.Insert(0, (byte)(l & 0xFF)); l >>= 8; }
            return Concat(Concat(new byte[] { type, (byte)(0x80 | lenBytes.Count) }, lenBytes.ToArray()), body);
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            var total = arrays.Sum(a => a.Length);
            var result = new byte[total];
            int pos = 0;
            foreach (var a in arrays) { Array.Copy(a, 0, result, pos, a.Length); pos += a.Length; }
            return result;
        }
    }
}
