// Copyright (c) 2026 Alaikis Limited
// Lychee Printing Assistant (PipPrinter) is licensed under Mulan PSL v2.
// You can use this software according to the terms and conditions of the Mulan PSL v2.
// You may obtain a copy of Mulan PSL v2 at: http://license.coscl.org.cn/MulanPSL2
//
// AoRelay 浏览器测试页：连接 ws://<host>:__WS_PORT__/client，
// 展示在线设备、下发打印任务、查看设备回传结果。

namespace AoRelay
{
    internal static class TestPage
    {
        public const string Html = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>AO 测试台 · Lychee Printing Assistant</title>
<style>
  :root {
    --primary: #E8455A; --primary-dark: #C93347; --primary-soft: #FDECEF;
    --bg: #F5F6F7; --card: #FFFFFF; --border: #E4E7EB; --text: #1F2937; --muted: #6B7280;
    --success: #2E9E5B; --danger: #E0243A;
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--text);
         font: 14px/1.6 -apple-system, 'Segoe UI', 'Microsoft YaHei', sans-serif; }
  header { background: linear-gradient(135deg, var(--primary) 0%, #F0687C 100%);
           color: #fff; padding: 28px 32px; }
  header h1 { margin: 0; font-size: 20px; font-weight: 600; letter-spacing: .3px; }
  header p { margin: 6px 0 0; opacity: .92; font-size: 13px; }
  main { max-width: 960px; margin: -18px auto 40px; padding: 0 16px;
         display: grid; gap: 16px; grid-template-columns: 1fr 1fr; }
  .card { background: var(--card); border: 1px solid var(--border); border-radius: 10px;
          padding: 18px; box-shadow: 0 1px 2px rgba(16,24,40,.04); }
  .card h2 { margin: 0 0 12px; font-size: 15px; font-weight: 600; }
  .card.wide { grid-column: 1 / -1; }
  .conn { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--muted); }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--muted); }
  .dot.on { background: var(--success); box-shadow: 0 0 0 3px rgba(46,158,91,.15); }
  .dot.off { background: var(--danger); }
  table { width: 100%; border-collapse: collapse; font-size: 13px; }
  th { text-align: left; color: var(--muted); font-weight: 500; font-size: 12px;
       padding: 8px 10px; border-bottom: 1px solid var(--border); background: #F7F8FA; }
  td { padding: 10px; border-bottom: 1px solid #EEF0F2; }
  tr:last-child td { border-bottom: none; }
  .tag { display: inline-block; padding: 1px 8px; border-radius: 999px;
         background: var(--primary-soft); color: var(--primary-dark);
         font-size: 11px; font-weight: 600; }
  label { display: block; font-size: 12px; color: var(--muted); margin: 10px 0 4px; }
  input, select, textarea { width: 100%; padding: 8px 10px; border: 1px solid var(--border);
         border-radius: 6px; font: inherit; background: #fff; color: var(--text); }
  textarea { min-height: 90px; resize: vertical; font-family: Consolas, monospace; font-size: 12px; }
  .row { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
  button { border: 0; border-radius: 6px; padding: 9px 16px; font: inherit; font-weight: 600;
           cursor: pointer; background: var(--primary); color: #fff; }
  button:hover { background: var(--primary-dark); }
  button.ghost { background: #fff; color: var(--text); border: 1px solid var(--border); }
  button.ghost:hover { background: var(--primary-soft); }
  #log { font-family: Consolas, monospace; font-size: 12px; background: #0F172A; color: #E2E8F0;
         border-radius: 8px; padding: 12px; min-height: 160px; max-height: 320px; overflow: auto;
         white-space: pre-wrap; word-break: break-all; }
  .empty { color: var(--muted); font-size: 13px; padding: 18px 0; text-align: center; }
  .pill { font-size: 11px; color: var(--muted); }
</style>
</head>
<body>
<header>
  <h1>AO 测试台 · Lychee Printing Assistant</h1>
  <p>Active-Online Print 中继 · 设备发现与远程打印任务下发</p>
</header>
<main>
  <section class=""card wide"">
    <h2>连接</h2>
    <div class=""row"">
      <span class=""dot off"" id=""dot""></span>
      <span id=""connText"">未连接</span>
      <span class=""pill"">ws://<span id=""host""></span>:__WS_PORT__/client</span>
      <span style=""flex:1""></span>
      <button class=""ghost"" id=""btnConnect"">连接</button>
    </div>
  </section>

  <section class=""card"">
    <h2>在线设备 <span class=""pill"" id=""devCount"">0</span></h2>
    <table>
      <thead><tr><th>设备</th><th>打印机</th><th>状态</th></tr></thead>
      <tbody id=""devBody""><tr><td colspan=""3"" class=""empty"">暂无设备</td></tr></tbody>
    </table>
  </section>

  <section class=""card"">
    <h2>下发任务</h2>
    <label>目标设备</label>
    <select id=""selDevice""><option value="""">— 选择设备 —</option></select>
    <label>命令（cmd）</label>
    <input id=""cmd"" value=""PrintBarcode"" placeholder=""PrintBarcode / PrintPdf / GetQueue …"">
    <label>参数（payload JSON）</label>
    <textarea id=""payload"">{""value"":""https://example.com"",""format"":""qr"",""width"":300,""height"":300}</textarea>
    <div class=""row"" style=""margin-top:12px"">
      <button id=""btnSend"">下发</button>
      <span class=""pill"">结果将显示在下方日志</span>
    </div>
  </section>

  <section class=""card wide"">
    <h2>日志</h2>
    <div id=""log""></div>
  </section>
</main>

<script>
const PORT = __WS_PORT__;
const $ = id => document.getElementById(id);
$('host').textContent = location.hostname;
let ws = null, timer = null, devices = [];

function log(text, kind) {
  const el = $('log');
  const time = new Date().toLocaleTimeString();
  const line = document.createElement('div');
  line.textContent = '[' + time + '] ' + text;
  if (kind === 'err') line.style.color = '#FCA5A5';
  if (kind === 'ok') line.style.color = '#86EFAC';
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
}

function setConn(on) {
  $('dot').className = 'dot ' + (on ? 'on' : 'off');
  $('connText').textContent = on ? '已连接' : '未连接';
  $('btnConnect').textContent = on ? '断开' : '连接';
}

function renderDevices(list) {
  devices = list || [];
  $('devCount').textContent = devices.length;
  const body = $('devBody');
  if (!devices.length) {
    body.innerHTML = '<tr><td colspan=""3"" class=""empty"">暂无设备</td></tr>';
    $('selDevice').innerHTML = '<option value="""">— 选择设备 —</option>';
    return;
  }
  body.innerHTML = devices.map(d => {
    const seen = Math.round((Date.now() / 1000 - d.lastSeen) / 60);
    const printers = (d.printers || []).join('、') || '—';
    return '<tr><td><b>' + esc(d.name) + '</b><br><span class=""pill"">' + esc(d.id) + '</span></td>'
         + '<td>' + esc(printers) + '</td>'
         + '<td><span class=""tag"">在线</span> <span class=""pill"">' + seen + ' 分钟前活跃</span></td></tr>';
  }).join('');
  $('selDevice').innerHTML = '<option value="""">— 选择设备 —</option>' +
    devices.map(d => '<option value=""' + esc(d.id) + '"">' + esc(d.name) + '</option>').join('');
}

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>""]/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '""': '&quot;' }[c]));
}

