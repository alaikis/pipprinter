# 验收报告 — printer-desktop-enhancement

- 验收时间：2026-11-13 15:10 (GMT+8)
- 环境：Windows / .NET SDK 10.0.300 / net10.0-windows / Release
- 应用：PipPrinter（小莱打印助手）v2.0.0
- 结论：**通过**（1 项未执行：远端 git push，属外部写操作，等待用户确认）

## 1. 构建

```
dotnet build PipPrinter.csproj -c Release
→ 已成功生成。0 个警告  0 个错误
```

输出目录资产齐全：`PipPrinter.exe`、`config/appsettings.json`、`config/log4net.config`、
`docs/web/{index,ws,api,dev}.html + styles.css`、`Resource/bitbug_favicon.ico`、
`pdfreader/SumatraPDF.exe`、Fleck / log4net / Newtonsoft.Json / iTextSharp 程序集。

## 2. 启动与托盘行为

| 场景 | 方法 | 结果 |
|---|---|---|
| 无参启动 | `EnumWindows` 枚举标题窗口 | 仅存在 `GDI+ Window (PipPrinter.exe)` 隐藏辅助窗口，**主窗口不可见** ✅ |
| 服务就绪 | `GET /api/status` | 返回 200，framework `10.0.8` ✅ |
| 托盘提示 | 启动气泡截图 | 显示「小莱打印助手 / 已启动，右键托盘图标可管理打印任务与文档服务」✅ |
| `--show` 启动 | `PipPrinter.exe --show` + 屏幕截图 | 主窗口显示，标题 `PipPrinter — 小莱打印助手`，**默认选中「打印队列」页** ✅ |
| 主窗口内容 | 截图 | 6 个页签、工具栏 6 个按钮、ListView 列头与数据行、状态栏均正常渲染，无重叠/空白 ✅ |

> 修复记录：首轮实测发现 `Application.Run(Form)` 会强制 Show 主窗体，覆盖构造函数里的
> `Hide()`；外部强制 ShowWindow 还会导致子控件未布局而呈现空白。已改为
> `Application.Run()` + `SetVisibleCore` 闸门 + `--show` 参数，并复测通过。

## 3. REST API（http://127.0.0.1:19861）

| 端点 | 结果 |
|---|---|
| `GET /` | 返回 `index.html`，标题「总览 · PipPrinter」 ✅ |
| `GET /api/status` | `app=PipPrinter`、`appCn=小莱打印助手`、23 种支持扩展名、队列摘要、运行时长 ✅ |
| `GET /api/printers` | 枚举到本机 4 台打印机，含驱动名、端口、默认标记、纸张规格 ✅ |
| `GET /api/queue` | 返回任务列表与 summary ✅ |
| `GET /api/disk` | 返回 3 块磁盘容量与扫描根目录 ✅ |
| `GET /api/docs` | 返回 4 个 HTML 页面 ✅ |
| `GET /api/commands` | 返回 20 条命令 ✅ |
| `POST /api/print` | 入队成功，返回 `jobId` 与初始 `pending` 任务 ✅ |
| `POST /api/command` | `GetQueue` / `ScanDisk` / `CleanDisk` 均正确 ✅ |
| 白名单拦截 | `{"cmd":"GetType"}` → `403 命令不在允许列表` ✅ |
| 非法 JSON | → `400 请求不是合法 JSON` ✅ |

## 4. WebSocket（ws://0.0.0.0:19860）

Node 22 原生 WebSocket 客户端实测依次收到：

```
RECV operator=startPrint   code=200      // 连接欢迎
RECV operator=GetStatus    code=200      // 命令响应
RECV operator=QueueChanged code=200      // 队列变更主动推送
RECV operator=QueuePrint   code=200      // 入队响应
```

## 5. 打印队列

- 入队一个不存在的文件 → 后台工作线程消费，状态 `pending → printing → failed`，
  `Error="文件不存在: ..."`，`Attempts=1`，`data/queue.json` 正确落盘 ✅
- 崩溃恢复：手工把 `queue.json` 中任务改为 `printing` 后重启，日志出现
  `崩溃恢复: 1 个任务重置为 pending`，任务被重新消费 ✅
- 日志修复：`log4net` 仓储不匹配导致分级日志始终为空的问题已修复，
  `log/debug/2026-11-13.log` 现能记录启动、恢复、打印、WS/Web 启动全过程；
  `log/err/` 无内容（无异常）✅

## 6. 磁盘清理安全

```
POST /api/command {"cmd":"CleanDisk","paths":["C:\\Windows\\notepad.exe","...\\Desktop\\important.docx"]}
→ {"DryRun":true,"Requested":2,"Deleted":0,"Skipped":2,"FreedBytes":0,"Errors":[]}
```

默认试运行、白名单与允许根目录二次校验均生效 ✅

## 7. 需求对应

| 用户需求 | 交付 |
|---|---|
| 英文名 PipPrinter / 中文名 小莱打印助手 | `Utils/Brand.cs` 常量；程序集/项目/sln/exe 全部改名；UI、托盘、测试页、文档站、`/api/status` 双名称 ✅ |
| 默认打印队列 | `TabControl.SelectedIndex = 1`，截图确认 ✅ |
| 托盘双击显示主窗口 | `NotifyIcon.MouseDoubleClick → ShowMain(-1)`；`--show` 与窗口显示链路已实测 ✅ |
| 托盘右键功能 | ContextMenuStrip 8 项 ✅ |
| 打印队列 | `PrintQueueService`（持久化/顺序/重试/恢复/推送）✅ |
| 本机打印机列表 | `PrinterService` + `/api/printers` ✅ |
| 硬盘清理 | `DiskCleanerService`（白名单/试运行/回收站）✅ |
| 更多打印文件类型 | PDF + Office + 图片 + HTML + 文本共 23 种扩展名 ✅ |
| Web 服务开发/使用文档 | Kestrel 静态站 4 页 + 10 个 REST 端点 ✅ |
| 升级依赖最新稳定版 | net10.0-windows LTS + 5 个包逐一核对 ✅ |
| /impetus 开发规范 | openspec 四件套 + `.impetus.yaml` + 本验收报告 ✅ |

## 8. 遗留 / 建议

- `git push` 未执行（外部写操作，等待确认）
- WS 默认监听 `0.0.0.0`，建议生产环境把 `wsHost` 改为 `127.0.0.1` 或加防火墙规则
- 托盘图标可能被 Windows 11 收进「^」溢出区，用户可在系统设置中固定显示
- iTextSharp 5.5.13.6 为 .NET Framework 兼容包（NU1701 已在 csproj 显式豁免）；
  当前 SumatraPDF 分流路径不直接调用它，可在后续 change 中评估移除
