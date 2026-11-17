// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using DuoAssistant.Services;
using Microsoft.Win32;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 本机指纹：稳定标识「这台机器」，供跨浏览器/客户端识别机器并作为打印习惯的 machine-key。
    /// 优先取注册表 MachineGuid（系统安装时生成，跨浏览器/会话稳定），辅以系统盘卷序列号、
    /// 系统版本与当前用户名做 SHA1 哈希，生成 24 位前缀短指纹。
    /// </summary>
    public sealed class MachineFingerprint
    {
        private static readonly object Gate = new object();
        private static string _cache;

        public static string Get()
        {
            if (_cache != null) return _cache;
            lock (Gate)
            {
                if (_cache != null) return _cache;
                _cache = Build();
                // 持久化到 appsettings.json，避免下次重启重算
                try
                {
                    SettingsStore.Instance.Update(s => s.MachineId = _cache);
                }
                catch (Exception ex)
                {
                    Tools.Logger("MachineId 持久化失败: " + ex.Message);
                }
                return _cache;
            }
        }

        /// <summary>
        /// 取本机 MachineGuid（HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid）。
        /// 该键在系统安装时生成，重装系统会变化；同机跨浏览器/会话稳定。
        /// 需要 64 位视图，否则 32 位进程读不到 64 位键。
        /// </summary>
        public static string MachineGuid()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(
                           RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (var sub = key.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    {
                        return sub?.GetValue("MachineGuid") as string;
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>取 C 盘卷序列号，作为机器级次级标识（用 WMI 取卷序列号）。</summary>
        public static string VolumeSerial()
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed) continue;
                    // 用 WMI 查 Win32_Volume，避免 DriveInfo 没有 VolumeSerialNumber 属性
                    using (var searcher = new ManagementObjectSearcher(
                        "root\\cimv2",
                        "SELECT VolumeSerial FROM Win32_Volume WHERE DriveLetter='" + drive.Name[0] + "'"))
                    {
                        foreach (ManagementObject vol in searcher.Get())
                        {
                            var serial = vol["VolumeSerial"]?.ToString();
                            if (!string.IsNullOrEmpty(serial)) return serial;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Tools.Logger("VolumeSerial 读取失败: " + ex.Message);
            }
            return null;
        }

        private static string Build()
        {
            var guid = MachineGuid() ?? string.Empty;
            var vol = VolumeSerial() ?? string.Empty;
            var os = Environment.OSVersion.VersionString;
            var user = Environment.UserName;
            var input = guid + "|" + vol + "|" + os + "|" + user;
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return "PP-" + BitConverter.ToString(bytes).Replace("-", "").Substring(0, 24).ToLower();
            }
        }
    }
}