function connect() {
  if (ws) { ws.close(); return; }
  const url = 'ws://' + location.hostname + ':' + PORT + '/client';
  log('连接 ' + url);
  ws = new WebSocket(url);
  ws.onopen = () => { setConn(true); log('已连接中继', 'ok'); };
  ws.onclose = () => { setConn(false); ws = null; log('连接已断开'); };
  ws.onerror = () => log('连接错误', 'err');
  ws.onmessage = ev => {
    let msg;
    try { msg = JSON.parse(ev.data); } catch { log('收到: ' + ev.data); return; }
    log('← ' + JSON.stringify(msg, null, 0));
    if (msg.type === 'devices') renderDevices(msg.devices);
    if (msg.type === 'accepted') log('任务已受理: ' + msg.jobId, 'ok');
    if (msg.type === 'result') log('设备回传结果: ' + msg.response, 'ok');
    if (msg.type === 'error') log('错误: ' + msg.error, 'err');
  };
  timer = setInterval(() => { if (ws && ws.readyState === 1) ws.send('{""type"":""ping""}'); }, 30000);
}

$('btnConnect').onclick = connect;
$('btnSend').onclick = () => {
  const deviceId = $('selDevice').value;
  const cmd = $('cmd').value.trim();
  let payload;
  try { payload = JSON.parse($('payload').value); }
  catch (e) { log('payload 不是合法 JSON: ' + e.message, 'err'); return; }
  if (!deviceId) { log('请先选择目标设备', 'err'); return; }
  if (!cmd) { log('cmd 不能为空', 'err'); return; }
  if (!ws || ws.readyState !== 1) { log('尚未连接中继', 'err'); return; }
  const jobId = 'j' + Date.now();
  const msg = { type: 'job', deviceId: deviceId, jobId: jobId, cmd: cmd, payload: payload };
  ws.send(JSON.stringify(msg));
  log('→ 下发 ' + cmd + ' (' + jobId + ')');
};
</script>
</body>
</html>";
    }
}
