/* 应用管家 - 前端逻辑（无框架、无外部依赖） */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

const el = {
  serverPill: $("#serverPill"),
  serverText: $("#serverText"),
  autostartDot: $("#autostartDot"),
  appList: $("#appList"),
  appCount: $("#appCount"),
  emptyState: $("#emptyState"),
  appView: $("#appView"),
  appName: $("#appName"),
  appStatusChip: $("#appStatusChip"),
  appUptime: $("#appUptime"),
  appMeta: $("#appMeta"),
  appError: $("#appError"),
  logPane: $("#logPane"),
  logInner: $("#logInner"),
  logEmpty: $("#logEmpty"),
  logCount: $("#logCount"),
  logFilter: $("#logFilter"),
  autoScroll: $("#autoScroll"),
  wrapLog: $("#wrapLog"),
  modalRoot: $("#modalRoot"),
  toasts: $("#toasts"),
  versionText: $("#versionText"),
};

const STATUS_TEXT = {
  running: "运行中",
  starting: "启动中",
  stopping: "停止中",
  stopped: "已停止",
  crashed: "已崩溃",
};

const state = {
  apps: [],
  server: null,
  selectedId: localStorage.getItem("am.selected") || null,
  logs: [],
  lastSeq: 0,
  filter: "",
  autoScroll: true,
  token: localStorage.getItem("am.token") || "",
  stateBusy: false,
  logBusy: false,
  connected: false,
  lastMetaJson: "",
  lastListJson: "",
  modalCleanup: null,
  systemTimer: null,
};

/* ------------------------------------------------------------------ */
/* 基础工具                                                           */
/* ------------------------------------------------------------------ */

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[c]));
}

function currentApp() {
  return state.apps.find((a) => a.id === state.selectedId) || null;
}

function toast(message, type = "ok", ms = 3200) {
  const node = document.createElement("div");
  node.className = `toast ${type}`;
  node.textContent = message;
  el.toasts.appendChild(node);
  setTimeout(() => {
    node.style.opacity = "0";
    node.style.transform = "translateY(6px)";
    node.style.transition = "all .2s";
    setTimeout(() => node.remove(), 220);
  }, ms);
}

function fmtTime(ms) {
  const d = new Date(ms);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
}

function fmtDuration(seconds) {
  seconds = Math.max(0, Math.floor(seconds || 0));
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  if (d) return `${d} 天 ${h} 小时`;
  if (h) return `${h} 小时 ${m} 分`;
  if (m) return `${m} 分 ${s} 秒`;
  return `${s} 秒`;
}

function parseEnv(text) {
  const env = {};
  String(text || "").split(/\r?\n/).forEach((line) => {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) return;
    const idx = trimmed.indexOf("=");
    if (idx <= 0) throw new Error(`环境变量格式应为 KEY=VALUE：${trimmed}`);
    const key = trimmed.slice(0, idx).trim();
    const value = trimmed.slice(idx + 1).trim();
    if (!/^[A-Za-z_][A-Za-z0-9_.-]*$/.test(key)) throw new Error(`环境变量名不合法：${key}`);
    env[key] = value;
  });
  return env;
}

function envToText(env) {
  return Object.entries(env || {}).map(([k, v]) => `${k}=${v}`).join("\n");
}

/* ------------------------------------------------------------------ */
/* API                                                                */
/* ------------------------------------------------------------------ */

async function api(path, { method = "GET", body = undefined, retried = false } = {}) {
  const headers = {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (state.token) headers["X-Auth-Token"] = state.token;

  let res;
  try {
    res = await fetch(path, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      cache: "no-store",
    });
  } catch (err) {
    throw new Error("无法连接管理服务，请确认它仍在运行");
  }

  if (res.status === 401) {
    if (!retried && askToken()) return api(path, { method, body, retried: true });
    throw new Error("访问令牌不正确");
  }

  let data = {};
  try {
    data = await res.json();
  } catch (err) {
    if (!res.ok) throw new Error(`请求失败（${res.status}）`);
  }
  if (!res.ok) throw new Error(data.error || `请求失败（${res.status}）`);
  return data;
}

function askToken() {
  const value = window.prompt("此管理面板已启用访问令牌，请输入令牌：", state.token || "");
  if (value === null) return false;
  state.token = value.trim();
  localStorage.setItem("am.token", state.token);
  return true;
}

/* ------------------------------------------------------------------ */
/* 状态轮询                                                           */
/* ------------------------------------------------------------------ */

async function refreshState() {
  if (state.stateBusy) return;
  state.stateBusy = true;
  try {
    const data = await api("/api/state");
    state.connected = true;
    state.server = data.server;
    state.apps = data.apps || [];
    updateServerPill();

    if (!state.selectedId || !state.apps.some((a) => a.id === state.selectedId)) {
      const next = state.apps[0] ? state.apps[0].id : null;
      if (next !== state.selectedId) {
        selectApp(next, { silent: true });
      }
    }

    renderAppList();
    renderDetail();
  } catch (err) {
    state.connected = false;
    updateServerPill(err.message);
  } finally {
    state.stateBusy = false;
  }
}

