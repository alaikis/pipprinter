// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Windows.Forms;
using DuoAssistant.Utils;
using Microsoft.Win32;

namespace DuoAssistant.Services
{
    /// <summary>系统级集成：开机自启。</summary>
    public class BaseServices
    {
        private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private static string AppName => "PipPrinter";

        /// <summary>
        /// 写入 HKCU\...\Run（无需管理员权限）。原实现写 HKLM 且路径缺少 .exe，
        /// 在普通权限下会静默失败，这里一并修正。
        /// </summary>
        public void StartWhenSystemStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true) ??
                                 Registry.CurrentUser.CreateSubKey(RunPath, true))
                {
                    key?.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
                Tools.Logger("已设置开机自启: " + Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                Tools.Logger("设置开机自启失败: " + ex.Message);
            }
        }

        public void DisableAutoStart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true))
                    key?.DeleteValue(AppName, false);
                Tools.Logger("已取消开机自启");
            }
            catch (Exception ex)
            {
                Tools.Logger("取消开机自启失败: " + ex.Message);
            }
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, false))
                    return key?.GetValue(AppName) != null;
            }
            catch { return false; }
        }
    }
}
