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

---

## 追加验收 · v1.00.001 重建 + 自动更新端到端实测（2026-11-14 05:05）

### 流水线（Gitee Go）实测
- 用户网页创建流水线后，完整配置写入已注册的 `流水线-202611140444.yml`（保留唯一标识），删除未注册副本
- tag `v1.00.001` 推送 → 流水线自动构建（约 3 分钟）→ 自动创建 Gitee Release + 附件（zip + latest.json）
- 强制前移 tag 重建 Release 成功（`allowUpdate: true`），sha256 更新为 `e907352a…203a6`，version 字段无前导空格（printf 单行修复生效）

### 自动更新端到端实测（真实 Gitee 链路）
| 步骤 | 结果 |
|---|---|
| 旧实例 v1.00.000 启动 | `/api/status` version=1.00.000 |
| `GET /api/update/check` | hasUpdate=True, remote=1.00.001（经 API v5 解析 Release 附件直链） |
| `POST /api/update/apply` | 200, installed=True（下载 68MB → SHA256 校验 → 旧 exe 改名让位 → 新 exe 就位） |
| 重启后 | version=**1.00.001** ✅ |
| `PipPrinter.exe.old-*` 残留 | 0（启动时 CleanupLeftovers 清理）✅ |
| machineId 用户数据 | PP-6218b020… 保持不变（config/ 未触碰）✅ |

### 修复的缺陷
1. Gitee 不支持 `/releases/latest/download/` 别名 → 改为 API v5 解析最新 Release 的 latest.json 附件地址
2. `Directory.Move` 移动安装目录必失败（运行中 exe 镜像锁 + log4net 日志句柄）→ 改为「旧 exe 改名让位 + 新 exe 就位 + 仅覆盖 docs/pdfreader/Resource」策略，带回滚
3. YAML 折叠块把 shell 续行符 `\` 折成空格注入 version 字段 → printf 改单行

---

## 追加验收 · v1.00.002 NSIS 安装向导 + 双制品流水线（2026-11-14 05:38）

### 安装向导（NSIS，Alaikis Limited）
- 双语向导（简中/英文）、每用户安装（%LOCALAPPDATA%\Programs，免管理员）
- 组件可选：开始菜单 / 桌面快捷方式 / 开机自启（HKCU）
- 升级保留 config\appsettings.json 用户配置；卸载器可选保留用户数据
- 本地实测：静默安装 → 运行（/api/status version=1.00.002，exe Company=Alaikis Limited）→ 静默卸载 → 注册表/自启/开始菜单全部清除 ✅

### 流水线双制品（Linux makensis 跨平台构建）
- tag v1.00.002 → 流水线 ~3 分钟产出：绿色版 zip（68.4MB）+ 安装版 Setup.exe（49.7MB）+ latest.json（新增 setupUrl 字段）
- SHA256 校验通过；Linux 构建的 Setup.exe 在 Windows 实测安装/运行/卸载全部正常 ✅

### 修复的坑
1. Windows makensis 的 File 命令只认反斜杠路径（正斜杠 glob 报 no files found），Linux 相反 → SEP 宏注入分隔符（本地默认 `\`，流水线传 `-DSEP=/`）
2. .nsi 必须 UTF-8 带 BOM，否则 Bad text encoding
3. 静默卸载（/S）时 MessageBox 缺 /SD 导致执行中断 → 加 /SD IDYES（仅影响静默模式）