function updateServerPill(error) {
  const s = state.server;
  if (state.connected && s) {
    el.serverPill.classList.remove("offline");
    el.serverPill.querySelector(".dot").className = "dot running";
    el.serverText.textContent = `${s.host}:${s.port}`;
    el.serverPill.title = `管理服务运行中\nPython: ${s.python}\n配置: ${s.config_path}`;
    el.versionText.textContent = `v${s.version}`;
    const inst = s.autostart && s.autostart.installed;
    el.autostartDot.className = `dot ${inst ? "installed" : "not-installed"}`;
    $("#btnAutostart").title = inst ? "开机自启已开启" : "开机自启未开启（点击设置）";
  } else {
    el.serverPill.classList.add("offline");
    el.serverPill.querySelector(".dot").className = "dot crashed";
    el.serverText.textContent = error ? "连接断开" : "连接中…";
    el.serverPill.title = error || "";
  }
}

/* ------------------------------------------------------------------ */
/* 列表与详情                                                         */
/* ------------------------------------------------------------------ */

function renderAppList() {
  const signature = JSON.stringify(state.apps.map((a) => [
    a.id, a.name, a.status, a.running, a.pid, a.command,
  ]));
  el.appCount.textContent = state.apps.length;
  if (signature === state.lastListJson) return;
  state.lastListJson = signature;

  const frag = document.createDocumentFragment();
  state.apps.forEach((app) => {
    const btn = document.createElement("button");
    btn.className = `app-item ${app.id === state.selectedId ? "active" : ""}`;
    btn.dataset.id = app.id;

    const dot = document.createElement("span");
    dot.className = `dot ${app.status}`;

    const body = document.createElement("span");
    body.className = "item-body";
    const name = document.createElement("span");
    name.className = "item-name";
    name.textContent = app.name;
    const sub = document.createElement("span");
    sub.className = "item-sub";
    sub.textContent = app.command.split(/\r?\n/)[0];
    body.append(name, sub);
    btn.append(dot, body);

    if (app.pid) {
      const pid = document.createElement("span");
      pid.className = "item-pid";
      pid.textContent = `#${app.pid}`;
      btn.append(pid);
    }

    btn.addEventListener("click", () => selectApp(app.id));
    frag.appendChild(btn);
  });
  el.appList.replaceChildren(frag);
}

function selectApp(id, { silent = false } = {}) {
  state.selectedId = id;
  if (id) localStorage.setItem("am.selected", id);
  else localStorage.removeItem("am.selected");
  state.logs = [];
  state.lastSeq = 0;
  state.lastMetaJson = "";
  el.logInner.replaceChildren();
  el.logFilter.value = "";
  state.filter = "";
  state.lastListJson = "";
  renderAppList();
  renderDetail();
  if (id && !silent) refreshLogs(true);
  if (id && silent) refreshLogs(true);
}

function metaCell(label, value, { wide = false, muted = false } = {}) {
  return `<div class="meta-cell ${wide ? "wide" : ""}">
    <div class="meta-label">${esc(label)}</div>
    <div class="meta-value ${muted ? "muted" : ""}" title="${esc(value)}">${esc(value)}</div>
  </div>`;
}

function renderDetail() {
  const app = currentApp();
  if (!app) {
    el.emptyState.hidden = false;
    el.appView.hidden = true;
    document.title = "应用管家";
    return;
  }
  el.emptyState.hidden = true;
  el.appView.hidden = false;

  el.appName.textContent = app.name;
  document.title = `${app.name} · 应用管家`;

  const chip = el.appStatusChip;
  chip.className = `chip ${app.status}`;
  chip.textContent = app.status === "crashed" && app.restart_pending
    ? "等待自动重启"
    : (STATUS_TEXT[app.status] || app.status);

  if (app.running && app.started_at) {
    el.appUptime.textContent = `已运行 ${fmtDuration((Date.now() - app.started_at) / 1000)}`;
  } else {
    el.appUptime.textContent = "";
  }

  const showError = app.last_error && app.status !== "running";
  el.appError.hidden = !showError;
  el.appError.textContent = showError ? app.last_error : "";

  const metaJson = JSON.stringify(app);
  if (metaJson !== state.lastMetaJson) {
    state.lastMetaJson = metaJson;
    const exitCode = app.exit_code === null || app.exit_code === undefined
      ? "—"
      : `${app.exit_code}${app.exit_code !== 0 ? "（异常）" : ""}`;
    el.appMeta.innerHTML = [
      metaCell("启动命令", app.command, { wide: true }),
      metaCell("工作目录", app.cwd, { wide: true, muted: true }),
      metaCell("启动方式", app.shell === "cmd" ? "命令提示符 (cmd)" : "PowerShell"),
      metaCell("PID", app.pid ? String(app.pid) : "—", { muted: !app.pid }),
      metaCell("退出码", exitCode, { muted: app.exit_code === null }),
      metaCell("自动重启", app.restart_on_crash ? `已开启（最多 ${app.max_restarts} 次）` : "已关闭"),
      metaCell("开机启动", app.auto_start ? "开机后自动启动" : "仅手动启动"),
      metaCell("重启次数", String(app.restarts || 0), { muted: !app.restarts }),
    ].join("");
  }

  const starting = app.status === "starting" || app.status === "stopping";
  $("#btnStart").disabled = app.running || starting;
  $("#btnStop").disabled = !app.running;
  $("#btnRestart").disabled = !app.running || starting;
}

