// Copyright (c) 2026 Alaikis.com Inc
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DuoAssistant.Services;
using DuoAssistant.Utils;
using Newtonsoft.Json.Linq;

namespace DuoAssistant.Views.Pages
{
    /// <summary>
    /// 「打印管理」页：配额、策略、报表、墨量、安全打印释放、通知的本地管理面板。
    /// 全部通过既有命令读取/写入，不重复实现业务逻辑；命令不可用（Rest 层未启用）时给出空白而非崩溃。
    /// </summary>
    public class ManageTab : UserControl, IRefreshable
    {
        private readonly TextBox _userId = Ui.Input(140);
        private readonly NumericUpDown _monthly = Ui.Number(0, 1000000, 0, 100);
        private readonly NumericUpDown _color = Ui.Number(0, 1000000, 0, 100);
        private readonly Label _quotaInfo = Ui.Label("");
        private readonly ListView _policies = Ui.List("ID", "名称", "用户", "打印机", "时段", "强制双面", "强制黑白", "禁彩", "上限", "启用");
        private readonly ListView _report = Ui.List("维度值", "任务数", "页数", "彩色页", "失败", "字节", "费用");
        private readonly ListView _toner = Ui.List("打印机", "耗材", "余量(%)", "状态");
        private readonly ListView _held = Ui.List("任务 ID", "文件", "打印机", "用户", "入队时间", "状态");
        private readonly ComboBox _reportGroup = Ui.Combo(120);
        private readonly Timer _timer = new Timer { Interval = 5000 };

        public ManageTab()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Card;

            // 列宽
            _policies.Columns[0].Width = 50; _policies.Columns[1].Width = 140;
            _policies.Columns[2].Width = 100; _policies.Columns[3].Width = 120;
            _policies.Columns[4].Width = 80;
            for (int i = 5; i <= 9; i++) _policies.Columns[i].Width = 70;
            _report.Columns[0].Width = 180;
            for (int i = 1; i <= 6; i++) _report.Columns[i].Width = 90;
            _toner.Columns[0].Width = 200; _toner.Columns[1].Width = 180;
            _toner.Columns[2].Width = 90; _toner.Columns[3].Width = 80;
            _held.Columns[0].Width = 200; _held.Columns[1].Width = 180;
            _held.Columns[2].Width = 140; _held.Columns[3].Width = 100;
            _held.Columns[4].Width = 150; _held.Columns[5].Width = 80;

            _reportGroup.Items.AddRange(new object[] { "printer", "user", "hour", "color", "engine" });
            if (_reportGroup.Items.Count > 0) _reportGroup.SelectedIndex = 0;

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            var sections = new TabControl { Dock = DockStyle.Fill, Font = Theme.BodyFont };

            sections.TabPages.Add(BuildQuota());
            sections.TabPages.Add(BuildPolicy());
            sections.TabPages.Add(BuildReport());
            sections.TabPages.Add(BuildToner());
            sections.TabPages.Add(BuildRelease());
            sections.TabPages.Add(BuildNotify());

            root.Controls.Add(sections);
            Controls.Add(root);

            _timer.Tick += (s, e) => RefreshData();
            _timer.Start();
            RefreshData();
            Disposed += (s, e) => { _timer.Stop(); _timer.Dispose(); };
        }

