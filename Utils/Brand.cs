// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
namespace DuoAssistant.Utils
{
    /// <summary>
    /// 产品品牌常量。所有面向用户的名称统一从这里取，避免散落硬编码。
    /// 英文名 Lychee Printing Assistant，中文名 荔枝打印助手。
    /// 程序集/可执行文件名保持 PipPrinter（改动会破坏更新器/快捷方式/注册表路径）。
    /// </summary>
    public static class Brand
    {
        public const string NameEn = "Lychee Printing Assistant";
        public const string NameCn = "荔枝打印助手";
        public const string Title = "Lychee Printing Assistant — 荔枝打印助手";
        public const string Exe = "PipPrinter.exe";
        public const string Company = "Alaikis Limited";
        public const string CompanyUrl = "https://alaikis.com";
        // 版本规范 vMAJOR.FEATURE.PATCH：补丁 001→002；大变更 FEATURE+1；重大重构 MAJOR+1
        public const string Version = "1.01.001";
    }
}