/* ------------------------------------------------------------------ */
/* 日志                                                               */
/* ------------------------------------------------------------------ */

async function refreshLogs(reset = false) {
  const app = currentApp();
  if (!app || state.logBusy) return;
  state.logBusy = true;
  const appId = app.id;
  try {
    const after = reset ? 0 : state.lastSeq;
    const data = await api(`/api/apps/${encodeURIComponent(appId)}/logs?after=${after}&limit=1500`);
    if (state.selectedId !== appId) return;

    if (reset || data.next < state.lastSeq) {
      state.logs = data.lines || [];
      state.lastSeq = data.next || 0;
      renderLogsFull();
    } else {
      appendLogLines(data.lines || []);
      state.lastSeq = data.next || state.lastSeq;
    }
  } catch (err) {
    /* 轮询失败不打扰用户，连接状态由状态轮询体现 */
  } finally {
    state.logBusy = false;
  }
}

function appendLogLines(lines) {
  if (!lines.length) return;
  const filtered = Boolean(state.filter);
  const frag = document.createDocumentFragment();
  lines.forEach((line) => {
    state.logs.push(line);
    if (!filtered || line.text.toLowerCase().includes(state.filter)) {
      frag.appendChild(makeLogLine(line));
    }
  });
  if (state.logs.length > 4000) state.logs.splice(0, state.logs.length - 4000);

  el.logInner.appendChild(frag);
  while (el.logInner.childElementCount > 4000) {
    el.logInner.firstElementChild.remove();
  }
  updateLogEmpty();
  updateLogCount();
  maybeScroll();
}

function makeLogLine(line) {
  const node = document.createElement("div");
  node.className = "log-line";
  const t = document.createElement("span");
  t.className = "lt";
  t.textContent = fmtTime(line.ts);
  const m = document.createElement("span");
  m.className = "lm";
  m.textContent = line.text;
  node.append(t, m);
  return node;
}

function renderLogsFull() {
  const frag = document.createDocumentFragment();
  state.logs.forEach((line) => {
    if (state.filter && !line.text.toLowerCase().includes(state.filter)) return;
    frag.appendChild(makeLogLine(line));
  });
  el.logInner.replaceChildren(frag);
  updateLogEmpty();
  updateLogCount();
  el.logPane.scrollTop = el.logPane.scrollHeight;
}

function updateLogEmpty() {
  el.logEmpty.hidden = el.logInner.childElementCount > 0;
}

function updateLogCount() {
  const shown = el.logInner.childElementCount;
  const total = state.logs.length;
  el.logCount.textContent = state.filter ? `显示 ${shown} / ${total} 行` : total ? `${total} 行` : "";
}

function maybeScroll() {
  if (state.autoScroll) {
    el.logPane.scrollTop = el.logPane.scrollHeight;
  }
}

/* ------------------------------------------------------------------ */
/* 操作                                                               */
/* ------------------------------------------------------------------ */

async function runAction(action) {
  const app = currentApp();
  if (!app) return;
  if (action === "delete") return deleteApp(app);
  if (action === "edit") return openAppModal(app);

  const buttons = [$("#btnStart"), $("#btnStop"), $("#btnRestart")];
  buttons.forEach((b) => { b.disabled = true; });
  try {
    const data = await api(`/api/apps/${encodeURIComponent(app.id)}/${action}`, { method: "POST" });
    if (data.ok) {
      toast(data.message || "操作成功");
    } else {
      toast(data.message || "操作失败", "err");
    }
    await refreshState();
    if (action === "clear-logs") {
      state.logs = [];
      state.lastSeq = data.log_seq || 0;
      el.logInner.replaceChildren();
      updateLogEmpty();
      updateLogCount();
    } else {
      await refreshLogs(true);
    }
  } catch (err) {
    toast(err.message, "err");
    await refreshState();
  }
}

async function startAll() {
  try {
    const data = await api("/api/all/start", { method: "POST" });
    const ok = (data.results || []).filter((r) => r.ok).length;
    toast(`已启动 ${ok} 个应用`);
    await refreshState();
    await refreshLogs(true);
  } catch (err) {
    toast(err.message, "err");
  }
}

async function stopAll() {
  const yes = await confirmDialog({
    title: "停止所有应用",
    message: "将停止当前托管的全部应用，确定继续吗？",
    confirmText: "全部停止",
    danger: true,
  });
  if (!yes) return;
  try {
    await api("/api/all/stop", { method: "POST" });
    toast("已停止所有应用");
    await refreshState();
    await refreshLogs(true);
  } catch (err) {
    toast(err.message, "err");
  }
}

/* ------------------------------------------------------------------ */
/* 弹窗                                                               */
/* ------------------------------------------------------------------ */

