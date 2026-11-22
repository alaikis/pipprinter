// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    public class DriverInstallResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
        public string InfPath { get; set; } = "";
        public string DriverName { get; set; } = "";
        public string Stdout { get; set; } = "";
    }

    /// <summary>
    /// 驱动自动部署：从本地 lib/driver 下的 INF 包静默安装打印机驱动。
    /// 安全约束：①拒绝路径穿越（INF 必须位于 lib/driver 内）；②校验文件大小上限（防 INF 炸弹）；
    /// ③不联网抓取任何驱动，只接受用户预先放置的本地 INF；④调用系统 pnputil/Add-PrinterDriver；
    /// ⑤非 Windows 平台直接返回不支持。不集成厂商 SDK，不修改系统既有驱动。
    /// </summary>
    public static class DriverDeploymentService
    {
        private const long MaxInfBytes = 10 * 1024 * 1024; // 10MB

        public static DriverInstallResult Install(string infPath, string driverName)
        {
            var result = new DriverInstallResult { InfPath = infPath ?? "", DriverName = driverName ?? "" };
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                result.Message = "驱动自动部署仅支持 Windows 平台";
                return result;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
                {
                    result.Message = "INF 文件不存在: " + infPath;
                    return result;
                }
                var full = Path.GetFullPath(infPath);
                var root = Path.GetFullPath(SettingsStore.Instance.DriverDir);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "安全拒绝：INF 必须位于 lib/driver 目录内（防路径穿越）";
                    return result;
                }
                var info = new FileInfo(full);
                if (info.Length > MaxInfBytes)
                {
                    result.Message = "安全拒绝：INF 文件超过 10MB 上限";
                    return result;
                }

                // ① pnputil 静默添加驱动包
                var pnp = Run("pnputil", "/add-driver \"" + full + "\" /install");
                result.Stdout += "[pnputil] " + pnp + Environment.NewLine;

                // ② 若指定 driverName，注册为系统打印机驱动
                if (!string.IsNullOrWhiteSpace(driverName))
                {
                    var safeName = driverName.Replace("'", "''");
                    var psCmd = "Add-PrinterDriver -Name '" + safeName + "'";
                    var add = Run("powershell", "-NoProfile -NonInteractive -Command \"" + psCmd + "\"");
                    result.Stdout += "[Add-PrinterDriver] " + add + Environment.NewLine;
                }
                result.Ok = true;
                result.Message = "驱动安装指令已执行（请以 pnputil 输出为准核验签名与结果）";
                Tools.Logger("驱动部署完成: " + full);
            }
            catch (Exception ex)
            {
                result.Message = "驱动安装失败: " + ex.Message;
                Tools.Logger("驱动部署失败: " + ex);
            }
            return result;
        }

        private static string Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "进程启动失败";
                var outp = p.StandardOutput.ReadToEnd();
                var errp = p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                return (outp + " " + errp).Trim();
            }
            catch (Exception ex) { return "执行异常: " + ex.Message; }
        }
    }
}
