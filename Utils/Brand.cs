namespace DuoAssistant.Utils
{
    /// <summary>
    /// 产品品牌常量。所有面向用户的名称统一从这里取，避免散落硬编码。
    /// 英文名 PipPrinter，中文名 小莱打印助手。
    /// </summary>
    public static class Brand
    {
        public const string NameEn = "PipPrinter";
        public const string NameCn = "小莱打印助手";
        public const string Title = "PipPrinter — 小莱打印助手";
        public const string Exe = "PipPrinter.exe";
        public const string Company = "Alaikis Limited";
        public const string CompanyUrl = "https://alaikis.com";
        // 版本规范 vMAJOR.FEATURE.PATCH：补丁 001→002；大变更 FEATURE+1；重大重构 MAJOR+1
        public const string Version = "1.01.000";
    }
}