function openModal(html, { className = "", onClose = null } = {}) {
  closeModal();
  const mask = document.createElement("div");
  mask.className = "modal-mask";
  mask.innerHTML = `<div class="modal ${className}" role="dialog" aria-modal="true">${html}</div>`;
  el.modalRoot.innerHTML = "";
  el.modalRoot.appendChild(mask);

  const modal = $(".modal", mask);
  state.modalCleanup = () => {
    if (state.systemTimer) {
      clearInterval(state.systemTimer);
      state.systemTimer = null;
    }
    if (onClose) onClose();
  };

  mask.addEventListener("mousedown", (e) => {
    if (e.target === mask) closeModal();
  });
  $$("[data-close]", modal).forEach((btn) => btn.addEventListener("click", closeModal));
  setTimeout(() => {
    const input = $("input:not([type=checkbox]):not([type=radio]), textarea", modal);
    if (input && !modal.dataset.noFocus) input.focus();
  }, 30);
  return modal;
}

function closeModal() {
  const cleanup = state.modalCleanup;
  state.modalCleanup = null;
  el.modalRoot.innerHTML = "";
  if (cleanup) cleanup();
}

function confirmDialog({ title, message, confirmText = "确定", danger = false }) {
  return new Promise((resolve) => {
    const modal = openModal(`
      <div class="modal-head"><h3>${esc(title)}</h3><button class="modal-close" data-close>×</button></div>
      <div class="modal-body"><p style="margin:0;color:var(--muted);font-size:13.5px;line-height:1.7">${esc(message)}</p></div>
      <div class="modal-foot">
        <button class="btn ghost" data-close>取消</button>
        <button class="btn ${danger ? "danger" : "primary"}" id="confirmOk">${esc(confirmText)}</button>
      </div>`, { className: "small" });
    let settled = false;
    const originalCleanup = state.modalCleanup;
    state.modalCleanup = () => { if (originalCleanup) originalCleanup(); if (!settled) { settled = true; resolve(false); } };
    $("#confirmOk", modal).addEventListener("click", () => {
      settled = true;
      closeModal();
      resolve(true);
    });
  });
}

/* ------------------------------------------------------------------ */
/* 添加 / 编辑应用                                                     */
/* ------------------------------------------------------------------ */

function openAppModal(app = null) {
  const isEdit = Boolean(app);
  const shell = app ? app.shell : "powershell";
  const modal = openModal(`
    <div class="modal-head">
      <h3>${isEdit ? "编辑应用" : "添加应用"}</h3>
      <button class="modal-close" data-close>×</button>
    </div>
    <div class="modal-body">
      <form class="form" id="appForm">
        <label class="field">
          <span>应用名称</span>
          <input type="text" name="name" maxlength="60" required placeholder="例如：订单 API" value="${esc(app?.name || "")}">
        </label>
        <label class="field">
          <span>启动命令 <span class="hint">在前台运行，日志会实时显示在这里</span></span>
          <textarea class="code" name="command" rows="3" required spellcheck="false"
            placeholder="python app.py&#10;或  npm run start&#10;或  node server.js">${esc(app?.command || "")}</textarea>
        </label>
        <label class="field">
          <span>工作目录 <span class="hint">留空则使用管家所在目录</span></span>
          <input class="code" type="text" name="cwd" spellcheck="false"
            placeholder="C:\\projects\\my-app" value="${esc(app?.cwd || "")}">
        </label>
        <div class="row2">
          <label class="field">
            <span>启动方式</span>
            <select name="shell">
              <option value="powershell" ${shell === "powershell" ? "selected" : ""}>PowerShell（推荐）</option>
              <option value="cmd" ${shell === "cmd" ? "selected" : ""}>命令提示符 (cmd)</option>
            </select>
          </label>
          <label class="field">
            <span>启动延迟（秒）</span>
            <input type="number" name="start_delay" min="0" max="600" value="${app?.start_delay ?? 0}">
          </label>
        </div>
        <label class="field">
          <span>环境变量 <span class="hint">每行一个 KEY=VALUE</span></span>
          <textarea class="code" name="env" rows="3" spellcheck="false"
            placeholder="PORT=8000&#10;NODE_ENV=production">${esc(envToText(app?.env))}</textarea>
        </label>
        <div class="checks">
          <label class="check"><input type="checkbox" name="auto_start" ${app?.auto_start ?? true ? "checked" : ""}> 开机后自动启动</label>
          <label class="check"><input type="checkbox" name="restart_on_crash" ${app?.restart_on_crash ?? true ? "checked" : ""}> 异常退出后自动重启</label>
        </div>
        <div class="form-error" id="formError" hidden></div>
        <div class="modal-foot" style="padding:4px 0 0;border:0">
          <button type="button" class="btn ghost" data-close>取消</button>
          <button type="submit" class="btn primary">${isEdit ? "保存修改" : "添加应用"}</button>
        </div>
      </form>
    </div>`);

  const form = $("#appForm", modal);
  const errorBox = $("#formError", modal);

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    errorBox.hidden = true;
    const fd = new FormData(form);
    let env;
    try {
      env = parseEnv(fd.get("env"));
    } catch (err) {
      errorBox.textContent = err.message;
      errorBox.hidden = false;
      return;
    }
    const payload = {
      name: String(fd.get("name") || "").trim(),
      command: String(fd.get("command") || "").trim(),
      cwd: String(fd.get("cwd") || "").trim(),
      shell: fd.get("shell"),
      env,
      auto_start: fd.get("auto_start") === "on",
      restart_on_crash: fd.get("restart_on_crash") === "on",
      start_delay: Number(fd.get("start_delay") || 0),
    };
    if (!payload.name || !payload.command) {
      errorBox.textContent = "应用名称和启动命令不能为空";
      errorBox.hidden = false;
      return;
    }
    const submit = $("button[type=submit]", form);
    submit.disabled = true;
    try {
      const result = isEdit
        ? await api(`/api/apps/${encodeURIComponent(app.id)}`, { method: "PUT", body: { app: payload } })
        : await api("/api/apps", { method: "POST", body: { app: payload } });
      closeModal();
      toast(isEdit ? "已保存修改" : "应用已添加");
      if (!isEdit && result.app) state.selectedId = result.app.id;
      state.lastListJson = "";
      await refreshState();
      if (!isEdit && result.app) selectApp(result.app.id);
    } catch (err) {
      errorBox.textContent = err.message;
      errorBox.hidden = false;
    } finally {
      submit.disabled = false;
    }
  });
}

