// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Runtime.InteropServices;

namespace DuoAssistant.Utils
{
    /// <summary>
    /// 用户「空闲」判定：基于 Win32 GetLastInputInfo，返回自上次键盘/鼠标输入
    /// 以来的时长。零网络、零探测，纯本地系统调用，对系统无副作用。
    ///
    /// 用于「闲时静默升级」：只有当用户持续无输入达 SilentUpdateIdleMinutes
    /// 时，才允许后台下载/替换、启动延迟重启，避免在用户正操作时硬替换运行中 exe。
    ///
    /// 注意：
    /// - 跨会话/多用户桌面：GetLastInputInfo 返回的是系统级最近输入，桌面锁屏/会话切换
    ///   会被当作「空闲」，行为可接受（静默升级在锁屏后执行是合理的）。
    /// - 时间戳用 32 位毫秒（自系统启动），溢出约 49.7 天；返回 -1 时保守按「未空闲」处理。
    /// </summary>
    public static class IdleDetector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime; // 自系统启动以来的毫秒数（32 位）
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        /// <summary>自上次键鼠输入以来的毫秒数；获取失败返回 -1（保守视为未空闲）。</summary>
        public static long LastInputElapsedMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return -1;
            long now = GetTickCount();
            long last = info.dwTime;
            // 处理 32 位 tick 计数回绕：last > now 意味着计数器回绕过，差值需加 2^32。
            long diff = now - last;
            if (diff < 0) diff += (1L << 32);
            return diff;
        }

        /// <summary>用户是否已持续空闲至少 idle 时长。idle 为负/0 视为不要求空闲（直接返回 true）。</summary>
        public static bool IsIdle(TimeSpan idle)
        {
            if (idle <= TimeSpan.Zero) return true;
            long ms = LastInputElapsedMs();
            if (ms < 0) return false; // 获取失败：保守视为未空闲
            return ms >= idle.TotalMilliseconds;
        }

        /// <summary>用户是否已持续空闲至少 N 分钟（整数）。0 或负数视为不要求空闲。</summary>
        public static bool IsIdle(int minutes)
        {
            return IsIdle(TimeSpan.FromMinutes(minutes));
        }
    }
}