        private TabPage BuildQuota()
        {
            var page = new TabPage(L10n.T("配额")) { Padding = new Padding(8), BackColor = Theme.Card };
            var bar = Ui.Toolbar(
                Ui.Label(L10n.T("用户") + ":"), _userId,
                Ui.Label(L10n.T("月限额") + ":"), _monthly,
                Ui.Label(L10n.T("彩色限额") + ":"), _color,
                Ui.Button(L10n.T("查询"), (s, e) => LoadQuota(), 70),
                Ui.Button(L10n.T("保存"), (s, e) => SaveQuota(), 70, primary: true),
                _quotaInfo);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildPolicy()
        {
            var page = new TabPage(L10n.T("策略")) { Padding = new Padding(8), BackColor = Theme.Card };
            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新"), (s, e) => LoadPolicies(), 70),
                Ui.Button(L10n.T("删除选中"), (s, e) => RemovePolicy(), 90));
            page.Controls.Add(_policies);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildReport()
        {
            var page = new TabPage(L10n.T("报表")) { Padding = new Padding(8), BackColor = Theme.Card };
            var bar = Ui.Toolbar(
                Ui.Label(L10n.T("分组") + ":"), _reportGroup,
                Ui.Button(L10n.T("刷新"), (s, e) => LoadReport(), 70));
            page.Controls.Add(_report);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildToner()
        {
            var page = new TabPage(L10n.T("墨量")) { Padding = new Padding(8), BackColor = Theme.Card };
            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新"), (s, e) => LoadToner(), 70));
            page.Controls.Add(_toner);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildRelease()
        {
            var page = new TabPage(L10n.T("安全打印")) { Padding = new Padding(8), BackColor = Theme.Card };
            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新"), (s, e) => LoadHeld(), 70),
                Ui.Button(L10n.T("释放选中"), (s, e) => ReleaseHeld(), 90, primary: true));
            page.Controls.Add(_held);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildNotify()
        {
            var page = new TabPage(L10n.T("通知")) { Padding = new Padding(8), BackColor = Theme.Card };
            var log = new ListBox { Dock = DockStyle.Fill, Font = Theme.BodyFont };
            var bar = Ui.Toolbar(
                Ui.Button(L10n.T("刷新"), (s, e) => LoadNotify(log), 70),
                Ui.Button(L10n.T("发送测试"), (s, e) =>
                {
                    Exec("TestNotification", "{\"message\":\"来自管理面板的测试通知\"}");
                    LoadNotify(log);
                }, 100, primary: true));
            page.Controls.Add(log);
            page.Controls.Add(bar);
            return page;
        }

        public void RefreshData()
        {
            Ui.Safe(this, () =>
            {
                LoadQuota(); LoadPolicies(); LoadReport(); LoadToner(); LoadHeld();
            });
        }

        private static JObject Exec(string cmd, string args = "{}")
        {
            try
            {
                var json = CommandDispatcher.Execute(cmd, args);
                return string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
            }
            catch { return new JObject(); }
        }

        private static JToken Data(JObject o) => o?["data"];

        private void LoadQuota()
        {
            try
            {
                var uid = _userId.Text.Trim();
                if (string.IsNullOrWhiteSpace(uid)) { _quotaInfo.Text = L10n.T("请输入用户 ID"); return; }
                var d = Data(Exec("GetQuota", "{\"userId\":\"" + Esc(uid) + "\"}"));
                if (d == null) return;
                _monthly.Value = Math.Min(_monthly.Maximum, d["monthlyLimit"]?.Value<int>() ?? 0);
                _color.Value = Math.Min(_color.Maximum, d["colorLimit"]?.Value<int>() ?? 0);
                _quotaInfo.Text = L10n.F("已用 {0} 页 / 彩色 {1} 页 / 花费 {2}",
                    d["usedPages"]?.Value<int>() ?? 0,
                    d["usedColorPages"]?.Value<int>() ?? 0,
                    d["spent"]?.Value<decimal>() ?? 0m);
            }
            catch (Exception ex) { _quotaInfo.Text = ex.Message; }
        }

        private void SaveQuota()
        {
            try
            {
                var uid = _userId.Text.Trim();
                if (string.IsNullOrWhiteSpace(uid)) { _quotaInfo.Text = L10n.T("请输入用户 ID"); return; }
                Exec("SetQuota", "{\"userId\":\"" + Esc(uid) + "\",\"monthlyLimit\":" +
                     _monthly.Value + ",\"colorLimit\":" + _color.Value + "}");
                LoadQuota();
            }
            catch (Exception ex) { _quotaInfo.Text = ex.Message; }
        }

        private void LoadPolicies()
        {
            try
            {
                var d = Data(Exec("ListPolicies"));
                var arr = d as JArray ?? (d?["policies"] as JArray) ?? new JArray();
                _policies.Items.Clear();
                foreach (var p in arr)
                {
                    var item = new ListViewItem(new[]
                    {
                        p["id"]?.ToString() ?? "", p["name"]?.ToString() ?? "",
                        p["userId"]?.ToString() ?? "", p["printer"]?.ToString() ?? "",
                        (p["startHour"]?.Value<int>() ?? 0) + "-" + (p["endHour"]?.Value<int>() ?? 24),
                        Yes(p["forceDuplex"]), Yes(p["forceBW"]), Yes(p["denyColor"]),
                        (p["maxCopies"]?.Value<int>() ?? 0).ToString(), Yes(p["enabled"])
                    });
                    _policies.Items.Add(item);
                }
            }
            catch { /* 管理数据不可用时保持空白 */ }
        }

        private void RemovePolicy()
        {
            if (_policies.SelectedItems.Count == 0) return;
            var id = _policies.SelectedItems[0].SubItems[0].Text;
            Exec("RemovePolicy", "{\"id\":" + id + "}");
            LoadPolicies();
        }

        private void LoadReport()
        {
            try
            {
                var group = _reportGroup.SelectedItem?.ToString() ?? "printer";
                var d = Data(Exec("GetPrintReport", "{\"groupBy\":\"" + Esc(group) + "\"}"));
                var arr = d as JArray ?? (d?["rows"] as JArray) ?? new JArray();
                _report.Items.Clear();
                foreach (var r in arr)
                    _report.Items.Add(new ListViewItem(new[]
                    {
                        r["key"]?.ToString() ?? "", (r["jobs"]?.Value<int>() ?? 0).ToString(),
                        (r["pages"]?.Value<int>() ?? 0).ToString(), (r["colorPages"]?.Value<int>() ?? 0).ToString(),
                        (r["failed"]?.Value<int>() ?? 0).ToString(), (r["bytes"]?.Value<long>() ?? 0).ToString(),
                        (r["cost"]?.Value<decimal>() ?? 0m).ToString("0.00")
                    }));
            }
            catch { }
        }

        private void LoadToner()
        {
            try
            {
                var d = Data(Exec("GetTonerLevels"));
                var arr = d as JArray ?? (d?["levels"] as JArray) ?? new JArray();
                _toner.Items.Clear();
                foreach (var t in arr)
                    _toner.Items.Add(new ListViewItem(new[]
                    {
                        t["host"]?.ToString() ?? "", t["cartridge"]?.ToString() ?? "",
                        (t["levelPercent"]?.Value<int>() ?? 0).ToString(),
                        (t["low"]?.Value<bool>() ?? false) ? L10n.T("偏低") : L10n.T("正常")
                    }));
            }
            catch { }
        }

        private void LoadHeld()
        {
            try
            {
                var d = Data(Exec("ListHeldJobs"));
                var arr = d as JArray ?? (d?["jobs"] as JArray) ?? new JArray();
                _held.Items.Clear();
                foreach (var h in arr)
                    _held.Items.Add(new ListViewItem(new[]
                    {
                        h["id"]?.ToString() ?? "", h["fileName"]?.ToString() ?? "",
                        h["printer"]?.ToString() ?? "", h["userId"]?.ToString() ?? "",
                        h["enqueuedAt"]?.ToString() ?? "", h["status"]?.ToString() ?? ""
                    }));
            }
            catch { }
        }

        private void ReleaseHeld()
        {
            if (_held.SelectedItems.Count == 0) return;
            var id = _held.SelectedItems[0].SubItems[0].Text;
            using var dlg = new Form
            {
                Text = L10n.T("安全打印释放"),
                Width = 340, Height = 160, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false
            };
            var pin = Ui.Input(200); pin.PasswordChar = '*';
            dlg.Controls.Add(pin);
            pin.Location = new Point(20, 20);
            var ok = Ui.Button(L10n.T("释放"), (s, e) =>
            {
                Exec("ReleaseJob", "{\"id\":\"" + Esc(id) + "\",\"pin\":\"" + Esc(pin.Text) + "\",\"releasedBy\":\"panel\"}");
                dlg.DialogResult = DialogResult.OK;
            }, 80, primary: true);
            ok.Location = new Point(20, 60);
            dlg.Controls.Add(ok);
            dlg.ShowDialog(this);
            LoadHeld();
        }

        private void LoadNotify(ListBox log)
        {
            try
            {
                var d = Data(Exec("ListNotifications", "{\"limit\":30}"));
                var arr = d as JArray ?? new JArray();
                log.Items.Clear();
                foreach (var n in arr)
                    log.Items.Add("[" + n["kind"] + "] " + n["title"] + " — " + n["message"] +
                                  (n["ok"]?.Value<bool>() ?? false ? "" : " (无渠道成功)"));
            }
            catch { }
        }

        private static string Yes(JToken t) => (t?.Value<bool>() ?? false) ? "✓" : "—";
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
