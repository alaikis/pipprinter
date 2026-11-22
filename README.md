# 荔枝打印助手 / Lychee Printing Assistant

**PipPrinter** 是「荔枝软件」办公套件中的**打印助手**：Windows 64 位桌面应用，提供静默打印、打印自动化、多格式文档直打与网络文件打印能力，并内嵌本机服务对外暴露 REST / WebSocket 接口，供 WMS / ERP / 运维等系统通过 HTTP 直接驱动打印。

- 源码主仓库：https://codeberg.org/alaikis/pipprinter
- 官方下载：https://www.litchio.com （安装版 exe 与绿色版 zip）
- 开发者文档：https://www.litchio.com/developers
- 开源与运行方式：https://www.litchio.com/open-source

## 许可

本项目采用 **Mulan PSL v2**（木兰宽松许可证第 2 版）开源许可，已通过 [OSI](https://opensource.org/licenses) 认证。许可证全文见 [LICENSE](LICENSE)。

## 功能

- **静默打印**：PDF / Office / OFD / XPS / 图片 / 文本 / HTML 等常见文档直接打印，无需依赖目标软件。
- **模板 + 数据打印**：模板占位变量配合数据批量出单。
- **网络文件打印**：通过 URL / UNC / 本机路径传入 `target` 字段即可打印，自动识别类型。
- **本机服务**：客户端内嵌的本机服务随客户端自动运行（默认端口 `19861`，可在设置中调整），对外提供 REST 与 WebSocket 端点，供第三方系统集成。
- **打印参数**：份数 / 双面 / 纸张 / 方向 / 边距 / 页序 / 打印队列等，未显式传参时回退应用默认值。

## 从源码构建

需要 **Windows 64 位** 与 **.NET 10 SDK**。

```bash
# 还原 + 构建
dotnet build PipPrinter.sln -c Release

# 自包含单文件发布（用户无需预装 .NET 运行时）
dotnet publish PipPrinter.csproj -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true
```

安装包使用 Inno Setup 脚本 `installer/PipPrinter.nsi` 生成。

## Code signing policy

本项目通过 [SignPath.io](https://about.signpath.io) 进行代码签名，证书由 **SignPath Foundation** 签发（开源项目免费）。

- **免费代码签名由 SignPath.io 提供，证书由 SignPath Foundation 颁发。**
- 签名产物为 Windows 安装版 exe 与绿色版 zip 内的可执行文件。
- 团队角色：
  - **Authors / Reviewers**：`alaikis`（GitHub 仓库维护者，负责源码与构建）
  - **Approvers**：`alaikis`（负责每次发布的签名审批）
- 隐私政策：https://www.litchio.com/privacy 。除用户在「设置」中明确配置的本机服务与可选的 Active-Online 中继外，本程序不向任何外部网络系统传输信息。

## 免责声明

本软件按「现状」提供，不含任何明示或默示担保。详见 [LICENSE](LICENSE)。

---

Copyright (c) 2026 Alaikis.com Inc