async function deleteApp(app) {
  const yes = await confirmDialog({
    title: "删除应用",
    message: `确定删除「${app.name}」吗？应用会先被停止，对应的配置会被移除（日志文件保留在 logs 目录）。`,
    confirmText: "删除",
    danger: true,
  });
  if (!yes) return;
  try {
    await api(`/api/apps/${encodeURIComponent(app.id)}`, { method: "DELETE" });
    toast("应用已删除");
    state.selectedId = null;
    state.lastListJson = "";
    await refreshState();
  } catch (err) {
    toast(err.message, "err");
  }
}

/* ------------------------------------------------------------------ */
/* 设置                                                               */
/* ------------------------------------------------------------------ */

async function openSettings() {
  let cfg;
  try {
    cfg = await api("/api/config");
  } catch (err) {
    toast(err.message, "err");
    return;
  }
  const s = state.server || {};
  const modal = openModal(`
    <div class="modal-head"><h3>设置</h3><button class="modal-close" data-close>×</button></div>
    <div class="modal-body">
      <div class="tabs">
        <button class="tab active" data-tab="general">常规</button>
        <button class="tab" data-tab="advanced">高级（JSON）</button>
        <button class="tab" data-tab="about">关于</button>
      </div>

      <form class="form" id="settingsForm">
        <div data-pane="general">
          <div class="row2">
            <label class="field">
              <span>监听地址</span>
              <select name="host">
                <option value="127.0.0.1" ${cfg.host === "127.0.0.1" ? "selected" : ""}>仅本机访问（推荐）</option>
                <option value="0.0.0.0" ${cfg.host === "0.0.0.0" ? "selected" : ""}>局域网可访问</option>
              </select>
            </label>
            <label class="field">
              <span>端口</span>
              <input type="number" name="port" min="1" max="65535" value="${cfg.port}">
            </label>
          </div>
          <label class="field" style="margin-top:14px">
            <span>访问令牌 <span class="hint">留空则不校验；对外开放时建议设置</span></span>
            <input type="text" name="auth_token" value="${esc(cfg.auth_token)}" autocomplete="off" placeholder="例如：一串只有你知道的字符">
          </label>
          <label class="field" style="margin-top:14px">
            <span>开机启动等待（秒） <span class="hint">开机后先等一会儿再拉起应用，避免网络/磁盘尚未就绪</span></span>
            <input type="number" name="startup_delay" min="0" max="3600" value="${cfg.startup_delay}">
          </label>
          <label class="field" style="margin-top:14px">
            <span>额外 PATH <span class="hint">当找不到 node / npm 时，把它们的目录填在这里</span></span>
            <input class="code" type="text" name="extra_path" value="${esc(cfg.extra_path)}" placeholder="C:\\Program Files\\nodejs">
          </label>
          <label class="field" style="margin-top:14px">
            <span>全局环境变量 <span class="hint">每行一个 KEY=VALUE，作用于所有应用</span></span>
            <textarea class="code" name="env" rows="3" spellcheck="false">${esc(envToText(cfg.env))}</textarea>
          </label>
          <div class="modal-note" style="margin-top:14px">
            修改监听地址、端口或访问令牌后，需要重启管理服务才会生效。
          </div>
        </div>

        <div data-pane="advanced" hidden>
          <div class="modal-note warn" style="margin-bottom:12px">
            这里可以直接编辑完整配置（config.json）。保存时会整体替换，请确保 JSON 格式正确。
          </div>
          <textarea class="code" id="rawConfig" rows="18" spellcheck="false"
            style="font-family:var(--mono);font-size:12px;line-height:1.6;width:100%;background:#0e141b;border:1px solid var(--line);border-radius:9px;padding:10px">${esc(JSON.stringify(cfg, null, 2))}</textarea>
        </div>

        <div data-pane="about" hidden>
          <div class="status-line"><span class="dot installed"></span><b>应用管家 v${esc(s.version || "")}</b></div>
          <div class="muted" style="font-size:12.5px;line-height:1.9;margin-top:8px">
            配置文件：<span class="mono">${esc(s.config_path || "")}</span><br>
            Python：<span class="mono">${esc(s.python || "")}</span><br>
            日志目录：<span class="mono">${esc((s.root || "") + "\\logs")}</span>
          </div>
          <div class="modal-note" style="margin-top:16px">
            后台常驻：安装“开机自启”后，即使不登录 Windows，管理服务也会在开机后自动运行。<br>
            停止管理服务会同时停止所有受管应用。
          </div>
          <div style="margin-top:16px;display:flex;gap:8px;flex-wrap:wrap">
            <button type="button" class="btn ghost" id="btnRestartManager">重启管理服务</button>
            <button type="button" class="btn danger" id="btnShutdownManager">停止管理服务</button>
          </div>
        </div>
      </form>
    </div>
    <div class="modal-foot">
      <button class="btn ghost" data-close>取消</button>
      <button class="btn primary" id="btnSaveSettings">保存设置</button>
    </div>`);

  let activeTab = "general";
  $$(".tab", modal).forEach((tab) => {
    tab.addEventListener("click", () => {
      activeTab = tab.dataset.tab;
      $$(".tab", modal).forEach((t) => t.classList.toggle("active", t === tab));
      $$("[data-pane]", modal).forEach((p) => { p.hidden = p.dataset.pane !== activeTab; });
      $("#btnSaveSettings", modal).hidden = activeTab === "about";
    });
  });

  $("#btnSaveSettings", modal).addEventListener("click", async () => {
    let payload;
    try {
      if (activeTab === "advanced") {
        payload = JSON.parse($("#rawConfig", modal).value);
      } else {
        const form = $("#settingsForm", modal);
        const fd = new FormData(form);
        payload = {
          ...cfg,
          host: fd.get("host"),
          port: Number(fd.get("port")),
          auth_token: String(fd.get("auth_token") || "").trim(),
          startup_delay: Number(fd.get("startup_delay") || 0),
          extra_path: String(fd.get("extra_path") || "").trim(),
          env: parseEnv(fd.get("env")),
        };
      }
    } catch (err) {
      toast(err.message, "err", 5000);
      return;
    }
    try {
      const result = await api("/api/config", { method: "PUT", body: payload });
      closeModal();
      toast("设置已保存");
      if (typeof payload.auth_token === "string") {
        state.token = payload.auth_token.trim();
        localStorage.setItem("am.token", state.token);
      }
      await refreshState();
      if (result.needs_restart) {
        const yes = await confirmDialog({
          title: "需要重启管理服务",
          message: "监听地址 / 端口 / 访问令牌已修改，需要重启管理服务才能生效。重启期间所有应用会短暂停止，确定现在重启吗？",
          confirmText: "立即重启",
        });
        if (yes) await restartManager({ port: Number(payload.port) });
      }
    } catch (err) {
      toast(err.message, "err", 5000);
    }
  });

  $("#btnRestartManager", modal).addEventListener("click", async () => {
    const yes = await confirmDialog({
      title: "重启管理服务",
      message: "重启期间所有应用会先停止，然后由新的管理服务重新拉起。确定继续吗？",
      confirmText: "重启",
    });
    if (yes) await restartManager();
  });

  $("#btnShutdownManager", modal).addEventListener("click", async () => {
    const yes = await confirmDialog({
      title: "停止管理服务",
      message: "管理服务会退出，所有受管应用也会停止。如果已安装开机自启，下次开机仍会自动恢复。",
      confirmText: "停止",
      danger: true,
    });
    if (!yes) return;
    try {
      await api("/api/manager/shutdown", { method: "POST" });
      showOverlay("管理服务已停止", "可以关闭此页面。若需重新启动，请运行 start-manager.cmd。");
    } catch (err) {
      toast(err.message, "err");
    }
  });
}

