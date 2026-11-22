// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Security.Principal;
using DuoAssistant.Utils;

namespace DuoAssistant.Services
{
    /// <summary>身份提供者类型。</summary>
    public static class IdpKind
    {
        public const string Ldap = "ldap";
        public const string Local = "local";
    }

    /// <summary>一次身份解析结果：userId 直接对齐配额/策略/审计的 user 维度。</summary>
    public class IdentityResult
    {
        public bool Ok { get; set; }
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Provider { get; set; } = "";
        public List<string> Groups { get; set; } = new List<string>();
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// 身份服务：可选 LDAP/AD 绑定认证 + 用户组查询；未配置 LDAP 时用 Windows 本机登录身份兜底。
    /// 解析出的 userId 供配额、策略、安全打印、审计统一使用，解决「userId 由调用方任意伪造」的根因。
    /// 认证失败静默返回 Ok=false，不抛异常打断调用方。
    /// </summary>
    public static class LdapAuthService
    {
        /// <summary>当前启用的身份提供者列表（供 /api/auth/providers 展示）。</summary>
        public static List<string> Providers()
        {
            var list = new List<string> { IdpKind.Local };
            var cfg = SettingsStore.Instance.Current;
            if (!string.IsNullOrWhiteSpace(cfg.LdapServer) && !string.IsNullOrWhiteSpace(cfg.LdapBaseDn))
                list.Insert(0, IdpKind.Ldap);
            return list;
        }

        /// <summary>
        /// 认证用户。配置 LDAP 时走绑定认证并返回 DN/组；否则仅接受 Windows 本机身份（无需密码）。
        /// </summary>
        public static IdentityResult Authenticate(string user, string password)
        {
            var cfg = SettingsStore.Instance.Current;
            if (!string.IsNullOrWhiteSpace(cfg.LdapServer) && !string.IsNullOrWhiteSpace(cfg.LdapBaseDn))
                return LdapBind(user, password, cfg);
            return LocalIdentity(user);
        }

        /// <summary>Windows 本机登录身份兜底：无 LDAP 时把 userId 绑定到真实登录用户。</summary>
        public static IdentityResult LocalIdentity(string requestedUser = null)
        {
            string windowsUser;
            try
            {
                windowsUser = WindowsIdentity.GetCurrent()?.Name
                              ?? Environment.UserDomainName + "\\" + Environment.UserName;
            }
            catch { windowsUser = Environment.UserName; }
            // 调用方指定用户且等于本机用户（或明确允许覆盖）时才采信，否则固定为本机身份
            var uid = string.IsNullOrWhiteSpace(requestedUser) ? windowsUser : requestedUser;
            if (!string.IsNullOrWhiteSpace(requestedUser) &&
                !string.Equals(requestedUser, windowsUser, StringComparison.OrdinalIgnoreCase) &&
                !cfg_AllowUserIdOverride())
                uid = windowsUser;
            return new IdentityResult
            {
                Ok = true, UserId = uid, DisplayName = uid, Provider = IdpKind.Local,
                Message = "本机身份（未配置 LDAP；userId 绑定 Windows 登录用户）"
            };
        }

        private static bool cfg_AllowUserIdOverride()
        {
            try { return SettingsStore.Instance.Current.AllowUserIdOverride; }
            catch { return false; }
        }

        private static IdentityResult LdapBind(string user, string password, AppSettings cfg)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
                return new IdentityResult { Ok = false, Provider = IdpKind.Ldap, Message = "缺少用户名或密码" };
            try
            {
                var server = cfg.LdapServer;
                int port = cfg.LdapPort > 0 ? cfg.LdapPort : 389;
                var ident = new LdapDirectoryIdentifier(server, port, false, false);
                var cred = new NetworkCredential(NormalizeUser(user, cfg), password);
                using (var conn = new LdapConnection(ident, cred, AuthType.Basic))
                {
                    conn.SessionOptions.ProtocolVersion = 3;
                    if (cfg.LdapUseSsl) conn.SessionOptions.SecureSocketLayer = true;
                    conn.Bind();
                }
                // 绑定成功：用只读连接取显示名与组（失败不影响认证结果）
                var groups = new List<string>();
                var display = user;
                try
                {
                    using var ro = new LdapConnection(new LdapDirectoryIdentifier(server, port, false, false));
                    ro.SessionOptions.ProtocolVersion = 3;
                    if (cfg.LdapUseSsl) ro.SessionOptions.SecureSocketLayer = true;
                    ro.Bind();
                    var filter = "(&(objectClass=user)(" + cfg.LdapUserAttribute + "=" + Escape(user) + "))";
                    var req = new SearchRequest(cfg.LdapBaseDn, filter, SearchScope.Subtree,
                        new[] { "displayName", "memberOf", "sAMAccountName", "uid" });
                    var res = (SearchResponse)ro.SendRequest(req);
                    foreach (SearchResultEntry entry in res.Entries)
                    {
                        if (entry.Attributes["displayName"]?.Count > 0)
                            display = entry.Attributes["displayName"][0]?.ToString() ?? display;
                        var attrs = entry.Attributes["memberOf"];
                        if (attrs != null)
                            foreach (var v in attrs) groups.Add(GroupCn(v?.ToString() ?? ""));
                        break;
                    }
                }
                catch (Exception ex) { Tools.Logger("LDAP 组查询失败(不影响认证): " + ex.Message); }

                return new IdentityResult
                {
                    Ok = true,
                    UserId = NormalizeUser(user, cfg),
                    DisplayName = display,
                    Provider = IdpKind.Ldap,
                    Groups = groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Message = "LDAP/AD 认证成功"
                };
            }
            catch (Exception ex)
            {
                Tools.Logger("LDAP 认证失败: " + ex.Message);
                return new IdentityResult { Ok = false, Provider = IdpKind.Ldap, Message = "认证失败: " + ex.Message };
            }
        }

        private static string NormalizeUser(string user, AppSettings cfg)
        {
            user = (user ?? "").Trim();
            if (string.IsNullOrWhiteSpace(cfg.LdapUserSuffix)) return user;
            return user.Contains("\\") || user.Contains("@") ? user : user + cfg.LdapUserSuffix;
        }

        private static string GroupCn(string dn)
        {
            if (string.IsNullOrWhiteSpace(dn)) return "";
            var first = dn.Split(',')[0];
            var eq = first.IndexOf('=');
            return eq >= 0 ? first.Substring(eq + 1) : first;
        }

        private static string Escape(string s) => (s ?? "")
            .Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28")
            .Replace(")", "\\29").Replace("\0", "\\00");
    }
}
