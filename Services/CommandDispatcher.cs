// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Services
{
    /// <summary>
    /// 命令分派单一入口：WebSocket、REST、WinForms UI 都走这里。
    /// 使用白名单限制可反射调用的方法，避免网络侧任意方法调用。
    /// </summary>
    public static class CommandDispatcher
    {
        private static readonly PrintServices Services = new PrintServices();
        private static readonly object Gate = new object();
        private static HashSet<string> _allowed;

        public static HashSet<string> Allowed
        {
            get
            {
                lock (Gate)
                {
                    if (_allowed == null)
                    {
                        var configured = SettingsStore.Instance.Current.AllowedCommands;
                        _allowed = new HashSet<string>(
                            configured == null || configured.Count == 0
                                ? typeof(PrintServices).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                    .Select(m => m.Name)
                                : configured,
                            StringComparer.OrdinalIgnoreCase);
                    }
                    return _allowed;
                }
            }
        }

        public static void ResetCache()
        {
            lock (Gate) _allowed = null;
        }

        /// <summary>列出全部可用命令（供文档页展示）。</summary>
        public static List<string> ListCommands() => Allowed.OrderBy(x => x).ToList();

        /// <summary>
        /// 执行一条命令。payload 为 JSON 字符串；缺失 cmd 时可从 payload.cmd 推断由调用方传入。
        /// </summary>
        public static string Execute(string cmd, string payload)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return Result.error("缺少 cmd", cmd: "Notify");
            try
            {
                if (!Allowed.Contains(cmd))
                    return Result.error("命令不在允许列表: " + cmd, 403, cmd);

                var method = typeof(PrintServices).GetMethod(cmd,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    null, new[] { typeof(string) }, null);
                if (method == null)
                    return Result.error("命令未实现: " + cmd, 404, cmd);

                var json = string.IsNullOrWhiteSpace(payload) ? "{}" : payload;
                // 提前校验 JSON，给出更友好的错误
                if (!IsValidJson(json)) return Result.error("请求体不是合法 JSON", 400, cmd);

                var result = method.Invoke(Services, new object[] { json });
                return result as string ?? Newtonsoft.Json.JsonConvert.SerializeObject(result);
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                Tools.Logger("命令 " + cmd + " 执行异常: " + inner);
                return Result.error(inner.Message, 500, cmd);
            }
            catch (Exception ex)
            {
                Tools.Logger("命令 " + cmd + " 分派异常: " + ex);
                return Result.error(ex.Message, 500, cmd);
            }
        }

        public static bool IsValidJson(string s)
        {
            try { JToken.Parse(s); return true; }
            catch { return false; }
        }
    }
}