function showOverlay(title, text) {
  closeModal();
  const mask = document.createElement("div");
  mask.className = "modal-mask";
  mask.innerHTML = `<div class="modal small"><div class="modal-body" style="text-align:center;padding:34px 24px">
    <h3 style="margin:0 0 10px;font-size:17px">${esc(title)}</h3>
    <p style="margin:0;color:var(--muted);font-size:13px;line-height:1.8">${esc(text)}</p>
  </div></div>`;
  el.modalRoot.innerHTML = "";
  el.modalRoot.appendChild(mask);
}

async function restartManager({ port } = {}) {
  const targetPort = Number(port || (state.server && state.server.port) || location.port);
  const currentPort = Number((state.server && state.server.port) || 0);
  const samePort = !currentPort || targetPort === currentPort;
  const base = samePort ? "" : `${location.protocol}//${location.hostname}:${targetPort}`;
  try {
    await api("/api/manager/restart", { method: "POST" });
  } catch (err) {
    toast(err.message, "err");
    return;
  }
  showOverlay("正在重启管理服务…", "应用会按顺序重新启动，页面将在服务恢复后自动刷新。");

  if (!samePort) {
    // 端口变了：跨端口 fetch 会被浏览器拦截，用图片探测端口是否恢复
    let tries = 0;
    const timer = setInterval(() => {
      tries += 1;
      const img = new Image();
      img.onload = () => {
        clearInterval(timer);
        location.href = `${base}/`;
      };
      img.src = `${base}/favicon.svg?t=${Date.now()}`;
      if (tries > 45) {
        clearInterval(timer);
        showOverlay("服务暂时没有响应", `如果端口已修改，请手动打开 ${base}/ 检查。`);
      }
    }, 800);
    return;
  }

  let tries = 0;
  const timer = setInterval(async () => {
    tries += 1;
    try {
      const res = await fetch(`${base}/api/health`, {
        headers: state.token ? { "X-Auth-Token": state.token } : {},
        cache: "no-store",
      });
      if (res.ok) {
        clearInterval(timer);
        if (samePort) location.reload();
        else location.href = `${base}/`;
      }
    } catch (err) {
      /* 等待服务回来 */
    }
    if (tries > 45) {
      clearInterval(timer);
      showOverlay("服务暂时没有响应", "请检查 start-manager.cmd 或计划任务是否正常运行。");
    }
  }, 800);
}

