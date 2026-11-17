# SignPath Foundation 代码签名申请材料

> 申请入口：https://signpath.org/apply
> 备选邮件：oss-support@signpath.org
> 准备日期：2026-11-17
> 维护人：Alaikis.com Inc / @alaikis (GitHub)
>
> ## ✅ 申请状态
> **已提交（2026-11-17）**：通过 signpath.org/apply 表单提交成功，返回「Form submitted / Thank you, we'll be in touch soon」。
> - 提交邮箱：`alex@laialex.com`
> - 申请字段见下方「申请表字段」
> - 等待 SignPath 人工审核（通常 1-3 个工作日，可能邮件追问，请留意 alex@laialex.com）
> - 批准后按下方「申请后流程」接入 GitHub Actions 签名

---

## 申请表字段（按 signpath.org/apply 在线表单）

### 1. Project / Repository URL
```
https://github.com/alaikis/pipprinter
```
公开仓库，主开发源。

### 2. License
```
Mulan PSL v2 (Mulan Permissive Software License, Version 2)
```
- OSI-approved international license（2020-02-14 通过 OSI 认证，首个中英双语国际开源许可证）
- 根目录 `LICENSE` 含全文：https://github.com/alaikis/pipprinter/blob/master/LICENSE
- 许可证链接：http://license.coscl.org.cn/MulanPSL2

### 3. Download / Release URL
```
官方下载页：https://www.litchio.com
开源说明：https://www.litchio.com/open-source
```
- 安装版 exe：`PipPrinter-Setup-<ver>.exe`
- 绿色版 zip：`PipPrinter-win-x64-<ver>.zip`
- 当前最新已发布版本：v1.01.003

### 4. Project Description（直接复制）
```
Lychee Printing Assistant (PipPrinter) is a Windows 64-bit desktop application
by Alaikis.com Inc that provides silent printing, print automation, multi-format
document printing, and network-file printing. It bundles an embedded local
service exposing REST and WebSocket endpoints so that WMS / ERP / ops systems
can drive printing over HTTP.

Distribution artifacts: Windows installer (Inno Setup .exe) and portable
(.zip) build, both self-contained (no pre-installed .NET runtime required).

The project is actively maintained, already released (v1.01.000 through
v1.01.003), and fully documented at https://www.litchio.com/developers.
Built from the public GitHub repository using .NET 10 SDK on Windows.
```

### 5. Maintainer Email
```
（您填写，建议使用 alex@laialex.com 或项目维护邮箱）
```

---

## 团队角色（Code signing policy 要求）

- **Authors / Reviewers**: `@alaikis`（GitHub，仓库 Owner，负责源码与构建）
- **Approvers**: `@alaikis`（负责每次发布签名审批）
- MFA 状态：GitHub 已启用 MFA；SignPath 注册后需同步启用

---

## 申请前自检清单（SignPath 硬性条件）

- [x] 无恶意软件 / PUP
- [x] OSI 认证许可证（Mulan PSL v2，OSI 国际类别）
- [x] 无专有/闭源组件混入（仅标准 .NET / 系统库）
- [x] 活跃维护（最近 commit 持续更新）
- [x] 已发布（v1.01.000 ~ v1.01.003，含安装版与绿色版）
- [x] 功能已文档化（官网 /developers + /open-source + README）
- [x] 公开源码仓库在 GitHub（https://github.com/alaikis/pipprinter）
- [x] 仓库根含 LICENSE + README（含 Code signing policy）
- [x] 团队 MFA（GitHub 已启用）
- [x] 隐私政策页存在（https://www.litchio.com/privacy）

---

## 申请后流程

1. 提交 → SignPath 人工审核（通常 1-3 个工作日，可能邮件追问）
2. 批准后：在 https://app.signpath.io 创建组织 + 项目（slug 建议 `pipprinter`）
3. 安装 SignPath GitHub App 到 `alaikis/pipprinter`
4. 配置 Trusted Build System（GitHub）
5. 配置 Artifact Configuration（exe 签名 + 强制 product name / version 元数据）
6. 创建 API Token（Submitter 权限）
7. GitHub Secrets：
   - `SIGNPATH_API_TOKEN`
   - `SIGNPATH_ORGANIZATION_ID`
   - `SIGNPATH_PROJECT_SLUG` = `pipprinter`
   - `SIGNPATH_SIGNING_POLICY_SLUG` = `release-signing`
8. 在 `.github/workflows/release.yml` 加签名步骤（dotnet publish 后签 exe，NSIS 打包后签 installer）
9. 用测试证书先跑通 `workflow_dispatch`，再打正式 tag
10. 替换当前 `printer/installer/sign-exe.cmd` / `sign-exe.ps1` 本地自签名方案

---

## 本地降级方案保留

- 现有自签名 `installer/alaikis-code-signing.pfx` 保留作本地调试（不进 GitHub，已 .gitignore）
- SignPath 审批 + CI 全链路跑通前，发布包继续走本地签名
