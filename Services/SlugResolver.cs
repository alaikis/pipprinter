// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;

namespace DuoAssistant.Services
{
    /// <summary>智能调度的结果：把「调用方给的 printer 字段」解析成一条明确的发送通道。</summary>
    public class ResolvedPrinter
    {
        /// <summary>
        /// 发送通道：
        ///   Net    → 网络直发（host:port / 别名指向网络目标），交给 NetPrinterService.PrintSmart
        ///   Driver → 本机 Win32 驱动管线（Windows 已装打印机名），交给 ExtendedPrintService.Print
        /// </summary>
        public string Channel { get; set; } = "Driver";

        /// <summary>
        /// 实际下发目标：
        ///   Net 通道  → host（可带端口，交给 PrintSmart 自动识别协议 / 查缓存）
        ///   Driver 通道 → Windows 已安装打印机名
        /// </summary>
        public string Printer { get; set; } = "";

        /// <summary>是否由别名表命中（用于返回给调用方核对，"slug → 实际目标" 的透明化）。</summary>
        public bool FromSlug { get; set; }

        /// <summary>命中的别名名（FromSlug=true 时）。</summary>
        public string Slug { get; set; } = "";

        /// <summary>调度说明（人类可读，便于对接方核对走了哪条路）。</summary>
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// 四级智能调度器：把调用方传的 printer 字段（可以是 友好别名 / host:port / 本机打印机名）
    /// 统一解析成「该走网络直发 还是 本机驱动」，把复杂的目标与协议细节藏在一个友好 slug 后面。
    ///
    /// 优先级（从高到低）：
    ///   ① 已是 host:port / 裸 IP（含 tcp:// 前缀）     → 网络直发，自动识别协议
    ///   ② 命中本地别名表 PrinterSlugStore               → 按条目 Target/Mode 转成 ① 或 ③
    ///   ③ 是 Windows 已安装打印机名                     → 本机驱动管线
    ///   ④ 兜底                                          → 系统默认打印机（本机驱动管线）
    ///
    /// 设计原则：纯本地、只读系统（只枚举已安装打印机名，不注册/不删/不装驱动），
    /// 不碰注册表；网络目标一律走免驱动直发，驱动目标走 Windows 原生可靠通道。
    /// </summary>
    public static class SlugResolver
    {
        /// <summary>解析 printer 字段，返回该走哪条通道及实际目标。</summary>
        public static ResolvedPrinter Resolve(string printer)
        {
            var raw = (printer ?? "").Trim();

            // ① 已是网络目标：host:port / 裸 IP / tcp://…
            if (NetPrinterService.IsNetworkTarget(raw))
                return new ResolvedPrinter
                {
                    Channel = "Net",
                    Printer = raw,
                    FromSlug = false,
                    Detail = "printer 直接是 host:port，走网络直发（自动识别 IPP/RAW）"
                };

            // ② 命中本地别名表
            var slug = PrinterSlugStore.Get(raw);
            if (slug != null)
            {
                var target = (slug.Target ?? "").Trim();
                var mode = (slug.Mode ?? "auto").Trim().ToLowerInvariant();

                // driver 模式：别名其实指向一台本机已装打印机
                if (mode == "driver")
                    return new ResolvedPrinter
                    {
                        Channel = "Driver",
                        Printer = target,
                        FromSlug = true,
                        Slug = slug.Slug,
                        Detail = "别名「" + slug.Slug + "」→ 本机驱动「" + target + "」（mode=driver）"
                    };

                // 目标本身是网络目标 → 直发（auto/ipp/raw 都交给 PrintSmart 的缓存与自动识别）
                if (NetPrinterService.IsNetworkTarget(target))
                    return new ResolvedPrinter
                    {
                        Channel = "Net",
                        Printer = target,
                        FromSlug = true,
                        Slug = slug.Slug,
                        Detail = "别名「" + slug.Slug + "」→ 网络目标 " + target +
                                 "（mode=" + (mode == "" ? "auto" : mode) + "）"
                    };

                // 目标是 Windows 已装打印机名 → 走驱动
                if (!string.IsNullOrWhiteSpace(target))
                    return new ResolvedPrinter
                    {
                        Channel = "Driver",
                        Printer = target,
                        FromSlug = true,
                        Slug = slug.Slug,
                        Detail = "别名「" + slug.Slug + "」→ 本机驱动「" + target + "」"
                    };

                // 目标留空 / auto：回退系统默认（本机驱动管线）
                return new ResolvedPrinter
                {
                    Channel = "Driver",
                    Printer = "",
                    FromSlug = true,
                    Slug = slug.Slug,
                    Detail = "别名「" + slug.Slug + "」目标为空，回退系统默认打印机"
                };
            }

            // ③ 是 Windows 已安装打印机名 → 本机驱动管线
            if (IsInstalledPrinterName(raw))
                return new ResolvedPrinter
                {
                    Channel = "Driver",
                    Printer = raw,
                    FromSlug = false,
                    Detail = "printer 是已安装打印机「" + raw + "」，走本机驱动"
                };

            // ④ 兜底：交给系统默认
            return new ResolvedPrinter
            {
                Channel = "Driver",
                Printer = "",
                FromSlug = false,
                Detail = "printer「" + raw + "」非网络目标/非别名/非已装打印机，回退系统默认"
            };
        }

        /// <summary>某 Windows 已安装打印机名是否存在（只读枚举，不注册/不删）。</summary>
        private static bool IsInstalledPrinterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var installed = new PrinterService().GetNames();
                return installed.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