/* ------------------------------------------------------------------ */
/* 开机自启                                                           */
/* ------------------------------------------------------------------ */

async function openAutostart() {
  let info;
  try {
    info = await api("/api/autostart");
  } catch (err) {
    toast(err.message, "err");
    return;
  }
  const installed = Boolean(info.installed);
  const modal = openModal(`
    <div class="modal-head"><h3>开机自启</h3><button class="modal-close" data-close>×</button></div>
    <div class="modal-body">
      <div class="status-line">
        <span class="dot ${installed ? "installed" : "not-installed"}"></span>
        <b>${installed ? "已开启" : "未开启"}</b>
        <span class="muted">${installed ? `（计划任务状态：${esc(info.state || "已注册")}）` : "（开机后管理服务和应用都不会自动运行）"}</span>
      </div>
      <p class="muted" style="font-size:12.5px;line-height:1.8;margin:10px 0 16px">
        开启后会注册一个名为 <span class="mono">AppManager</span> 的 Windows 计划任务，
        管理服务会在开机后自动启动，并拉起所有勾选了“开机后自动启动”的应用。
      </p>
      <div class="radio-row">
        <label class="radio-card">
          <input type="radio" name="mode" value="boot" checked>
          <span><b>开机时启动（推荐，需管理员一次）</b><small>以 SYSTEM 账户运行，不需要登录 Windows，适合服务器。</small></span>
        </label>
        <label class="radio-card">
          <input type="radio" name="mode" value="logon">
          <span><b>用户登录后启动（无需管理员）</b><small>使用当前用户环境变量，适合依赖用户级 PATH / 配置的项目。</small></span>
        </label>
      </div>
      <div class="modal-note warn" style="margin-top:14px">
        「登录后启动」普通权限即可安装；「开机时启动」需要一次管理员授权。
        如果这里提示权限不足，请右键“以管理员身份运行” install-autostart.cmd。
      </div>
      <div class="output-box" id="autoOutput"></div>
    </div>
    <div class="modal-foot">
      ${installed ? '<button class="btn danger" id="btnUninstallAuto">移除开机自启</button>' : ""}
      <button class="btn primary" id="btnInstallAuto">${installed ? "重新安装" : "开启开机自启"}</button>
    </div>`);

  const output = $("#autoOutput", modal);
  const run = async (action) => {
    const mode = $("input[name=mode]:checked", modal).value;
    output.textContent = action === "install" ? "正在注册计划任务…" : "正在移除计划任务…";
    try {
      const res = await api("/api/autostart", { method: "POST", body: { action, mode } });
      output.textContent = (res.output || "").trim() || (res.ok ? "操作成功" : "操作失败");
      if (res.ok) {
        toast(action === "install" ? "开机自启已开启" : "开机自启已移除");
        await refreshState();
        setTimeout(() => openAutostart(), 900);
      } else {
        toast("操作失败，请查看下方输出", "err", 5000);
      }
    } catch (err) {
      output.textContent = err.message;
      toast(err.message, "err");
    }
  };

  $("#btnInstallAuto", modal).addEventListener("click", () => run("install"));
  const un = $("#btnUninstallAuto", modal);
  if (un) un.addEventListener("click", () => run("uninstall"));
}

/* ------------------------------------------------------------------ */
/* 服务日志                                                           */
/* ------------------------------------------------------------------ */

