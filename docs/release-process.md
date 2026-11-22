# PipPrinter 发版与分发流程（SOP）

> 2026-11-22 固化。背景：此前分发全靠一次性手工操作，反复踩到 latest 指向乱序、
> latest.json 格式不匹配、Gitee 附件配额爆满三个坑。本 SOP + `scripts/release.py`
> 把流程固定下来。**令牌只从环境变量读取，绝不写入脚本 / 文档 / git。**

## 一、分发策略（谁放哪）

| 版本 | 托管 | 原因 |
|---|---|---|
| 当前版本三件套 | **Gitee Releases** | 客户端自动更新依赖 `Gitee /releases/latest` 找 latest.json，不能迁走 |
| 更早版本（如 1.01.003–009） | **Codeberg Releases** | Gitee 附件总配额 1 GB，放不下全部历史包 |
| 源码 | **Codeberg**（origin）+ Gitee / GitHub / 阿里云镜像 | 见 MEMORY.md |

官网 `www.litchio.com` 按 tag 合并两边资产：最新版取 Gitee，历史下载自动分流到 Codeberg。
合并逻辑在 `site/src/lib/releases.ts`（缓存 key `releases-v3`），**`site/` 不入本仓库**，官网独立部署。

## 二、令牌（环境变量，不落盘）

```bash
export GITEE_TOKEN=...       # Gitee 个人令牌，publish / verify 用（Bearer）
export CODEBERG_TOKEN=...    # Codeberg 个人令牌，mirror 用（token）
```

校验脚本无硬编码令牌：`grep -E '35ed76|b219b2|gto_' scripts/release.py` 应无输出。

## 三、新版本发布步骤

假设新版本 `1.01.013`：

1. **升版本号**：`Utils/Brand.cs`、`PipPrinter.csproj`、`headless/Litchio.Headless.csproj`、
   `headless/Program.cs`、`AoRelay/AoRelay.csproj` 全部同步。
2. **构建 + 打包三件套到 `out/`**（`out/` 已 gitignore，不入库）：
   - 绿色包：`out/PipPrinter-win-x64-1.01.013.zip`（文件名**无 v**，避免被 Gitee 源码归档 `v1.01.013.zip` 覆盖）
   - 安装版：`out/PipPrinter-Setup-v1.01.013.exe`
   - 全部 **self-contained**（用户无需预装 .NET）
3. **生成扁平 latest.json**（真实 size / sha256，不编造）：
   ```bash
   python scripts/release.py manifest 1.01.013
   ```
   产出 `out/1.01.013.json`，字段：`version / url / setupUrl / sha256 / size / notes`。
   **必须是扁平结构**，不能是 `assets[]` 嵌套——客户端 `UpdateService.cs` 与官网都读扁平字段。
4. **上传当前版本到 Gitee**：
   ```bash
   python scripts/release.py publish 1.01.013
   ```
   脚本会：找/建该 tag 的 Release → 删除同名旧附件（避免 Gitee 旧附件优先）→ 上传 zip/exe/latest.json。
5. **官网基线同步 + 部署**：更新 `site/` 的 version-matrix / VersionNote / releases-api 说明，
   然后 `cd site && npm run build && npx wrangler deploy`。
6. **复验**（必做）：
   ```bash
   python scripts/release.py verify
   ```
   三段检查全 PASS 才算完成：① Gitee `/releases/latest` 清单 ② 官网 `/api/v1/releases/latest`
   ③ 下载直链 200。

可选：把某个旧版迁到 Codeberg：
```bash
python scripts/release.py mirror 1.01.009
```
（幂等，已存在的附件跳过；Codeberg Releases 默认关闭，脚本会尝试开启。）

## 四、三个必记的坑（都踩过）

1. **Gitee `/releases/latest` 按 `created_at` 判定，不看版本号**。
   清库/批量重建 Release 会把时间戳打乱，导致 latest 指向旧版（曾指到 v1.00.002 / v1.01.003）。
   - 正常发版（按时间顺序新建）天然正确，无需干预。
   - 若重建过 Release，**必须**跑 `verify` 确认 latest 指向当前版本。
   - 官网不受此影响：已改为按版本号数值段排序（`byNewest`）。
2. **latest.json 必须扁平结构 + 真实校验值**。嵌套 `assets[]` 会让 size/sha256 变 null。
   sha256 只能来自真实产物（`manifest` 子命令现算），禁止编造。
3. **Gitee 附件总配额 1 GB**。绿色包 zip 文件名不能以 `v` 开头（`v<tag>.zip` 会被
   源码归档覆盖，几小时后直链 404）；用无 v 的 `PipPrinter-win-x64-<version>.zip`。
   历史包放 Codeberg；同名附件重传前先删旧附件。

## 五、本地网络注意

本机 Bash 环境被注入代理 `127.0.0.1:64727`，curl 对 `www.litchio.com` 的 TLS 握手会失败
（Gitee 正常）。`release.py` 的只读请求已加 PowerShell 兜底，故 `verify` 在本机可正常跑。
换环境时 curl 优先，兜底仅在失败时触发。

## 六、当前基线（最后更新 2026-11-22）

- 线上最新：**v1.01.012**（source:live / stale:false）
- 绿色包 size `97715812`、sha256 `dfd6b251d44ba726f6eb9037af9adc6e24c5eb5cb0b09bd55d7770fdbb834cfc`
- Gitee 当前 Release id 见 `verify` 输出；历史 v1.01.003–009 在 Codeberg。
