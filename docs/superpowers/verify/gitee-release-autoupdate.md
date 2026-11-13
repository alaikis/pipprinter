# 验收报告 · Gitee 发布链路 + 自动更新 + 本机指纹

日期：2026-11-13 · 变更：feat(gitee-release) db66f1c · tag v2.0.0

## 交付内容

1. **Gitee 远程仓库** `https://gitee.com/alaikiscom/pipprinter.git`
   - `master` 分支与 `v2.0.0` tag 已推送（Codeup origin 同步推送）
2. **Gitee Go 发布流水线** `.workflow/pipeline.yml`
   - 触发：push tag 前缀 `v`（如 v2.0.0、v2.1.3）
   - Stage 1 `build@dotnet`（.NET 10）：`dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableWindowsTargeting=true`，打 zip、生成 sha256 的 latest.json
   - Stage 2 `release@gitee`：自动创建 Gitee Release 并附 zip + latest.json
3. **自动更新（客户端）** `Services/UpdateService.cs` + `CheckUpdate` / `ApplyUpdate` 命令 + REST `/api/update/check`、`POST /api/update/apply`
   - 语义化版本比对；manifest 无 version 字段时不误报（已修复验证）
   - 下载 → SHA256 校验 → 旧目录整体备份（`*.bak-时间戳`）→ 替换 → 提示重启
4. **本机指纹** `Utils/MachineFingerprint.cs` + `GetMachineFingerprint` 命令 + REST `/api/machine`
   - MachineGuid + C 盘卷序列号 + OS + 用户名 → SHA1 → `PP-` 24 位指纹，跨浏览器/会话稳定
   - 持久化到 `config/appsettings.json` 的 `machineId`
5. **文档/帮助入口**
   - 托盘：打开文档站点 / 查看本机指纹 / 检查更新
   - 设置页：文档与帮助、本机指纹（可复制）、自动更新（检查/安装/重启）三个区块
6. **本地正式包** `dist/PipPrinter-win-x64-v2.0.0.zip`（68,180,130 B，sha256 `793456bc…5233`）+ `dist/latest.json`

## 运行时验证（release/publish 实测）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 警告 0 错误 |
| `GET /api/status` | version=2.0.0.0，WS/Web/队列全部正常 |
| `GET /api/machine` | machineId=PP-6218b0208a423d2e899d2780（已生成） |
| `GET /api/update/check`（Release 未发布时） | HasUpdate=false + "manifest 缺少 version 字段" 修复生效 |
| `GET /api/commands` | 23 个命令，含 GetMachineFingerprint/CheckUpdate/ApplyUpdate |
| 日志 | 所有服务启动正常，无 err/fatal 记录 |

## 遗留（Gitee 侧一次性配置，留给用户）

1. Gitee 仓库开通 Gitee Go（仓库 → 流水线 → 开通）
2. 若 `release@gitee` 需要令牌，在仓库环境变量配置 `GITEE_TOKEN`
3. 推送 tag 后流水线应自动触发；也可手动在 Gitee 网页编辑流水线（代码视图）保存确认识别
4. 手动发版（不等流水线）：Gitee Release 页创建 v2.0.0，上传 `dist/PipPrinter-win-x64-v2.0.0.zip` 与 `dist/latest.json`
5. latest.json 的 url 用 `releases/download/v2.0.0/...`；客户端 UpdateManifestUrl 指向 `releases/latest/download/latest.json`（最新 release）