async function openSystemLog() {
  const modal = openModal(`
    <div class="modal-head"><h3>服务日志</h3><button class="modal-close" data-close>×</button></div>
    <div class="modal-body" style="padding:0">
      <div class="log-pane" style="max-height:60vh;min-height:320px;padding:10px 0">
        <div class="log-inner" id="sysInner"></div>
      </div>
    </div>
    <div class="modal-foot">
      <button class="btn ghost" data-close>关闭</button>
    </div>`);
  const inner = $("#sysInner", modal);
  let after = 0;

  const load = async () => {
    try {
      const data = await api(`/api/system/logs?after=${after}&limit=400`);
      (data.lines || []).forEach((line) => {
        inner.appendChild(makeLogLine(line));
        after = Math.max(after, line.seq);
      });
      if (!inner.childElementCount) {
        inner.innerHTML = '<div style="padding:24px;text-align:center;color:var(--dim)">暂无服务日志</div>';
      }
      const pane = inner.parentElement;
      pane.scrollTop = pane.scrollHeight;
    } catch (err) {
      /* 静默 */
    }
  };
  await load();
  state.systemTimer = setInterval(load, 2500);
}

/* ------------------------------------------------------------------ */
/* 事件绑定                                                           */
/* ------------------------------------------------------------------ */

$("#btnAdd").addEventListener("click", () => openAppModal());
$("#btnAddEmpty").addEventListener("click", () => openAppModal());
$("#btnEdit").addEventListener("click", () => runAction("edit"));
$("#btnDelete").addEventListener("click", () => runAction("delete"));
$("#btnStart").addEventListener("click", () => runAction("start"));
$("#btnStop").addEventListener("click", () => runAction("stop"));
$("#btnRestart").addEventListener("click", () => runAction("restart"));
$("#btnStartAll").addEventListener("click", startAll);
$("#btnStopAll").addEventListener("click", stopAll);
$("#btnSettings").addEventListener("click", openSettings);
$("#btnAutostart").addEventListener("click", openAutostart);
$("#btnSystem").addEventListener("click", openSystemLog);

$("#btnClearLog").addEventListener("click", async () => {
  const app = currentApp();
  if (!app) return;
  const yes = await confirmDialog({
    title: "清空日志",
    message: "日志文件与当前显示的内容都会被清空，确定吗？",
    confirmText: "清空",
    danger: true,
  });
  if (yes) runAction("clear-logs");
});

$("#btnCopyLog").addEventListener("click", async () => {
  const text = state.logs.map((l) => `[${fmtTime(l.ts)}] ${l.text}`).join("\n");
  try {
    await navigator.clipboard.writeText(text);
    toast("日志已复制");
  } catch (err) {
    toast("复制失败，请手动选择文本", "err");
  }
});

$("#btnDownloadLog").addEventListener("click", () => {
  const app = currentApp();
  if (!app) return;
  const q = state.token ? `?token=${encodeURIComponent(state.token)}` : "";
  window.open(`/api/apps/${encodeURIComponent(app.id)}/logs/download${q}`, "_blank");
});

el.logFilter.addEventListener("input", () => {
  state.filter = el.logFilter.value.trim().toLowerCase();
  const atBottom = el.logPane.scrollHeight - el.logPane.scrollTop - el.logPane.clientHeight < 40;
  renderLogsFull();
  if (!atBottom) return;
});

el.autoScroll.addEventListener("change", () => {
  state.autoScroll = el.autoScroll.checked;
  if (state.autoScroll) maybeScroll();
});

el.wrapLog.addEventListener("change", () => {
  $(".logs").classList.toggle("wrap", el.wrapLog.checked);
});

el.logPane.addEventListener("scroll", () => {
  const dist = el.logPane.scrollHeight - el.logPane.scrollTop - el.logPane.clientHeight;
  if (dist > 80 && state.autoScroll) {
    state.autoScroll = false;
    el.autoScroll.checked = false;
  } else if (dist < 10 && !state.autoScroll) {
    state.autoScroll = true;
    el.autoScroll.checked = true;
  }
}, { passive: true });

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && el.modalRoot.childElementCount) closeModal();
});

document.addEventListener("visibilitychange", () => {
  if (!document.hidden) {
    refreshState();
    refreshLogs(false);
  }
});

/* ------------------------------------------------------------------ */
/* 启动                                                               */
/* ------------------------------------------------------------------ */

(async function boot() {
  updateLogEmpty();
  updateServerPill();
  await refreshState();
  if (state.selectedId) {
    await refreshLogs(true);
  }
  const openPanel = new URLSearchParams(location.search).get("open");
  if (openPanel === "add") openAppModal();
  else if (openPanel === "settings") openSettings();
  else if (openPanel === "autostart") openAutostart();
  else if (openPanel === "system") openSystemLog();
  setInterval(() => {
    if (document.hidden) return;
    refreshState();
  }, 2000);
  setInterval(() => {
    if (document.hidden) return;
    refreshLogs(false);
  }, 1000);
  setInterval(() => {
    const app = currentApp();
    if (app && app.running && app.started_at) {
      el.appUptime.textContent = `已运行 ${fmtDuration((Date.now() - app.started_at) / 1000)}`;
    }
  }, 1000);
})();
