"use strict";
/* AutomateCert console. No frameworks, strict CSP (no inline script or style). */
(() => {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const state = { me: null, templates: [], cache: {}, chat: [], palette: [], paletteIndex: 0 };
  const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmtDate = d => d ? new Date(d).toLocaleString() : "—";
  const fmtDay = d => d ? new Date(d).toLocaleDateString() : "—";
  const daysLeft = d => d ? Math.floor((new Date(d) - Date.now()) / 86400000) : null;
  const can = { op: () => ["Admin", "Operator"].includes(state.me?.role), admin: () => state.me?.role === "Admin", approve: () => ["Admin", "Operator", "Approver"].includes(state.me?.role) };
  const cn = s => ((s || "").match(/CN=([^,]+)/) || [])[1] || s || "";
  const NS = "http://www.w3.org/2000/svg";
  const INTERNAL_TLDS = ["local", "localdomain", "lan", "corp", "internal", "intranet", "home", "private", "domain", "ad", "test", "example", "invalid", "localhost", "priv", "int", "loc", "office", "grp"];
  const isInternalName = n => {
    const x = String(n || "").replace(/^\*\./, "").replace(/\.$/, "").toLowerCase();
    if (!x.includes(".") || /^\d{1,3}(\.\d{1,3}){3}$/.test(x) || x.includes(":") || x.endsWith(".home.arpa")) return true;
    return INTERNAL_TLDS.includes(x.split(".").pop());
  };

  // ---------------- icons (stroke SVG) ----------------
  const ICONS = {
    home: '<path d="M3 11l9-8 9 8v9a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z"/>',
    spark: '<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z"/>',
    shield: '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/><path d="M9 12l2 2 4-4"/>',
    globe: '<circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a14 14 0 0 1 0 18a14 14 0 0 1 0-18z"/>',
    eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
    badge: '<circle cx="12" cy="9" r="6"/><path d="M9 9l2 2 4-4"/><path d="M8.5 14.5L7 22l5-3 5 3-1.5-7.5"/>',
    refresh: '<path d="M21 12a9 9 0 1 1-2.6-6.4"/><polyline points="21 3 21 8 16 8"/>',
    id: '<rect x="3" y="5" width="18" height="14" rx="2"/><circle cx="9" cy="11" r="2.5"/><path d="M5.5 17c.8-2 2-3 3.5-3s2.7 1 3.5 3"/><path d="M15 10h4"/><path d="M15 14h3"/>',
    list: '<path d="M9 6h12"/><path d="M9 12h12"/><path d="M9 18h12"/><circle cx="4.5" cy="6" r="1"/><circle cx="4.5" cy="12" r="1"/><circle cx="4.5" cy="18" r="1"/>',
    server: '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01"/><path d="M7 16.5h.01"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9"/><path d="M17 6l3 3"/><path d="M15 8l2 2"/>',
    file: '<path d="M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z"/><polyline points="14 3 14 8 19 8"/><path d="M8 13h8"/><path d="M8 17h5"/>',
    gear: '<path d="M4 6h10"/><path d="M18 6h2"/><circle cx="16" cy="6" r="2"/><path d="M4 12h4"/><path d="M12 12h8"/><circle cx="10" cy="12" r="2"/><path d="M4 18h12"/><path d="M20 18h0"/><circle cx="18" cy="18" r="2"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2"/><path d="M12 20v2"/><path d="M4.9 4.9l1.4 1.4"/><path d="M17.7 17.7l1.4 1.4"/><path d="M2 12h2"/><path d="M20 12h2"/><path d="M4.9 19.1l1.4-1.4"/><path d="M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M21 13A9 9 0 1 1 11 3a7 7 0 0 0 10 10z"/>',
    menu: '<path d="M4 7h16"/><path d="M4 12h16"/><path d="M4 17h16"/>',
    plus: '<path d="M12 5v14"/><path d="M5 12h14"/>',
    download: '<path d="M12 4v11"/><polyline points="7 10 12 15 17 10"/><path d="M5 20h14"/>',
    bolt: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
    more: '<circle cx="5" cy="12" r="1.5"/><circle cx="12" cy="12" r="1.5"/><circle cx="19" cy="12" r="1.5"/>'
  };
  const icon = n => `<svg class="ico" viewBox="0 0 24 24" aria-hidden="true">${ICONS[n] || ""}</svg>`;

  // ---------------- API ----------------
  async function api(path, opts = {}) {
    const res = await fetch("/api" + path, {
      method: opts.method || "GET",
      headers: { "Content-Type": "application/json", "X-AutomateCert-Client": "web" },
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
      credentials: "same-origin"
    });
    if (res.status === 204) return null;
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = { error: text }; }
    if (res.status === 401 && !opts.noAuthRedirect) { showAuth(); throw new Error((data && data.error) || "Please sign in."); }
    if (res.status === 403 && data && data.passwordChangeRequired) { showGate("password"); throw new Error(data.error); }
    if (res.status === 403 && data && data.mfaSetupRequired) { showGate("mfa"); throw new Error(data.error); }
    if (res.status === 403) throw new Error("Your role does not allow this action.");
    if (res.status === 429) throw new Error("Too many requests. Please wait a moment.");
    if (!res.ok) throw new Error((data && (data.error || data.title)) || `Request failed (${res.status})`);
    return data;
  }

  function toast(msg) {
    const t = $("#toast"); t.textContent = msg; t.classList.remove("hidden");
    clearTimeout(toast.h); toast.h = setTimeout(() => t.classList.add("hidden"), 6500);
  }
  async function copy(text) {
    try { await navigator.clipboard.writeText(text); toast("Copied to clipboard."); } catch { toast("Copy failed. Select the text and press Ctrl+C."); }
  }
  function download(name, text) {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
    const a = document.createElement("a"); a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  function md(text) {
    const lines = esc(text || "").split(/\r?\n/);
    let html = "", list = null;
    const inline = s => s.replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>");
    const close = () => { if (list) { html += `</${list}>`; list = null; } };
    for (const raw of lines) {
      const l = raw.trim(); let m;
      if ((m = l.match(/^[-*•]\s+(.*)$/))) { if (list !== "ul") { close(); html += "<ul>"; list = "ul"; } html += `<li>${inline(m[1])}</li>`; }
      else if ((m = l.match(/^\d+[.)]\s+(.*)$/))) { if (list !== "ol") { close(); html += "<ol>"; list = "ol"; } html += `<li>${inline(m[1])}</li>`; }
      else if (!l) close();
      else { close(); html += `<p>${inline(l.replace(/^#+\s*/, ""))}</p>`; }
    }
    close();
    return `<div class="md">${html}</div>`;
  }

  const expiryBadge = notAfter => {
    const d = daysLeft(notAfter);
    if (d === null) return `<span class="badge b-muted">Not issued</span>`;
    if (d < 0) return `<span class="badge b-bad">Expired</span>`;
    if (d <= 7) return `<span class="badge b-bad">${d} days</span>`;
    if (d <= 30) return `<span class="badge b-warn">${d} days</span>`;
    return `<span class="badge b-ok">${d} days</span>`;
  };
  const statusBadge = s => {
    const map = { Succeeded: "b-ok", Failed: "b-bad", Cancelled: "b-muted", AwaitingApproval: "b-warn", WaitingForManualDns: "b-warn", Scheduled: "b-brand" };
    return `<span class="badge ${map[s] || "b-info"}">${esc(String(s).replace(/([a-z])([A-Z])/g, "$1 $2"))}</span>`;
  };
  const FREE = ["Let's Encrypt", "ZeroSSL", "Google Trust Services", "Buypass"];
  const vendorTag = v => `<span class="vendor ${FREE.includes(v) ? "free" : ""}">${esc(v)}${FREE.includes(v) ? " · free" : ""}</span>`;
  const roleChips = roles => (roles || []).map(r => `<span class="role-chip">${esc(r)}</span>`).join("");
  const emptyState = (msg, ico = "shield") => `<div class="empty-state"><div class="big">${icon(ico)}</div><div>${msg}</div></div>`;
  function table(heads, rows, empty) {
    if (!rows.length) return emptyState(empty);
    return `<div class="table-wrap"><table class="responsive"><thead><tr>${heads.map(h => `<th>${esc(h)}</th>`).join("")}</tr></thead><tbody>${
      rows.map(r => `<tr>${r.map((c, i) => `<td data-label="${esc(heads[i] || "")}">${c}</td>`).join("")}</tr>`).join("")}</tbody></table></div>`;
  }
  const paBtn = (a, cls = "btn sm primary") => a ? `<button type="button" class="${cls}" data-action="pa" data-type="${esc(a.type)}" data-id="${esc(a.id || "")}" data-route="${esc(a.route || "")}">${esc(a.label)}</button>` : "";

  // ---------------- SVG charts ----------------
  function svg(w, h, cls = "chart") { const s = document.createElementNS(NS, "svg"); s.setAttribute("viewBox", `0 0 ${w} ${h}`); s.setAttribute("class", cls); s.setAttribute("role", "img"); return s; }
  function el(tag, attrs, text) { const e = document.createElementNS(NS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); if (text !== undefined) e.textContent = text; return e; }
  function barChart(items, classFor) {
    const W = 640, H = 220, pad = { l: 28, r: 8, t: 16, b: 30 }, s = svg(W, H);
    const max = Math.max(1, ...items.map(i => i.value)), iw = (W - pad.l - pad.r) / Math.max(1, items.length);
    for (let g = 0; g <= 4; g++) {
      const y = pad.t + (H - pad.t - pad.b) * g / 4;
      s.appendChild(el("line", { x1: pad.l, x2: W - pad.r, y1: y, y2: y, class: "grid-line" }));
      s.appendChild(el("text", { x: pad.l - 6, y: y + 3, "text-anchor": "end", class: "axis" }, String(Math.round(max * (4 - g) / 4))));
    }
    items.forEach((it, i) => {
      const h = (H - pad.t - pad.b) * it.value / max, x = pad.l + i * iw + iw * 0.2, w = iw * 0.6, y = H - pad.b - h;
      s.appendChild(el("rect", { x, y, width: w, height: Math.max(0, h), rx: 5, class: "bar " + (classFor ? classFor(it, i) : "") }));
      if (it.value > 0) s.appendChild(el("text", { x: x + w / 2, y: y - 4, "text-anchor": "middle", class: "val" }, String(it.value)));
      s.appendChild(el("text", { x: x + w / 2, y: H - 10, "text-anchor": "middle", class: "axis" }, it.label));
    });
    return s;
  }
  function stackedChart(days) {
    const W = 640, H = 200, pad = { l: 24, r: 6, t: 12, b: 26 }, s = svg(W, H);
    const max = Math.max(1, ...days.map(d => d.succeeded + d.failed)), iw = (W - pad.l - pad.r) / days.length;
    for (let g = 0; g <= 2; g++) {
      const y = pad.t + (H - pad.t - pad.b) * g / 2;
      s.appendChild(el("line", { x1: pad.l, x2: W - pad.r, y1: y, y2: y, class: "grid-line" }));
      s.appendChild(el("text", { x: pad.l - 5, y: y + 3, "text-anchor": "end", class: "axis" }, String(Math.round(max * (2 - g) / 2))));
    }
    days.forEach((d, i) => {
      const x = pad.l + i * iw + iw * 0.15, w = iw * 0.7, base = H - pad.b, k = (H - pad.t - pad.b) / max, hs = d.succeeded * k, hf = d.failed * k;
      if (hs) s.appendChild(el("rect", { x, y: base - hs, width: w, height: hs, rx: 2, class: "bar ok" }));
      if (hf) s.appendChild(el("rect", { x, y: base - hs - hf, width: w, height: hf, rx: 2, class: "bar bad" }));
      if (i % 5 === 0) s.appendChild(el("text", { x: x + w / 2, y: H - 8, "text-anchor": "middle", class: "axis" }, d.day));
    });
    return s;
  }
  function donut(items, centerText, centerCap) {
    const s = svg(150, 150), r = 56, c = 2 * Math.PI * r, total = items.reduce((a, b) => a + b.count, 0);
    s.appendChild(el("circle", { cx: 75, cy: 75, r, class: "seg seg-track" }));
    let offset = 0;
    items.forEach((it, i) => {
      const len = total ? c * it.count / total : 0;
      s.appendChild(el("circle", { cx: 75, cy: 75, r, class: `seg s${i % 6}`, "stroke-dasharray": `${len} ${c - len}`, "stroke-dashoffset": -offset, transform: "rotate(-90 75 75)" }));
      offset += len;
    });
    s.appendChild(el("text", { x: 75, y: 78, "text-anchor": "middle", class: "donut-center" }, centerText));
    s.appendChild(el("text", { x: 75, y: 94, "text-anchor": "middle", class: "donut-cap" }, centerCap));
    return s;
  }
  function ring(score, grade) {
    const s = svg(160, 160, ""); s.setAttribute("width", "160"); s.setAttribute("height", "160");
    const defs = el("defs", {}), g = el("linearGradient", { id: "ringGrad", x1: "0", y1: "0", x2: "1", y2: "1" });
    g.appendChild(el("stop", { offset: "0", class: "stop-a" })); g.appendChild(el("stop", { offset: "1", class: "stop-b" }));
    defs.appendChild(g); s.appendChild(defs);
    const r = 64, c = 2 * Math.PI * r, health = Math.max(0, 100 - score), len = c * health / 100;
    s.appendChild(el("circle", { cx: 80, cy: 80, r, fill: "none", "stroke-width": 12, class: "ring-track" }));
    s.appendChild(el("circle", { cx: 80, cy: 80, r, fill: "none", "stroke-width": 12, class: "ring-val", "stroke-linecap": "round", "stroke-dasharray": `${len} ${c - len}`, transform: "rotate(-90 80 80)" }));
    s.appendChild(el("text", { x: 80, y: 88, "text-anchor": "middle", class: "ring-grade" }, grade));
    s.appendChild(el("text", { x: 80, y: 108, "text-anchor": "middle", class: "ring-cap" }, "HEALTH " + health));
    return s;
  }
  function meter(pct, cls = "") {
    const s = svg(100, 10, ""); s.setAttribute("preserveAspectRatio", "none"); s.setAttribute("width", "100%"); s.setAttribute("height", "10");
    s.appendChild(el("rect", { x: 0, y: 0, width: Math.max(0, Math.min(100, pct)), height: 10, class: "meter-fill " + cls }));
    return s;
  }

  // ---------------- modal ----------------
  function openModal(title, html, onMount) {
    $("#modal-title").textContent = title; $("#modal-body").innerHTML = html;
    $("#modal").classList.remove("hidden"); if (onMount) onMount($("#modal-body"));
    const first = $("#modal-body input:not([type=hidden]):not([type=checkbox]), #modal-body select, #modal-body textarea"); if (first) first.focus();
  }
  const closeModal = () => $("#modal").classList.add("hidden");
  $("#modal-close").addEventListener("click", closeModal);
  $("#modal").addEventListener("click", e => { if (e.target.id === "modal") closeModal(); });

  function bindForm(root, handler) {
    const form = root.tagName === "FORM" ? root : $("form", root);
    form.addEventListener("submit", async e => {
      e.preventDefault();
      const err = $(".error", form); if (err) err.textContent = "";
      const btn = $("button[type=submit]", form); if (btn) btn.disabled = true;
      try { await handler(new FormData(form), form); }
      catch (ex) { if (err) err.textContent = ex.message; else toast(ex.message); }
      finally { if (btn) btn.disabled = false; }
    });
  }
  const codeBlock = (id, text) => `<div class="code-block"><pre class="log" id="${id}">${esc(text)}</pre><button class="btn sm copy" type="button" data-copy="${id}">Copy</button></div>`;
  const wireCopy = root => $$("[data-copy]", root).forEach(b => b.addEventListener("click", () => copy($("#" + b.dataset.copy, root).textContent)));

  // ---------------- theme ----------------
  function applyTheme(t) {
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem("ac-theme", t); } catch { /* ignore */ }
    const dark = t === "dark" || (t === "auto" && matchMedia("(prefers-color-scheme: dark)").matches);
    $("#theme-btn").innerHTML = icon(dark ? "sun" : "moon");
  }
  $("#theme-btn").addEventListener("click", () => {
    const cur = document.documentElement.dataset.theme;
    const dark = cur === "dark" || (cur === "auto" && matchMedia("(prefers-color-scheme: dark)").matches);
    applyTheme(dark ? "light" : "dark");
  });
  try { applyTheme(localStorage.getItem("ac-theme") || "auto"); } catch { applyTheme("auto"); }
  $("#menu-btn").innerHTML = icon("menu");
  $(".search-ico").innerHTML = icon("search");
  $(".spark").innerHTML = icon("spark");

  // ---------------- auth ----------------
  function pwStrength(v) {
    let s = 0; if (v.length >= 12) s += 35; if (v.length >= 16) s += 20; if (/[A-Z]/.test(v) && /[a-z]/.test(v)) s += 15; if (/\d/.test(v)) s += 15; if (/[^A-Za-z0-9]/.test(v)) s += 15;
    return Math.min(100, s);
  }
  function wireStrength(root) {
    const i = $("input[data-strength]", root), m = $(".pw-meter", root); if (!i || !m) return;
    const draw = () => { const p = pwStrength(i.value); m.innerHTML = ""; m.appendChild(meter(p, p < 50 ? "bad" : p < 80 ? "warn" : "")); };
    i.addEventListener("input", draw); draw();
  }

  async function showAuth() {
    $("#shell").classList.add("hidden"); $("#gate").classList.add("hidden"); $("#copilot-fab").classList.add("hidden");
    $("#copilot").classList.add("hidden"); $("#auth").classList.remove("hidden");
    let setup = false;
    try { setup = (await api("/setup/status", { noAuthRedirect: true })).required; } catch { /* ignore */ }
    const body = $("#auth-body");
    const logo = `<img src="/img/logo-dark.png" alt="AutomateCert" class="logo-light">`;
    if (setup) {
      body.innerHTML = `${logo}<div><h2>Welcome to AutomateCert</h2><p class="hint">Create your organization and the first administrator.</p></div>
        <form class="form"><label>Organization<input name="tenantName" required maxlength="200"></label>
        <label>Your name<input name="displayName" required maxlength="200"></label>
        <label>Work email<input name="email" type="email" required autocomplete="username"></label>
        <label>Password <small>12+ characters — a passphrase is best</small><input name="password" type="password" minlength="12" required autocomplete="new-password" data-strength="1"></label>
        <div class="pw-meter"></div>
        <div class="error"></div><button class="btn primary" type="submit">Create administrator</button></form>`;
      wireStrength(body);
      bindForm(body, async fd => { await api("/setup", { method: "POST", body: Object.fromEntries(fd), noAuthRedirect: true }); toast("Setup complete. Please sign in."); showAuth(); });
      return;
    }
    body.innerHTML = `${logo}<div><h2>Sign in</h2><p class="hint">AutomateCert</p></div>
      <form class="form"><label>Email<input name="email" type="email" required autocomplete="username"></label>
      <label>Password<input name="password" type="password" required autocomplete="current-password"></label>
      <div class="error"></div><button class="btn primary" type="submit">Continue</button>
      <p class="hint">Protected with TLS 1.3, two-step verification and account lockout.</p></form>`;
    bindForm(body, async fd => {
      const r = await api("/auth/login", { method: "POST", body: Object.fromEntries(fd), noAuthRedirect: true });
      if (r.mfaRequired) showMfaChallenge(r); else await boot();
    });
  }

  function showMfaChallenge(r) {
    const body = $("#auth-body"), appMode = r.method === "Totp";
    body.innerHTML = `<img src="/img/logo-dark.png" alt="AutomateCert" class="logo-light"><div><h2>Two-step verification</h2><p class="hint">${appMode ? "Enter the 6-digit code from your authenticator app." : esc(r.info || "Enter the code we emailed you.")}</p></div>
      <form class="form"><label><span id="code-label">Verification code</span><input name="code" required autocomplete="one-time-code" inputmode="numeric" maxlength="11"></label>
      <div class="error"></div><button class="btn primary" type="submit">Verify</button>
      <div class="row-actions"><button class="btn link" type="button" id="use-recovery">Use a recovery code</button>
      ${appMode ? "" : `<button class="btn link" type="button" id="resend">Resend code</button>`}<button class="btn link" type="button" id="back">Back</button></div></form>`;
    $("#use-recovery", body).addEventListener("click", () => { $("#code-label", body).textContent = "Recovery code (e.g. ABCDE-FGHIJ)"; const i = $("input[name=code]", body); i.removeAttribute("inputmode"); i.value = ""; i.focus(); });
    $("#back", body).addEventListener("click", showAuth);
    if (!appMode) $("#resend", body).addEventListener("click", async () => {
      try { const x = await api("/auth/login/mfa/resend", { method: "POST", body: { ticket: r.ticket }, noAuthRedirect: true }); toast(x.info); } catch (ex) { toast(ex.message); }
    });
    bindForm(body, async fd => {
      const x = await api("/auth/login/mfa", { method: "POST", body: { ticket: r.ticket, code: fd.get("code") }, noAuthRedirect: true });
      if (x.recoveryCodesLeft <= 2) toast(`Only ${x.recoveryCodesLeft} recovery codes left. Generate new ones in Settings.`);
      await boot();
    });
    $("input[name=code]", body).focus();
  }

  function mfaEnroll(container, onDone) {
    container.innerHTML = `<div class="grid"><p class="hint">Authenticator apps (Microsoft Authenticator, Google Authenticator, 1Password…) are the most secure and work even when email is down.</p>
      <div class="row-actions"><button class="btn primary" type="button" id="m-app">Authenticator app (recommended)</button><button class="btn" type="button" id="m-email">Email code</button></div><div id="m-step"></div></div>`;
    const step = $("#m-step", container);
    const finish = codes => {
      step.innerHTML = `<div class="grid mt"><div class="note ok"><b>Two-step verification is on.</b> Save these recovery codes somewhere safe. Each works once.</div>
        <div class="codes">${codes.map(c => `<code>${esc(c)}</code>`).join("")}</div>
        <div class="row-actions"><button class="btn" type="button" id="c-copy">Copy</button><button class="btn" type="button" id="c-dl">Download</button><button class="btn primary" type="button" id="c-done">I saved them — continue</button></div></div>`;
      $("#c-copy", step).addEventListener("click", () => copy(codes.join("\n")));
      $("#c-dl", step).addEventListener("click", () => download("automatecert-recovery-codes.txt", "AutomateCert recovery codes\n\n" + codes.join("\n")));
      $("#c-done", step).addEventListener("click", onDone);
    };
    $("#m-app", container).addEventListener("click", async () => {
      try {
        const e = await api("/me/mfa/totp/begin", { method: "POST" });
        step.innerHTML = `<div class="steps mt"><img class="qr" alt="QR code for authenticator app" src="${esc(e.qr)}">
          <form class="form"><p class="hint">1. Scan the QR code with your authenticator app.<br>Can't scan? Key: <code>${esc(e.secret)}</code></p>
          <label>2. Enter the 6-digit code<input name="code" required inputmode="numeric" autocomplete="one-time-code" maxlength="7"></label>
          <div class="error"></div><div><button class="btn primary" type="submit">Verify and turn on</button></div></form></div>`;
        bindForm(step, async fd => { const r = await api("/me/mfa/totp/confirm", { method: "POST", body: { code: fd.get("code") } }); finish(r.recoveryCodes); });
        $("input[name=code]", step).focus();
      } catch (ex) { toast(ex.message); }
    });
    $("#m-email", container).addEventListener("click", async () => {
      try {
        const x = await api("/me/mfa/email/begin", { method: "POST" });
        step.innerHTML = `<form class="form mt"><p class="hint">${esc(x.info)}</p><label>Code<input name="code" required inputmode="numeric" maxlength="7"></label>
          <div class="error"></div><div><button class="btn primary" type="submit">Verify and turn on</button></div></form>`;
        bindForm(step, async fd => { const r = await api("/me/mfa/email/confirm", { method: "POST", body: { code: fd.get("code") } }); finish(r.recoveryCodes); });
      } catch (ex) { step.innerHTML = `<div class="note bad mt">${esc(ex.message)}</div>`; }
    });
  }

  function showGate(kind) {
    $("#shell").classList.add("hidden"); $("#auth").classList.add("hidden"); $("#copilot-fab").classList.add("hidden");
    $("#gate").classList.remove("hidden");
    const b = $("#gate-body");
    const head = `<img src="/img/logo-dark.png" alt="AutomateCert" class="logo-light">`;
    const out = `<div><button class="btn link" type="button" id="gate-logout">Sign out</button></div>`;
    if (kind === "password") {
      b.innerHTML = `${head}<div><h2>Choose a new password</h2><p class="hint">Your administrator created this account with a temporary password.</p></div>
        <form class="form"><label>Temporary password<input name="currentPassword" type="password" required autocomplete="current-password"></label>
        <label>New password <small>12+ characters</small><input name="newPassword" type="password" minlength="12" required autocomplete="new-password" data-strength="1"></label>
        <div class="pw-meter"></div><div class="error"></div><div><button class="btn primary" type="submit">Save and continue</button></div></form>${out}`;
      wireStrength(b);
      bindForm(b, async fd => { await api("/me/password", { method: "POST", body: Object.fromEntries(fd) }); $("#gate").classList.add("hidden"); boot(); });
    } else {
      b.innerHTML = `${head}<div><h2>Set up two-step verification</h2><p class="hint">Your organization requires a second factor for every account.</p></div><div id="gate-enroll"></div>${out}`;
      mfaEnroll($("#gate-enroll", b), () => { $("#gate").classList.add("hidden"); boot(); });
    }
    $("#gate-logout", b).addEventListener("click", async () => { await api("/auth/logout", { method: "POST" }).catch(() => {}); showAuth(); });
  }

  // ---------------- navigation ----------------
  // ---------------- Disclaimers (shown in context; full text on About & legal) ----------------
  const LEGAL = {
    access: "Authorised use only. Activity is logged and audited. Unauthorised access is prohibited and may be reported.",
    ai: "AI answers can be wrong or incomplete. Copilot only reads data and proposes actions — verify before you approve anything. Data is sent only to the AI endpoint your administrator configured.",
    change: "These steps change live AD FS, WAP and Dynamics CRM configuration and may interrupt sign-in. Test in a non-production environment first, keep a current backup / AD FS configuration export, run inside an approved maintenance window and keep approval enabled.",
    ca: "Certificates are issued by the selected certificate authority under its own subscriber agreement and terms (for Let's Encrypt: the ISRG Subscriber Agreement). You are responsible for your CA contracts, fees, domain control and the accuracy of certificate data.",
    data: "CA balances, orders and Certificate Transparency data come from third-party services (CA APIs, crt.sh) and may be delayed or incomplete. Always confirm financial figures in your CA portal.",
    scan: "Only scan networks and hosts you own or are explicitly authorised to test."
  };
  const NAV = [
    { group: "Monitor", items: [["overview", "Overview", "home"], ["autopilot", "Autopilot", "spark"], ["certificates", "Certificates", "shield"], ["endpoints", "Endpoints", "globe"], ["transparency", "Transparency", "eye"], ["compliance", "Compliance", "badge"]] },
    { group: "Automate", items: [["profiles", "Automation", "refresh"], ["identity", "CRM & AD FS", "id"], ["jobs", "Jobs & approvals", "list"], ["agents", "Servers", "server"], ["issuers", "CAs & DNS", "key"]] },
    { group: "Administer", items: [["logs", "Logs & reports", "file"], ["settings", "Settings", "gear"], ["about", "About & legal", "badge"]] }
  ];
  const TITLES = Object.fromEntries(NAV.flatMap(g => g.items.map(i => [i[0], i[1]])));
  const SUBTITLES = {
    overview: "Live health of every certificate across servers, endpoints and certificate authorities",
    autopilot: "Prioritised risks with one-click actions — nothing runs without your approval",
    certificates: "Every certificate on enrolled servers — any CA, any vendor",
    endpoints: "Agentless monitoring and network discovery for appliances, Linux, cloud and partner sites",
    transparency: "Certificates issued for your domains in public Certificate Transparency logs",
    compliance: "Policy checks: key strength, signatures, lifetimes, trust and protocols",
    profiles: "Certificates AutomateCert renews and deploys automatically",
    identity: "AD FS farms, Web Application Proxy and Dynamics CRM — handled as one system",
    jobs: "Renewals, deployments, staged steps, approvals and diagnostics",
    agents: "Windows servers running the AutomateCert agent", issuers: "Certificate authorities, CA accounts and DNS validation providers",
    about: "Version, licence notices, disclaimers and support",
    logs: "Audit trail, system events and exports", settings: "Security, alerts, monitoring, AI assistant and users"
  };
  function renderNav() {
    $("#nav").innerHTML = NAV.map(g => `<div class="group"><div class="group-label">${g.group}</div>${g.items.map(([k, label, ic]) =>
      `<a href="#/${k}" data-view="${k}">${icon(ic)}<span>${label}</span>${k === "jobs" ? `<span class="pill hidden" id="nav-pending"></span>` : ""}</a>`).join("")}</div>`).join("");
    $("#tabbar").innerHTML = [["overview", "Home", "home"], ["certificates", "Certs", "shield"], ["autopilot", "Autopilot", "spark"], ["jobs", "Jobs", "list"]]
      .map(([k, l, ic]) => `<a href="#/${k}" data-view="${k}">${icon(ic)}<span>${l}</span></a>`).join("") + `<button type="button" id="tab-more">${icon("more")}<span>More</span></button>`;
    $("#tab-more").addEventListener("click", openSidebar);
  }
  const openSidebar = () => { $("#sidebar").classList.add("open"); $("#scrim").classList.remove("hidden"); };
  const closeSidebar = () => { $("#sidebar").classList.remove("open"); $("#scrim").classList.add("hidden"); };
  $("#menu-btn").addEventListener("click", openSidebar);
  $("#scrim").addEventListener("click", closeSidebar);
  function actions(html) { $("#actions").innerHTML = html; }

  // ---------------- command palette ----------------
  function paletteItems(q) {
    const items = [];
    NAV.forEach(g => g.items.forEach(([k, l, ic]) => items.push({ label: l, hint: "Go to", ico: ic, run: () => { location.hash = "#/" + k; } })));
    const acts = [
      ["New automation", "profile-new", "plus"], ["Free certificate (Let's Encrypt)", "free-cert", "bolt"], ["Connect servers", "agent-enroll", "server"],
      ["Add certificate authority", "issuer-new", "key"], ["Add DNS provider", "dns-new", "globe"], ["Monitor endpoints", "endpoint-new", "globe"],
      ["Discover network", "endpoint-discover", "search"], ["CRM & AD FS packs", "identity-open", "id"], ["Ask Copilot", "copilot-open", "spark"]
    ];
    acts.forEach(([l, a, ic]) => items.push({ label: l, hint: "Action", ico: ic, run: () => handlers[a] && handlers[a]() }));
    items.push({ label: "Toggle dark / light theme", hint: "Action", ico: "moon", run: () => $("#theme-btn").click() });
    (state.cache.certs || []).forEach(c => items.push({ label: cn(c.subject), hint: `${c.vendor} · ${c.agent} · ${fmtDay(c.notAfter)}`, ico: "shield", run: () => certDetails(c) }));
    const t = (q || "").toLowerCase().trim();
    return (t ? items.filter(i => (i.label + " " + i.hint).toLowerCase().includes(t)) : items).slice(0, 40);
  }
  function renderPalette() {
    const list = state.palette;
    $("#palette-list").innerHTML = list.map((i, n) => `<button type="button" class="palette-item ${n === state.paletteIndex ? "on" : ""}" data-n="${n}">${icon(i.ico)}<span>${esc(i.label)}</span><small>${esc(i.hint)}</small></button>`).join("")
      || `<div class="empty">No matches</div>`;
  }
  async function openPalette() {
    if ($("#shell").classList.contains("hidden")) return;
    if (!state.cache.certs) { try { state.cache.certs = await api("/certificates"); } catch { /* ignore */ } }
    $("#palette").classList.remove("hidden");
    const i = $("#palette-input"); i.value = ""; state.paletteIndex = 0; state.palette = paletteItems(""); renderPalette(); i.focus();
  }
  const closePalette = () => $("#palette").classList.add("hidden");
  $("#search-btn").addEventListener("click", openPalette);
  $("#palette").addEventListener("click", e => { if (e.target.id === "palette") closePalette(); const b = e.target.closest(".palette-item"); if (b) { closePalette(); state.palette[+b.dataset.n].run(); } });
  $("#palette-input").addEventListener("input", e => { state.paletteIndex = 0; state.palette = paletteItems(e.target.value); renderPalette(); });
  $("#palette-input").addEventListener("keydown", e => {
    if (e.key === "ArrowDown") { state.paletteIndex = Math.min(state.palette.length - 1, state.paletteIndex + 1); renderPalette(); e.preventDefault(); }
    else if (e.key === "ArrowUp") { state.paletteIndex = Math.max(0, state.paletteIndex - 1); renderPalette(); e.preventDefault(); }
    else if (e.key === "Enter" && state.palette[state.paletteIndex]) { closePalette(); state.palette[state.paletteIndex].run(); }
  });
  document.addEventListener("keydown", e => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openPalette(); }
    if (e.key === "Escape") { closeModal(); closePalette(); closeSidebar(); $("#copilot").classList.add("hidden"); }
  });

  // ======================= VIEWS =======================
  const views = {};

  views.overview = async () => {
    const [d, a, recs, sec] = await Promise.all([api("/dashboard"), api("/analytics"), api("/autopilot"), api("/security").catch(() => null)]);
    const kpi = (label, value, sub, cls = "") => `<div class="card kpi ${cls}"><div class="label">${label}</div><div class="value">${value}</div><div class="sub">${sub}</div></div>`;
    const headline = a.riskScore <= 10 ? "Your certificate estate is healthy." : a.riskScore <= 45 ? "A few items need attention." : "Action needed to prevent outages.";
    const steps = [
      [d.hasIssuer, "Certificate authority", "Free Let's Encrypt is pre-configured; add DigiCert, Sectigo, GlobalSign or AD CS.", "#/issuers"],
      [d.agents > 0, "Connect a server", "Install the agent on IIS, AD FS, CRM, Exchange or SQL servers.", "#/agents"],
      [d.hasDns, "Automatic DNS", "Cloudflare, Azure DNS, Route 53, your own name servers or CNAME delegation.", "#/issuers"],
      [d.profiles > 0, "Automate", "Adopt an existing certificate or create a free one.", "#/certificates"]
    ];
    const quick = steps.every(s => s[0]) ? "" : `<div class="card mt"><div class="card-head"><div><h3>Get to fully hands-off in four steps</h3><p class="card-sub">${steps.filter(s => s[0]).length} of 4 done</p></div></div>
      <div class="quickstart">${steps.map((s, i) => `<a class="qs-step ${s[0] ? "done" : ""}" href="${s[3]}"><span class="n">${s[0] ? "✓" : i + 1}</span><b>${s[1]}</b><span class="sub">${s[2]}</span></a>`).join("")}</div></div>`;
    const html = `<div class="card hero"><div id="ring"></div>
        <div><h2>${esc(headline)}</h2><p>${a.unmanagedExpiring30} certificate(s) without automation expire within 30 days · ${a.coverage}% of live server certificates automated · ${a.vendorCount} CA vendor(s) · ${a.freeCount} free certificate(s) · ${a.criticalIssues} critical compliance issue(s).</p>
          ${sec ? `<a class="chip" href="#/settings">${icon("shield")} Security score ${sec.score}%</a>` : ""}</div>
        <div class="stats"><div class="stat"><b>${a.successRate}%</b><span>renewal success (30d)</span></div><div class="stat"><b>${a.hoursSaved30}h</b><span>manual work saved</span></div><div class="stat"><b>${a.avgRenewMinutes}m</b><span>avg. time to renew</span></div></div></div>
      ${quick}
      <div class="grid kpis mt">
        ${kpi("Server certificates", d.certificates, `${d.expired} expired`, d.expired ? "bad" : "")}
        ${kpi("Expiring ≤ 7 days", d.expiring7, "needs attention now", d.expiring7 ? "bad" : "ok")}
        ${kpi("Expiring ≤ 30 days", d.expiring30, "renewal window", d.expiring30 ? "warn" : "ok")}
        ${kpi("Endpoints", d.endpoints, `${d.endpointsAtRisk} at risk`, d.endpointsAtRisk ? "warn" : "")}
        ${kpi("Automated", d.profilesEnabled, `${d.profiles} profiles`)}
        ${kpi("Servers online", `${d.agentsOnline}/${d.agents}`, "heartbeat < 5 min", d.agentsOnline < d.agents ? "warn" : "ok")}
      </div>
      <div class="grid two mt">
        <div class="card"><div class="card-head"><div><h3>${icon("spark")} Autopilot</h3><p class="card-sub">Highest-impact actions right now</p></div><a class="btn sm" href="#/autopilot">See all</a></div>
          <div class="grid">${recs.slice(0, 4).map(recCard).join("") || emptyState("Nothing to do — every risk is handled.", "spark")}</div></div>
        <div class="card"><h3>CA vendors</h3><p class="card-sub">Who issued your certificates</p><div class="donut-wrap"><div id="c-vendors"></div>
          <ul class="legend">${a.vendors.map((x, i) => `<li><span class="dot d${i % 6}"></span>${esc(x.name)}${x.free ? " · free" : ""}<span class="n">${x.count}</span></li>`).join("") || `<li class="hint">No data yet</li>`}</ul></div>
          <p class="section-title mt">Automation coverage · ${a.coverage}%</p><div class="meter" id="c-meter"></div></div>
      </div>
      <div class="grid two mt">
        <div class="card"><h3>Expiry forecast</h3><p class="card-sub">Certificates (servers + endpoints) expiring per week</p><div id="c-weeks"></div></div>
        <div class="card"><h3>Renewals, last 30 days</h3><p class="card-sub">${a.renewals30} succeeded · ${a.failed30} failed</p><div id="c-days"></div></div>
      </div>
      <div class="card mt"><h3>Next expiries</h3>${table(["Certificate", "Vendor", "Server", "Bound to", "Expires"],
        d.upcoming.map(u => [`<b>${esc(cn(u.subject))}</b><div class="sub mono">${esc(u.thumbprint)}</div>`, vendorTag(u.vendor), esc(u.agent), `<div class="sub">${esc(u.bindings || "Not bound")}</div>`, `${expiryBadge(u.notAfter)}<div class="sub">${fmtDay(u.notAfter)}</div>`]),
        "No certificates discovered yet. Connect a server to start.")}</div>`;
    return { html, mount: root => {
      $("#ring", root).appendChild(ring(a.riskScore, a.grade));
      $("#c-weeks", root).appendChild(barChart(a.expiryWeeks.map(w => ({ label: w.week, value: w.count })), (_, i) => i < 1 ? "hot" : i < 4 ? "warm" : ""));
      $("#c-vendors", root).appendChild(donut(a.vendors, String(a.vendors.reduce((s, x) => s + x.count, 0)), "CERTIFICATES"));
      $("#c-days", root).appendChild(stackedChart(a.renewalsDaily));
      $("#c-meter", root).appendChild(meter(a.coverage, a.coverage < 50 ? "bad" : a.coverage < 80 ? "warn" : ""));
    } };
  };

  const recCard = r => `<div class="rec ${esc(r.severity)}"><div><div class="cat">${esc(r.category)}</div><b>${esc(r.title)}</b><div class="sub">${esc(r.detail)}</div></div><div>${paBtn(r.action)}</div></div>`;

  views.autopilot = async () => {
    const recs = await api("/autopilot");
    actions(`<button class="btn primary" type="button" data-action="briefing">${icon("spark")} AI briefing</button>`);
    return `<div class="note brand">Autopilot continuously analyses inventory, renewals, endpoints, CT logs, CA subscriptions and security posture. Every action is a suggestion — nothing changes until you click.</div>
      <div id="briefing"></div>
      <div class="grid mt">${recs.map(recCard).join("") || `<div class="card">${emptyState("All clear. Autopilot found nothing that needs your attention.", "spark")}</div>`}</div>`;
  };

  views.certificates = async (params) => {
    const q = params.get("q") || "", days = params.get("days") || "", vendor = params.get("vendor") || "", auto = params.get("auto") || "";
    const qs = new URLSearchParams(); if (q) qs.set("q", q); if (days) qs.set("days", days);
    let list = await api(`/certificates${qs.toString() ? "?" + qs : ""}`);
    if (!q && !days) state.cache.certs = list;
    const vendors = [...new Set(list.map(c => c.vendor))].sort();
    if (vendor) list = list.filter(c => c.vendor === vendor);
    if (auto === "manual") list = list.filter(c => !c.managedBy);
    if (auto === "auto") list = list.filter(c => c.managedBy);
    actions(`<a class="btn" href="/api/reports/certificates.csv">${icon("download")} Export CSV</a>${can.op() ? `<button class="btn primary" type="button" data-action="free-cert">${icon("bolt")} Free certificate</button>` : ""}`);
    state.cache.certList = list;
    return `<form class="toolbar" id="cert-filter"><input name="q" placeholder="Search subject, SAN, issuer, thumbprint" value="${esc(q)}">
        <select name="days"><option value="">All expiries</option>${[7, 30, 60, 90].map(n => `<option value="${n}" ${days == n ? "selected" : ""}>≤ ${n} days</option>`).join("")}</select>
        <select name="vendor"><option value="">All vendors</option>${vendors.map(v => `<option ${v === vendor ? "selected" : ""}>${esc(v)}</option>`).join("")}</select>
        <select name="auto"><option value="">Automated + manual</option><option value="manual" ${auto === "manual" ? "selected" : ""}>Manual only</option><option value="auto" ${auto === "auto" ? "selected" : ""}>Automated only</option></select>
        <button class="btn" type="submit">Filter</button><span class="hint">${list.length} certificate(s)</span></form>
      <div class="card">${table(["Certificate", "Vendor", "Server", "Bindings", "Automation", "Expires"],
        list.map(c => [
          `<button type="button" class="btn link" data-action="cert-details" data-id="${c.id}"><b>${esc(cn(c.subject))}</b></button><div class="sub">${esc(c.dnsNames)}</div>${c.issues ? `<span class="badge b-warn mt-s">${c.issues} policy issue(s)</span>` : ""}`,
          `${vendorTag(c.vendor)}${c.internalOnly ? `<div class="sub">internal names</div>` : ""}`,
          `${esc(c.agent)}<div class="sub">${esc(c.keyInfo || "")}${c.hasPrivateKey ? " · key" : " · no key"}</div>`,
          `<div class="sub">${esc(c.bindings || "—")}</div>`,
          c.managedBy ? `<span class="badge b-ok">${esc(c.managedBy)}</span>` : `<span class="badge b-muted">Manual</span>${can.op() && c.hasPrivateKey ? `<div class="mt-s"><button class="btn sm primary" type="button" data-action="adopt" data-id="${c.id}">Adopt</button></div>` : ""}`,
          `${expiryBadge(c.notAfter)}<div class="sub">${fmtDay(c.notAfter)}</div>`]),
        "No certificates match.")}</div>`;
  };

  function certDetails(c) {
    openModal(cn(c.subject), `<dl class="kv"><dt>Subject</dt><dd>${esc(c.subject)}</dd><dt>DNS names</dt><dd>${esc(c.dnsNames || "—")}</dd>
      <dt>Vendor / issuer</dt><dd>${vendorTag(c.vendor)} <div class="sub">${esc(c.issuer)}</div></dd><dt>Server</dt><dd>${esc(c.agent)}</dd>
      <dt>Bindings</dt><dd>${esc(c.bindings || "—")}</dd><dt>Key / signature</dt><dd>${esc(c.keyInfo || "?")} · ${esc(c.signatureAlgorithm || "?")}</dd>
      <dt>Validity</dt><dd>${fmtDay(c.notBefore)} → ${fmtDay(c.notAfter)} ${expiryBadge(c.notAfter)}</dd><dt>Thumbprint</dt><dd class="mono">${esc(c.thumbprint)}</dd>
      <dt>Automation</dt><dd>${c.managedBy ? esc(c.managedBy) : "Manual"}</dd></dl>
      ${!c.managedBy && can.op() && c.hasPrivateKey ? `<div class="mt"><button class="btn primary" type="button" data-action="adopt" data-id="${c.id}">Adopt (zero downtime)</button></div>` : ""}`);
  }

  views.endpoints = async () => {
    const list = await api("/endpoints");
    actions(`<a class="btn" href="/api/reports/endpoints.csv">${icon("download")} CSV</a>${can.op() ? `<button class="btn" data-action="endpoint-discover" type="button">${icon("search")} Discover network</button><button class="btn primary" data-action="endpoint-new" type="button">${icon("plus")} Monitor endpoints</button>` : ""}`);
    const trust = e => e.lastError ? `<span class="badge b-bad">Unreachable</span><div class="sub">${esc(e.lastError)}</div>`
      : e.chainValid === false ? `<span class="badge b-bad">Untrusted</span><div class="sub">${esc(e.chainStatus || "")}</div>`
      : e.nameMatches === false ? `<span class="badge b-warn">Name mismatch</span>` : e.lastCheckedAt ? `<span class="badge b-ok">Trusted</span>` : `<span class="badge b-muted">Scanning…</span>`;
    return `<div class="card">${table(["Endpoint", "Certificate", "Vendor", "Trust", "TLS", "Expires", ""],
      list.map(e => [`<b>${esc(e.host)}:${e.port}</b>${e.label ? `<div class="sub">${esc(e.label)}</div>` : ""}<div class="sub">checked ${fmtDate(e.lastCheckedAt)}</div>`,
        `${esc(cn(e.subject) || "—")}<div class="sub">${esc(e.keyInfo || "")}</div>`, e.issuer ? vendorTag(e.vendor) : "—", trust(e), esc(e.tlsProtocol || "—"),
        e.notAfter ? `${expiryBadge(e.notAfter)}<div class="sub">${fmtDay(e.notAfter)}</div>` : "—",
        can.op() ? `<div class="row-actions"><button class="btn sm" type="button" data-action="endpoint-scan" data-id="${e.id}">Scan</button><button class="btn sm danger" type="button" data-action="endpoint-delete" data-id="${e.id}">Remove</button></div>` : ""]),
      "No endpoints yet. Paste host names or discover a network range.")}</div>`;
  };

  views.transparency = async () => {
    const t = await api("/transparency");
    actions(can.op() ? `<button class="btn primary" type="button" data-action="ct-sync">${icon("refresh")} Check CT logs now</button>` : "");
    const unknown = t.items.filter(i => !i.known && !i.acknowledged).length;
    return `<div class="note">Watching: <b>${esc(t.domains || "no domains")}</b> — change under Settings → Monitoring. Sources: public Certificate Transparency logs via crt.sh (refreshed every 12 h). ${unknown ? `<b>${unknown}</b> certificate(s) are not in your inventory.` : ""}</div>
      <div class="card mt">${table(["Names", "Vendor", "Issued", "Expires", "Status", ""],
        t.items.map(i => [`<b>${esc(i.names.split(",")[0])}</b><div class="sub">${esc(i.names)}</div>`, vendorTag(i.vendor), fmtDay(i.notBefore), `${expiryBadge(i.notAfter)}`,
          i.known ? `<span class="badge b-ok">In inventory</span>` : i.acknowledged ? `<span class="badge b-muted">Acknowledged</span>` : `<span class="badge b-warn">Unknown</span>`,
          !i.known && !i.acknowledged && can.op() ? `<button class="btn sm" type="button" data-action="ct-ack" data-id="${i.id}">Mark expected</button>` : ""]),
        "No CT data yet. Add domains to watch in Settings → Monitoring, then check now.")}</div>`;
  };

  views.compliance = async () => {
    const c = await api("/compliance");
    const sev = { critical: "b-bad", warning: "b-warn", info: "b-info" };
    return { html: `<div class="grid halves"><div class="card"><h3>Policy compliance</h3><p class="card-sub">${c.clean} of ${c.total} certificates pass every rule</p>
        <div class="kpi"><div class="value">${c.score}%</div></div><div class="meter mt" id="cmeter"></div></div>
        <div class="card"><h3>Rules checked</h3><p class="card-sub">CA/Browser Forum baseline + enterprise policy</p><div class="sub">RSA ≥ 2048 · no SHA-1/MD5 signatures · lifetimes within public limits · no self-signed certificates on services · SAN present · trusted chain · host name match · no TLS 1.0/1.1 · expired-but-bound detection · wildcard usage</div></div></div>
      <div class="card mt">${table(["Certificate", "Where", "Rule", "Severity", "Detail"],
        c.items.map(i => [`<b>${esc(cn(i.subject))}</b><div class="sub">${esc(i.source)}</div>`, esc(i.where), esc(i.rule), `<span class="badge ${sev[i.severity] || "b-muted"}">${esc(i.severity)}</span>`, `<div class="sub">${esc(i.detail)}</div>`]),
        "Every certificate passes the policy checks.")}</div>`,
      mount: root => $("#cmeter", root).appendChild(meter(c.score, c.score < 60 ? "bad" : c.score < 85 ? "warn" : "")) };
  };

  views.profiles = async () => {
    const list = await api("/profiles");
    state.cache.profiles = list;
    actions(can.op() ? `<button class="btn" type="button" data-action="free-cert">${icon("bolt")} Free certificate</button><button class="btn primary" data-action="profile-new" type="button">${icon("plus")} New automation</button>` : "");
    const tname = k => (state.templates.find(x => x.key === k) || {}).displayName || k;
    return `<div class="card">${table(["Profile", "CA / server", "Deploys to", "Current", "Next renewal", "State", ""],
      list.map(p => [`<b>${esc(p.name)}</b>${p.scenario ? ` <span class="role-chip">${esc(p.scenario)}</span>` : ""}<div class="sub">${esc(p.domains.join(", "))}</div>`,
        `${esc(p.issuer)}${p.isFree ? ` <span class="vendor free">free</span>` : ""}<div class="sub">${esc(p.agent)} · ${esc(p.keyAlgorithm)}</div>`,
        `<div class="sub">${p.targets.map(t => esc(tname(t.template)) + (t.agent ? ` → ${esc(t.agent)}` : "")).join("<br>") || "Install only"}</div>`,
        `${expiryBadge(p.currentNotAfter)}`, p.nextRenewal ? fmtDay(p.nextRenewal) : "On first run",
        `${p.enabled ? (p.due ? `<span class="badge b-warn">Renewal due</span>` : `<span class="badge b-ok">Automated</span>`) : `<span class="badge b-muted">Paused</span>`}${p.requireApproval ? `<div class="sub">Approval required</div>` : ""}${p.windowStartHour != null ? `<div class="sub">Window ${p.windowStartHour}:00–${p.windowEndHour}:00 UTC</div>` : ""}`,
        can.op() ? `<div class="row-actions"><button class="btn sm primary" data-action="profile-renew" data-id="${p.id}" type="button">Renew now</button><button class="btn sm" data-action="profile-edit" data-id="${p.id}" type="button">Edit</button><button class="btn sm danger" data-action="profile-delete" data-id="${p.id}" type="button">Delete</button></div>` : ""]),
      "No automation yet. Fastest path: Certificates → Adopt, or Free certificate.")}</div>`;
  };

  views.identity = async () => {
    const d = await api("/identity");
    actions(can.op() ? `<button class="btn primary" type="button" data-action="identity-open">${icon("id")} Guided packs</button>` : "");
    const by = r => d.servers.filter(s => s.roles.includes(r));
    const certRow = c => `<div class="sub">${esc(c.bindings)} — <b>${esc(cn(c.subject))}</b> ${expiryBadge(c.notAfter)}</div>`;
    const server = s => `<div class="card flat"><div class="card-head"><div><b>${esc(s.name)}</b><div>${roleChips(s.roles)}</div></div>${s.online ? `<span class="badge b-ok">Online</span>` : `<span class="badge b-warn">Offline</span>`}</div>
      ${s.certificates.map(certRow).join("") || `<div class="sub">No bound certificates reported.</div>`}${s.roles.includes("ADFS-AutoRollover") ? `<div class="note mt-s">AD FS manages its own token-signing/decrypting certificates (AutoCertificateRollover on).</div>` : ""}</div>`;
    return `<div class="note brand"><b>One system, not three.</b> AD FS SSL, Web Application Proxy, token-signing and Dynamics CRM claims encryption depend on each other. AutomateCert orchestrates them in order — node by node, staged secondary → primary, then refreshes CRM relying parties and reloads CRM — with approvals, maintenance windows, verification and rollback.</div>
      <div class="grid three mt">
        <div class="card"><h3>AD FS farm</h3><p class="card-sub">${by("ADFS").length} server(s)</p><div class="grid">${by("ADFS").map(server).join("") || emptyState("No AD FS server connected.", "server")}</div></div>
        <div class="card"><h3>Web Application Proxy</h3><p class="card-sub">${by("WAP").length} server(s)</p><div class="grid">${by("WAP").map(server).join("") || emptyState("No WAP server connected.", "server")}</div></div>
        <div class="card"><h3>Dynamics CRM</h3><p class="card-sub">${by("CRM").length} server(s)</p><div class="grid">${by("CRM").map(server).join("") || emptyState("No CRM server connected.", "server")}</div></div>
      </div>
      <div class="grid halves mt">
        <div class="card"><h3>Guided automation packs</h3><p class="card-sub">Pre-built, correctly ordered workflows</p><div class="grid">
          ${[["pack-adfs-ssl", "AD FS SSL + service communications", "Per farm node, with WAP. Restarts AD FS inside the maintenance window."],
             ["pack-crm", "Dynamics CRM claims / IFD certificate", "Swap bindings on CRM, set claims encryption certificate, iisreset, then refresh CRM relying parties on AD FS."],
             ["pack-token", "AD FS token-signing (staged)", "Add as secondary → wait → promote to primary → reload CRM on every front-end."]]
            .map(([a, t, s]) => `<div class="rec"><div><b>${t}</b><div class="sub">${s}</div></div><div>${can.op() ? `<button class="btn sm primary" type="button" data-action="${a}">Set up</button>` : ""}</div></div>`).join("")}</div></div>
        <div class="card"><h3>Active identity automations</h3><p class="card-sub">Profiles that touch AD FS, WAP or CRM</p>${table(["Profile", "Steps", "Expires", "Next renewal"],
          d.profiles.map(p => [`<b>${esc(p.name)}</b>${p.scenario ? ` <span class="role-chip">${esc(p.scenario)}</span>` : ""}`, `<div class="sub">${p.targets.map(esc).join(" → ")}</div>`, expiryBadge(p.currentNotAfter), p.nextRenewal ? fmtDay(p.nextRenewal) : "—"]),
          "None yet — use a guided pack.")}</div>
      </div>
      <div class="card mt"><h3>Runbook: what remains manual and why</h3><div class="sub">
        <p><b>Microsoft 365 federated domains</b> — after promoting a new token-signing certificate, update the federation settings in Entra ID (e.g. with Microsoft Graph PowerShell <code>Update-MgDomainFederationConfiguration</code>) unless the domain uses metadata auto-refresh. AutomateCert schedules the promotion and alerts you.</p>
        <p><b>Third-party SaaS relying parties without metadata URLs</b> — export the new public token-signing certificate and upload it at the SaaS provider before promotion.</p>
        <p><b>CRM IFD reconfiguration</b> is only needed when host names change, not for certificate renewal.</p></div></div>`;
  };

  views.jobs = async (params) => {
    const status = params.get("status") || "";
    const list = await api(status ? `/jobs?status=${encodeURIComponent(status)}` : "/jobs");
    const opts = ["", "AwaitingApproval", "Scheduled", "WaitingForManualDns", "Pending", "Deploying", "Succeeded", "Failed", "Cancelled"];
    return `<form class="toolbar" id="job-filter"><select name="status">${opts.map(o => `<option value="${o}" ${o === status ? "selected" : ""}>${o ? o.replace(/([a-z])([A-Z])/g, "$1 $2") : "All statuses"}</option>`).join("")}</select><button class="btn" type="submit">Filter</button></form>
      <div class="card">${table(["Job", "Target", "Status", "Requested", ""],
        list.map(j => [`<b>${esc(j.type)}${j.phase ? " · " + esc(j.phase) : ""}</b><div class="sub mono">${esc(j.id.slice(0, 8))}</div>${j.parentJobId ? `<div class="sub">follow-up</div>` : ""}`,
          `${esc(j.profile || "—")}<div class="sub">${esc(j.agent)}</div>`,
          `${statusBadge(j.status)}${j.notBefore && j.status === "Scheduled" ? `<div class="sub">at ${fmtDate(j.notBefore)}</div>` : ""}<div class="sub">${esc(j.message || "")}</div>`,
          `${esc(j.requestedBy)}<div class="sub">${fmtDate(j.createdAt)}</div>${j.approvedBy ? `<div class="sub">Approved: ${esc(j.approvedBy)}</div>` : ""}`,
          `<div class="row-actions">${j.status === "Failed" ? `<button class="btn sm primary" data-action="job-diagnose" data-id="${j.id}" type="button">${icon("spark")} Diagnose</button>` : ""}
            <button class="btn sm" data-action="job-log" data-id="${j.id}" type="button">Log</button>
            ${can.approve() && j.status === "AwaitingApproval" ? `<button class="btn sm primary" data-action="job-approve" data-id="${j.id}" type="button">Approve</button><button class="btn sm danger" data-action="job-reject" data-id="${j.id}" type="button">Reject</button>` : ""}
            ${can.op() && j.status === "WaitingForManualDns" ? `<button class="btn sm primary" data-action="job-dns" data-id="${j.id}" type="button">DNS ready</button>` : ""}
            ${can.op() && j.status === "Scheduled" ? `<button class="btn sm" data-action="job-run-now" data-id="${j.id}" type="button">Run now</button>` : ""}
            ${can.op() && !["Succeeded", "Failed", "Cancelled", "Deploying", "Issuing"].includes(j.status) ? `<button class="btn sm danger" data-action="job-cancel" data-id="${j.id}" type="button">Cancel</button>` : ""}</div>`]),
        "No jobs yet.")}</div>`;
  };

  views.agents = async () => {
    const list = await api("/agents");
    state.cache.agents = list;
    actions(can.admin() ? `<button class="btn primary" data-action="agent-enroll" type="button">${icon("plus")} Connect servers</button>` : "");
    return `<div class="card">${table(["Server", "Roles", "Status", "Certificates", "Agent", "Last seen", ""],
      list.map(a => [`<b>${esc(a.name)}</b><div class="sub">${esc(a.osVersion || "")}</div>`, roleChips(a.roles) || "—",
        a.revoked ? `<span class="badge b-bad">Revoked</span>` : a.online ? `<span class="badge b-ok">Online</span>` : `<span class="badge b-warn">Offline</span>`,
        String(a.certificates), esc(a.version || "—"), `${fmtDate(a.lastSeenAt)}<div class="sub">${esc(a.lastIp || "")}</div>`,
        `<div class="row-actions">${can.op() && !a.revoked ? `<button class="btn sm" data-action="agent-discover" data-id="${a.id}" type="button">Discover now</button>` : ""}${can.admin() && !a.revoked ? `<button class="btn sm danger" data-action="agent-revoke" data-id="${a.id}" type="button">Revoke</button>` : ""}</div>`]),
      "No servers connected yet. Click “Connect servers”.")}</div>`;
  };

  views.issuers = async (params) => {
    const tab = params.get("tab") || "cas";
    const tabs = `<div class="tabs">${[["cas", "Certificate authorities"], ["accounts", "CA accounts & balances"], ["dns", "DNS providers"]].map(([k, l]) => `<a class="tab ${k === tab ? "on" : ""}" href="#/issuers?tab=${k}">${l}</a>`).join("")}</div>`;
    if (tab === "accounts") {
      const d = await api("/ca-accounts");
      actions(can.admin() ? `<button class="btn primary" data-action="ca-account-new" type="button">${icon("plus")} Connect CA account</button>` : "");
      const dataNote = `<div class="note mini mt">${esc(LEGAL.data)}</div>`;
      const acct = a => {
        const s = a.snapshot || {};
        return `<div class="card"><div class="card-head"><div><h3>${esc(a.name)}</h3><p class="card-sub">${esc(a.type)} · synced ${fmtDate(a.lastSyncAt)}</p></div>
          <div class="row-actions">${can.op() ? `<button class="btn sm" type="button" data-action="ca-sync" data-id="${a.id}">Sync</button>` : ""}${can.admin() ? `<button class="btn sm danger" type="button" data-action="ca-account-delete" data-id="${a.id}">Remove</button>` : ""}</div></div>
          ${a.lastError ? `<div class="note bad">${esc(a.lastError)}</div>` : ""}
          <div class="grid kpis">${s.balance != null ? `<div class="kpi"><div class="label">Balance</div><div class="value">${esc(s.balance)} <small>${esc(s.currency || "")}</small></div><div class="sub">available ${esc(s.availableFunds ?? "—")}</div></div>` : ""}
            <div class="kpi"><div class="label">Orders</div><div class="value">${esc(s.totalOrders ?? "—")}</div><div class="sub">${s.issued != null ? `${s.issued} issued · ${s.pending} pending` : esc(s.note || "")}</div></div>
            ${s.expiring30 != null ? `<div class="kpi ${s.expiring30 ? "warn" : "ok"}"><div class="label">Expiring ≤ 30d</div><div class="value">${s.expiring30}</div><div class="sub">${s.coverageEnding60} order coverage ending ≤ 60d</div></div>` : ""}</div>
          ${(s.recent || []).length ? table(["Certificate", "Product", "Status", "Expires"], s.recent.map(r => [esc(r.name), esc(r.product), esc(r.status), esc(r.expires)]), "") : ""}</div>`;
      };
      return `${tabs}<div class="grid halves"><div class="card"><h3>Certificates by vendor</h3><p class="card-sub">From your live inventory</p>${table(["Vendor", "Certificates", ""], d.vendors.map(v => [vendorTag(v.vendor), String(v.count), v.free ? `<span class="sub">Free — no balance</span>` : ""]), "No data yet.")}</div>
        <div class="card"><h3>Issued by AutomateCert (90 days)</h3><p class="card-sub">Per certificate authority</p>${table(["CA", "Issued"], d.issuedByAutomateCert.map(i => [esc(i.issuer), String(i.count)]), "No renewals yet.")}</div></div>
        <div class="grid mt">${d.accounts.map(acct).join("") || `<div class="card">${emptyState("Connect DigiCert CertCentral or Sectigo SCM to see balances, orders and upcoming expirations.", "key")}</div>`}</div>${dataNote}`;
    }
    if (tab === "dns") {
      const [dns, types] = await Promise.all([api("/dns-providers"), api("/dns-provider-types")]);
      state.cache.dnsTypes = types; state.cache.dns = dns;
      actions(can.admin() ? `<button class="btn primary" data-action="dns-new" type="button">${icon("plus")} Add DNS provider</button>` : "");
      return `${tabs}<div class="note">Public CAs check DNS TXT records. Use any provider with an API, your own name servers (RFC 2136 / Windows DNS), a webhook, or <b>CNAME delegation</b> for registrars without an API (e.g. Dynadot): one CNAME per name, then renewals are automatic.</div>
        <div class="card mt">${table(["Provider", "Type", "Mode", "Last test", "Used by", ""],
          dns.map(d => [`<b>${esc(d.name)}</b>`, esc(d.typeName), d.aliasZone ? `<span class="badge b-info">CNAME → ${esc(d.aliasZone)}</span>` : d.type === "Manual" ? `<span class="badge b-muted">Manual</span>` : `<span class="badge b-ok">Automatic</span>`,
            d.lastTestAt ? `${d.lastTestOk ? `<span class="badge b-ok">Passed</span>` : `<span class="badge b-bad">Failed</span>`}<div class="sub">${fmtDate(d.lastTestAt)}</div>` : `<span class="sub">Not tested</span>`,
            `${d.profiles} profile(s)`,
            `<div class="row-actions">${can.op() && d.type !== "Manual" ? `<button class="btn sm primary" type="button" data-action="dns-test" data-id="${d.id}">Test</button>` : ""}${d.aliasZone ? `<button class="btn sm" type="button" data-action="dns-cnames" data-id="${d.id}">CNAMEs</button>` : ""}${can.admin() ? `<button class="btn sm" type="button" data-action="dns-edit" data-id="${d.id}">Edit</button><button class="btn sm danger" data-action="dns-delete" data-id="${d.id}" type="button">Delete</button>` : ""}</div>`]),
          "No DNS providers.")}</div>`;
    }
    const issuers = await api("/issuers");
    actions(can.admin() ? `<button class="btn primary" data-action="issuer-new" type="button">${icon("plus")} Add CA</button>` : "");
    return `${tabs}<div class="card">${table(["Name", "Type", "Endpoint", "Coverage", "Used by", ""],
      issuers.map(i => [`<b>${esc(i.name)}</b>${i.isFree ? ` <span class="vendor free">free</span>` : ""}`,
        i.type === "Acme" ? `<span class="badge b-brand">ACME</span> ${i.isPublic ? `<span class="badge b-info">Public</span>` : `<span class="badge b-muted">Private</span>`}${i.hasEab ? ` <span class="badge b-muted">EAB</span>` : ""}` : `<span class="badge b-info">AD CS</span>`,
        `<div class="sub">${esc(i.type === "Acme" ? i.directoryUrl : `${i.adcsConfig} · ${i.adcsTemplate}`)}</div>${i.type === "Acme" ? (i.registered ? `<span class="badge b-ok">Account registered</span>` : `<span class="badge b-muted">Registers on first order</span>`) : ""}`,
        i.isFree ? `<span class="sub">Free · no subscription</span>` : i.coverageEndsAt ? `${expiryBadge(i.coverageEndsAt)}<div class="sub">${fmtDay(i.coverageEndsAt)}</div>` : `<span class="sub">Not set</span>`,
        `${i.profiles} profile(s)`,
        can.admin() ? `<div class="row-actions">${!i.isFree ? `<button class="btn sm" type="button" data-action="issuer-coverage" data-id="${i.id}">Coverage</button>` : ""}<button class="btn sm danger" data-action="issuer-delete" data-id="${i.id}" type="button">Delete</button></div>` : ""]),
      "No CAs yet.")}</div>`;
  };

  views.about = async () => {
    const h = await fetch("/healthz").then(r => r.json()).catch(() => ({ version: "?" }));
    const sec = Object.entries({ "Authorised use": LEGAL.access, "Automated changes": "AutomateCert installs certificates and changes service bindings (IIS, AD FS, WAP, Exchange, RDP, SQL Server, Dynamics CRM) on servers where you install the agent and configure automation. Each deployment is verified and rolled back on failure, but no automation can guarantee zero impact. Use approvals and maintenance windows for production systems and validate with a staging certificate authority first.",
      "Certificate authorities": LEGAL.ca, "Identity systems (AD FS & CRM)": LEGAL.change, "AI assistant": LEGAL.ai, "Third-party data": LEGAL.data, "Network discovery": LEGAL.scan,
      "No warranty": "The software is provided under the licence in the LICENSE file. Except as stated in that licence, it is provided \"as is\" without warranties of any kind, and the authors are not liable for indirect or consequential damages such as service interruption or certificate mis-issuance. This summary does not replace the licence.",
      "Privacy": "AutomateCert stores user names, email addresses, IP addresses of sign-ins and certificate metadata in your own SQL Server. Private keys never leave the servers they are generated on. Secrets are encrypted at rest. Nothing is sent to the software authors." });
    return `<div class="grid halves"><div class="card"><img src="/img/logo-dark.png" alt="AutomateCert" class="about-logo">
        <h3 class="mt-s">AutomateCert</h3><p class="card-sub">Version ${esc(h.version)} </p>
        <div class="sub">Certificate lifecycle management for Windows estates: discovery, multi-CA automation, AD FS &amp; Dynamics CRM orchestration, monitoring, alerts and audit.</div>
        <div class="row-actions mt"></div>
        <p class="hint mt">Open-source components: .NET (MIT), Entity Framework Core (MIT), Certes (MIT), MailKit/MimeKit (MIT), DnsClient.NET (Apache 2.0), QRCoder (MIT). Full notices ship in THIRD-PARTY-NOTICES.txt.</p></div>
      <div class="card"><h3>Disclaimers</h3><div class="grid">${sec.map(([t, d]) => `<div><b>${esc(t)}</b><div class="sub">${esc(d)}</div></div>`).join("")}</div></div></div>`;
  };
  views.logs = async (params) => {
    const tab = params.get("tab") || "audit";
    const tabs = `<div class="tabs">${[["audit", "Audit trail"], ...(can.admin() ? [["system", "System events"]] : []), ["exports", "Reports"]].map(([k, l]) => `<a class="tab ${k === tab ? "on" : ""}" href="#/logs?tab=${k}">${l}</a>`).join("")}</div>`;
    if (tab === "system" && can.admin()) {
      const level = params.get("level") || "";
      const list = await api(`/logs/system${level ? "?level=" + level : ""}`);
      return `${tabs}<div class="toolbar">${["", "Error", "Warning", "Information"].map(l => `<a class="btn sm ${l === level ? "primary" : ""}" href="#/logs?tab=system&level=${l}">${l || "All"}</a>`).join("")}<span class="hint">Last ${list.length} events since the service started · also written to Windows Event Log</span></div>
        <div class="card">${table(["Time", "Level", "Source", "Message"], list.map(e => [fmtDate(e.at), `<span class="badge ${e.level === "Error" || e.level === "Critical" ? "b-bad" : e.level === "Warning" ? "b-warn" : "b-info"}">${esc(e.level)}</span>`, esc(e.category), `<div class="sub">${esc(e.message)}</div>`]), "No events.")}</div>`;
    }
    if (tab === "exports") return `${tabs}<div class="grid three">${[["certificates", "Certificate inventory", "All server certificates with vendor, key, bindings and expiry"], ["endpoints", "Endpoints", "Monitored TLS endpoints, trust and protocol"], ["audit", "Audit trail", "Every change, sign-in and approval (last 20,000)"]]
      .map(([k, t, s]) => `<div class="card"><h3>${t}</h3><p class="card-sub">${s}</p><a class="btn primary" href="/api/reports/${k}.csv">${icon("download")} Download CSV</a></div>`).join("")}</div>`;
    const list = await api("/audit");
    return `${tabs}<div class="card">${table(["When", "Actor", "Action", "Target", "Details"], list.map(a => [fmtDate(a.at), esc(a.actor), `<code>${esc(a.action)}</code>`, esc(a.target), `<div class="sub">${esc(a.details || "")}</div>`]), "No audit entries.")}</div>`;
  };

  views.settings = async () => {
    const [s, me, sec] = await Promise.all([api("/settings"), api("/auth/me"), api("/security")]);
    state.me = me;
    const dis = can.admin() ? "" : "disabled";
    let usersHtml = "";
    if (can.admin()) {
      const [users, roles] = await Promise.all([api("/users"), api("/users/roles")]);
      state.cache.roles = roles;
      usersHtml = `<div class="card mt"><div class="card-head"><div><h3>Users & roles</h3><p class="card-sub">${roles.map(r => `<b>${r.role}</b>: ${esc(r.description)}`).join(" · ")}</p></div><button class="btn primary" data-action="user-new" type="button">${icon("plus")} Add user</button></div>
        ${table(["User", "Role", "2-step", "Last sign-in", ""], users.map(u => [`<b>${esc(u.displayName)}</b><div class="sub">${esc(u.email)}</div>${u.disabled ? `<span class="badge b-muted">Disabled</span>` : ""}${u.mustChangePassword ? ` <span class="badge b-warn">Temp password</span>` : ""}`,
          `<select data-role-user="${u.id}">${roles.map(r => `<option ${r.role === u.role ? "selected" : ""}>${r.role}</option>`).join("")}</select>`,
          u.mfaEnabled ? `<span class="badge b-ok">${u.mfaMethod === "Totp" ? "App" : "Email"}</span>` : `<span class="badge b-warn">Off</span>`,
          `${fmtDate(u.lastLoginAt)}<div class="sub">${esc(u.lastLoginIp || "")}</div>`,
          `<div class="row-actions">${u.disabled ? `<button class="btn sm" data-action="user-enable" data-id="${u.id}" type="button">Enable</button>` : `<button class="btn sm danger" data-action="user-disable" data-id="${u.id}" type="button">Disable</button>`}
            <button class="btn sm" data-action="user-reset-pw" data-id="${u.id}" type="button">Reset password</button>${u.mfaEnabled ? `<button class="btn sm" data-action="user-reset-mfa" data-id="${u.id}" type="button">Reset 2-step</button>` : ""}</div>`]), "")}</div>`;
    }
    const ai = s.ai || {};
    return { html: `<div class="card"><div class="card-head"><div><h3>${icon("shield")} Security center</h3><p class="card-sub">Live posture of this installation</p></div><div class="kpi"><div class="value">${sec.score}%</div></div></div>
        <div class="meter" id="secmeter"></div>
        <div class="grid halves mt">${[sec.checks.slice(0, Math.ceil(sec.checks.length / 2)), sec.checks.slice(Math.ceil(sec.checks.length / 2))].map(col => `<div>${col.map(c => `<div class="check-row"><span class="${c.ok ? "dot-ok" : "dot-bad"}">${c.ok ? "✓" : "!"}</span><div><b>${esc(c.name)}</b><div class="sub">${esc(c.detail)}</div></div><span></span></div>`).join("")}</div>`).join("")}</div></div>
      <div class="grid two mt">
        <div class="card"><div class="card-head"><div><h3>Organization: alerts, security & monitoring</h3><p class="card-sub">${esc(s.name)}</p></div>${s.smtpConfigured ? `<span class="badge b-info">SMTP configured</span>` : `<span class="badge b-warn">SMTP not configured</span>`}</div>
          <form class="form" id="settings-form">
            <label>Alert recipients <small>Comma-separated. Empty = Admins and Operators who opted in.</small><input name="alertEmails" value="${esc(s.alertEmails || "")}" ${dis} placeholder="pki-team@contoso.com, noc@contoso.com"></label>
            <div class="form-grid"><label>Expiry alert thresholds (days)<input name="alertDays" value="${esc(s.alertDays)}" ${dis}></label>
            <label>Session length (hours)<input name="sessionHours" type="number" min="1" max="24" value="${s.sessionHours}" ${dis}></label></div>
            <label>Domains to watch in CT logs <small>Detect certificates issued for your domains outside AutomateCert</small><input name="ctDomains" value="${esc(s.ctDomains || "")}" ${dis} placeholder="contoso.com, contoso.net"></label>
            <label>Teams / Slack webhook <small>Optional, https://</small><input name="webhookUrl" value="${esc(s.webhookUrl || "")}" ${dis}></label>
            <label class="check"><input type="checkbox" name="alertUnmanaged" ${s.alertUnmanaged ? "checked" : ""} ${dis}> Alert for manually managed certificates too</label>
            <label class="check"><input type="checkbox" name="notifyOnSuccess" ${s.notifyOnSuccess ? "checked" : ""} ${dis}> Email when renewals succeed</label>
            <label class="check"><input type="checkbox" name="weeklySummary" ${s.weeklySummary ? "checked" : ""} ${dis}> Weekly executive summary email (Mondays)</label>
            <label class="check"><input type="checkbox" name="requireMfa" ${s.requireMfa ? "checked" : ""} ${dis}> Require two-step verification for every user</label>
            <div class="error"></div>${can.admin() ? `<div class="row-actions"><button class="btn primary" type="submit">Save</button><button class="btn" type="button" data-action="test-email">Send test email</button><button class="btn" type="button" data-action="send-summary">Send summary now</button></div>` : ""}
          </form></div>
        <div class="card"><h3>My account</h3><p class="card-sub">${esc(me.email)} · ${esc(me.role)} · last sign-in ${fmtDate(me.lastLoginAt)}</p>
          <div class="grid"><div class="card-head"><div><b>Two-step verification</b><p class="hint">${me.mfaEnabled ? `On (${me.mfaMethod === "Totp" ? "authenticator app" : "email code"}) · ${me.recoveryCodesLeft} recovery codes left` : "Off — strongly recommended"}</p></div>${me.mfaEnabled ? `<span class="badge b-ok">Protected</span>` : `<span class="badge b-warn">At risk</span>`}</div>
            <div class="row-actions">${me.mfaEnabled ? `<button class="btn" type="button" data-action="mfa-codes">New recovery codes</button>${me.orgRequiresMfa ? "" : `<button class="btn danger" type="button" data-action="mfa-disable">Turn off</button>`}` : `<button class="btn primary" type="button" data-action="mfa-enable">Turn on two-step verification</button>`}</div>
            <label class="check"><input type="checkbox" id="my-notify" ${me.notifyByEmail ? "checked" : ""}> Send me alert emails</label>
            <div><button class="btn" type="button" data-action="password">Change password</button></div></div></div>
      </div>
      <div class="card mt"><div class="card-head"><div><h3>${icon("spark")} AI assistant</h3><p class="card-sub">Bring your own model. Powers Copilot, AI briefings and diagnosis. Tools are read-only; actions are proposals you approve.</p></div>${ai.enabled ? `<span class="badge b-ok">Enabled</span>` : `<span class="badge b-muted">Off</span>`}</div>
        <form class="form" id="ai-form"><div class="form-grid">
          <label>Provider<select name="provider" id="ai-provider" ${dis}><option value="AzureOpenAI" ${ai.provider === "AzureOpenAI" ? "selected" : ""}>Azure OpenAI (v1 API)</option><option value="OpenAI" ${ai.provider === "OpenAI" ? "selected" : ""}>OpenAI</option><option value="Custom" ${ai.provider === "Custom" ? "selected" : ""}>OpenAI-compatible (Ollama, vLLM, LiteLLM, on-prem)</option></select></label>
          <label>Model / deployment name<input name="model" value="${esc(ai.model || "")}" ${dis}></label>
          <label>Endpoint (base URL)<input name="endpoint" id="ai-endpoint" value="${esc(ai.endpoint || "")}" ${dis} placeholder="https://YOUR-RESOURCE.openai.azure.com/openai/v1"></label>
          <label>API key <small>${ai.hasKey ? "Stored encrypted — leave empty to keep" : "Stored encrypted"}</small><input name="apiKey" type="password" autocomplete="off" ${dis}></label></div>
          <label class="check"><input type="checkbox" name="enabled" ${ai.enabled ? "checked" : ""} ${dis}> Enable AI assistant</label>
          <div class="error"></div>${can.admin() ? `<div class="row-actions"><button class="btn primary" type="submit">Save AI settings</button><button class="btn" type="button" data-action="ai-test">Test connection</button></div>` : ""}</form></div>
      ${usersHtml}`, mount: root => $("#secmeter", root).appendChild(meter(sec.score, sec.score < 60 ? "bad" : sec.score < 85 ? "warn" : "")) };
  };

  // ======================= AUTOMATION DESIGNER =======================
  function pickIssuerFor(cert, issuers) {
    if (cert.internalOnly) return (issuers.find(i => i.type === "AdcsInternal") || issuers[0]).id;
    const v = (cert.vendor || "").toLowerCase().split(" ")[0].replace("'s", "");
    const match = issuers.find(i => i.type === "Acme" && !i.name.toLowerCase().includes("staging") && (i.name.toLowerCase().includes(v) || (i.directoryUrl || "").toLowerCase().includes(v)));
    if (match) return match.id;
    if (cert.vendor === "Internal / private CA") { const ad = issuers.find(i => i.type === "AdcsInternal"); if (ad) return ad.id; }
    return (issuers.find(i => i.isPublic && !i.name.toLowerCase().includes("staging")) || issuers[0]).id;
  }

  function profileForm(p, prefill) {
    Promise.all([api("/issuers"), api("/dns-providers"), api("/agents")]).then(([issuers, dns, agentsAll]) => {
      const agents = agentsAll.filter(a => !a.revoked);
      if (!issuers.length) { toast("Add a certificate authority first (CAs & DNS)."); location.hash = "#/issuers"; return; }
      if (!agents.length) { toast("Connect a server first (Servers)."); location.hash = "#/agents"; return; }
      const src = p || prefill || {};
      const adopting = !!(prefill && prefill.adoptCertificateId);
      const sel = (name, items, val, label, allowEmpty) => `<select name="${name}">${allowEmpty ? `<option value="">— none —</option>` : ""}${items.map(i => `<option value="${i.id}" ${i.id === val ? "selected" : ""}>${esc(label(i))}</option>`).join("")}</select>`;
      const defaultDns = src.dnsProviderId || (dns.find(d => d.type !== "Manual") || dns[0] || {}).id;
      openModal(p ? "Edit automation" : prefill?.title || (adopting ? "Adopt existing certificate" : "New automation"), `<form class="form">
        ${adopting ? `<div class="note ok"><b>Zero-downtime adoption.</b> Nothing changes now. AutomateCert tracks the current certificate (${esc(prefill.vendor)}, expires ${fmtDay(prefill.notAfter)}) and renews at the threshold below. <b>Replace everywhere</b> then swaps the new certificate into every binding that used the old one, verifies and rolls back on failure. The first renewal waits for approval.</div>` : ""}
        ${prefill?.intro ? `<div class="note brand">${prefill.intro}</div>` : ""}
        <div class="form-grid"><label>Name<input name="name" required value="${esc(src.name)}"></label><label>Server (where the certificate lives)${sel("agentId", agents, src.agentId, a => a.name + (a.roles?.length ? ` · ${a.roles.join(", ")}` : ""))}</label></div>
        <label>DNS names <small>One per line; the first is the subject. Wildcards (*.contoso.com) via DNS-01.</small><textarea name="domains" required>${esc((src.domains || []).join("\n"))}</textarea></label>
        <div class="note warn hidden" id="name-warn"></div>
        <div class="form-grid"><label>Certificate authority${sel("issuerId", issuers, src.issuerId, i => `${i.name}${i.isFree ? " — free" : ""} (${i.type === "Acme" ? (i.isPublic ? "public ACME" : "private ACME") : "AD CS"})`)}</label>
          <label>DNS provider <small>for ACME CAs</small>${sel("dnsProviderId", dns, defaultDns, d => `${d.name} (${d.typeName}${d.aliasZone ? ", CNAME" : ""})`, true)}</label>
          <label>Key algorithm<select name="keyAlgorithm">${["RSA2048", "RSA3072", "RSA4096", "ECDSAP256", "ECDSAP384"].map(k => `<option ${(src.keyAlgorithm || "RSA2048") === k ? "selected" : ""}>${k}</option>`).join("")}</select></label>
          <label>Key storage <small>CAPI only for legacy Exchange/CRM</small><select name="keyStorage">${["CNG", "CAPI"].map(k => `<option ${src.keyStorage === k ? "selected" : ""}>${k}</option>`).join("")}</select></label>
          <label>Renew when remaining lifetime ≤ (%) <small>33% ≈ 66 days before expiry on a 200-day cert</small><input name="renewAtRemainingPercent" type="number" min="5" max="80" value="${src.renewAtRemainingPercent ?? 33}"></label>
          <label>Maintenance window (UTC) <small>Disruptive steps only start inside it; empty = any time</small><span class="form-grid"><input name="windowStartHour" type="number" min="0" max="23" placeholder="from" value="${src.windowStartHour ?? ""}"><input name="windowEndHour" type="number" min="0" max="23" placeholder="to" value="${src.windowEndHour ?? ""}"></span></label></div>
        <div class="form-grid"><label class="check"><input type="checkbox" name="requireApproval" ${src.requireApproval ? "checked" : ""}> Require approval before changes</label>
          <label class="check"><input type="checkbox" name="exportable" ${src.exportable ? "checked" : ""}> Exportable private key (farms only)</label>
          <label class="check"><input type="checkbox" name="enabled" ${p ? (p.enabled ? "checked" : "") : "checked"}> Enabled</label></div>
        <input type="hidden" name="scenario" value="${esc(src.scenario || "")}">
        <p class="section-title">Deployment steps</p><p class="hint">Run in order. Steps marked “on another server” run after the renewal (or after promotion for staged AD FS tokens). Failures roll back.</p>
        <div id="targets" class="grid"></div><div><button class="btn sm" type="button" id="add-target">${icon("plus")} Add step</button></div>
        <div class="error"></div><div><button class="btn primary" type="submit">${p ? "Save changes" : adopting ? "Adopt certificate" : "Create automation"}</button></div></form>`, root => {
        const holder = $("#targets", root), warn = $("#name-warn", root);
        const checkNames = () => {
          const issuer = issuers.find(i => i.id === root.querySelector("[name=issuerId]").value);
          const names = String(root.querySelector("[name=domains]").value).split(/[\n,]/).map(x => x.trim()).filter(Boolean);
          const bad = issuer && issuer.isPublic ? names.filter(isInternalName) : [];
          warn.classList.toggle("hidden", !bad.length);
          warn.innerHTML = bad.length ? `<b>${esc(bad.join(", "))}</b> can't be issued by a public CA (internal name). Choose AD CS, or use a public DNS name you own.` : "";
        };
        root.querySelector("[name=issuerId]").addEventListener("change", checkNames);
        root.querySelector("[name=domains]").addEventListener("input", checkNames);
        checkNames();
        const addTarget = t => {
          const div = document.createElement("div"); div.className = "target";
          const groups = [...new Set(state.templates.map(x => x.category))];
          const opts = groups.map(g => `<optgroup label="${esc(g)}">${state.templates.filter(x => x.category === g).map(x => `<option value="${x.key}" ${t && t.template === x.key ? "selected" : ""}>${esc(x.displayName)}${x.disruptive ? " ⟳" : ""}</option>`).join("")}</optgroup>`).join("");
          div.innerHTML = `<div class="target-head"><select data-f="template">${opts}</select><button class="btn sm danger" type="button" data-f="remove">Remove</button></div>
            <p class="hint" data-f="desc"></p><div class="form-grid" data-f="params"></div>
            <label data-f="remote-wrap" class="hidden">Run on server <small>this step can run on another server after the renewal</small><select data-f="agent"><option value="">Same server as the certificate</option>${agents.map(a => `<option value="${a.id}" ${t && t.agentId === a.id ? "selected" : ""}>${esc(a.name)}${a.roles?.length ? " · " + esc(a.roles.join(", ")) : ""}</option>`).join("")}</select></label>
            <div class="form-grid" data-f="verify"><label>Verify host <small>TLS check after deploy</small><input data-f="verifyHost" value="${esc(t && t.verifyHost)}"></label><label>Verify port<input data-f="verifyPort" type="number" min="1" max="65535" value="${esc(t && t.verifyPort ? t.verifyPort : "")}"></label></div>`;
          const render = () => {
            const def = state.templates.find(x => x.key === $("[data-f=template]", div).value);
            $("[data-f=desc]", div).textContent = def.description;
            $("[data-f=params]", div).innerHTML = def.parameters.map(pp => {
              const val = t && t.parameters && t.parameters[pp.name] != null ? t.parameters[pp.name] : (pp.default ?? "");
              const input = pp.options ? `<select data-param="${esc(pp.name)}">${pp.options.map(o => `<option ${String(val) === o ? "selected" : ""}>${esc(o)}</option>`).join("")}</select>`
                : `<input data-param="${esc(pp.name)}" ${pp.required ? "required" : ""} value="${esc(val)}">`;
              return `<label>${esc(pp.label)}${pp.required ? "" : " <small>optional</small>"}${pp.help ? ` <small>${esc(pp.help)}</small>` : ""}${input}</label>`;
            }).join("");
            $("[data-f=verify]", div).classList.toggle("hidden", !def.supportsTlsVerification);
            $("[data-f=remote-wrap]", div).classList.toggle("hidden", def.requiresLocalCertificate);
          };
          $("[data-f=template]", div).addEventListener("change", () => { t = null; render(); });
          $("[data-f=remove]", div).addEventListener("click", () => div.remove());
          render(); holder.appendChild(div);
        };
        (src.targets || []).forEach(addTarget);
        $("#add-target", root).addEventListener("click", () => addTarget(null));
        bindForm(root, async (fd, form) => {
          const targets = $$(".target", form).map(div => {
            const parameters = {};
            $$("[data-param]", div).forEach(i => { if (String(i.value).trim()) parameters[i.dataset.param] = String(i.value).trim(); });
            const port = $("[data-f=verifyPort]", div).value, ag = $("[data-f=agent]", div).value;
            const def = state.templates.find(x => x.key === $("[data-f=template]", div).value);
            return { template: def.key, parameters, verifyHost: $("[data-f=verifyHost]", div).value.trim() || null, verifyPort: port ? Number(port) : null, agentId: !def.requiresLocalCertificate && ag ? ag : null };
          });
          const ws = fd.get("windowStartHour"), we = fd.get("windowEndHour");
          const body = {
            name: fd.get("name"), agentId: fd.get("agentId"), issuerId: fd.get("issuerId"), dnsProviderId: fd.get("dnsProviderId") || null,
            domains: String(fd.get("domains")).split(/[\n,]/).map(s => s.trim()).filter(Boolean),
            keyAlgorithm: fd.get("keyAlgorithm"), keyStorage: fd.get("keyStorage"), renewAtRemainingPercent: Number(fd.get("renewAtRemainingPercent")),
            requireApproval: fd.has("requireApproval"), exportable: fd.has("exportable"), enabled: fd.has("enabled"), targets,
            windowStartHour: ws === "" ? null : Number(ws), windowEndHour: we === "" ? null : Number(we), scenario: fd.get("scenario") || null,
            adoptCertificateId: adopting ? prefill.adoptCertificateId : null
          };
          const r = await api(p ? `/profiles/${p.id}` : "/profiles", { method: p ? "PUT" : "POST", body });
          closeModal();
          toast(p ? "Automation updated." : adopting ? `Adopted. Next renewal ${r.nextRenewal ? "on " + fmtDay(r.nextRenewal) : "soon"} — nothing changed on the server.` : "Automation created. Click “Renew now” to issue immediately, or the scheduler will within 5 minutes.");
          location.hash = "#/profiles"; route();
        });
      });
    }).catch(e => toast(e.message));
  }

  async function identityPack(kind) {
    const [agents, issuers, identity] = await Promise.all([api("/agents"), api("/issuers"), api("/identity")]);
    const live = agents.filter(a => !a.revoked);
    const has = (a, r) => (a.roles || []).includes(r);
    const adfs = live.filter(a => has(a, "ADFS")), crm = live.filter(a => has(a, "CRM")), wap = live.filter(a => has(a, "WAP"));
    const adcs = issuers.find(i => i.type === "AdcsInternal");
    const certOn = (agentId, marker) => { const s = identity.servers.find(x => x.id === agentId); return s && s.certificates.find(c => (c.bindings || "").includes(marker)); };
    const opts = (list, all) => (list.length ? list : all).map(a => `<option value="${a.id}">${esc(a.name)}${a.roles?.length ? " · " + esc(a.roles.join(", ")) : ""}</option>`).join("");
    const titles = { "pack-adfs-ssl": "AD FS SSL + service communications", "pack-crm": "Dynamics CRM claims / IFD certificate", "pack-token": "AD FS token-signing (staged rotation)" };
    openModal(titles[kind], `<form class="form"><div class="note warn"><b>Before you continue:</b> ${esc(LEGAL.change)}</div>
      ${kind === "pack-adfs-ssl" ? `<label>AD FS node<select name="adfs">${opts(adfs, live)}</select></label><div class="note">Create this pack once per farm node (and once per WAP with the same names). Each node renews in the maintenance window with approval.</div>` : ""}
      ${kind === "pack-crm" ? `<label>CRM server (front-end / full)<select name="crm">${opts(crm, live)}</select></label><label>Primary AD FS server<select name="adfs">${opts(adfs, live)}</select></label>
        <label>CRM relying party names in AD FS <small>comma-separated, exactly as in AD FS Management</small><input name="rps" value="CRM Claims, CRM IFD"></label>` : ""}
      ${kind === "pack-token" ? `<label>Primary AD FS server<select name="adfs">${opts(adfs, live)}</select></label>
        <label>Certificate type<select name="ctype"><option>Token-Signing</option><option>Token-Decrypting</option></select></label>
        <label>Promote to primary after (hours)<input name="hours" type="number" min="0" max="720" value="48"></label>
        <label>CRM servers to reload after promotion <small>hold Ctrl to select several</small><select name="crms" multiple>${opts(crm, live)}</select></label>
        <div class="note warn">AD FS must have AutoCertificateRollover turned off for CA-issued token certificates. The pack sets “Turn off AutoCertificateRollover” to <b>false</b>; change it on the step if you want AutomateCert to switch it off.</div>` : ""}
      <div class="error"></div><div><button class="btn primary" type="submit">Continue to review</button></div></form>`, root => bindForm(root, async fd => {
        const pick = (id) => live.find(a => a.id === id);
        if (kind === "pack-adfs-ssl") {
          const node = pick(fd.get("adfs")); const c = certOn(node.id, "AD FS SSL");
          profileForm(null, { title: titles[kind], scenario: "ADFS-SSL", name: `AD FS SSL – ${node.name}`, agentId: node.id, domains: c ? (c.subject.match(/CN=([^,]+)/) || [])[1] ? [cn(c.subject)] : [] : [],
            issuerId: (issuers.find(i => i.isPublic && !i.name.toLowerCase().includes("staging")) || issuers[0]).id, requireApproval: true, windowStartHour: 22, windowEndHour: 4,
            intro: "Sets the AD FS SSL and service-communications certificate on this node, grants the AD FS service account key access, restarts AD FS and verifies. Add the federation service name (e.g. sts.contoso.com) and certauth / enterpriseregistration names if you use them.",
            targets: [{ template: "adfs", verifyHost: c ? cn(c.subject) : "", verifyPort: 443 }] });
        } else if (kind === "pack-crm") {
          const crmS = pick(fd.get("crm")), adfsS = pick(fd.get("adfs")); const c = certOn(crmS.id, "IIS");
          profileForm(null, { title: titles[kind], scenario: "CRM-Claims", name: `Dynamics CRM – ${crmS.name}`, agentId: crmS.id, domains: c ? [cn(c.subject)] : [],
            issuerId: (issuers.find(i => i.isPublic && !i.name.toLowerCase().includes("staging")) || issuers[0]).id, requireApproval: true, windowStartHour: 22, windowEndHour: 4,
            intro: "1) Replace the certificate on every CRM binding, 2) give the CRM app pool key access and set it as the claims encryption certificate, iisreset, 3) on AD FS refresh the CRM relying parties from CRM's federation metadata. Include internal and IFD names (auth.*, dev.*, org URLs) or use a wildcard.",
            targets: [{ template: "rebind" }, { template: "crmclaims" }, { template: "adfsrprefresh", agentId: adfsS.id, parameters: { relyingParties: fd.get("rps") } }] });
        } else {
          const adfsS = pick(fd.get("adfs")); const crms = [...root.querySelector("[name=crms]").selectedOptions].map(o => o.value);
          profileForm(null, { title: titles[kind], scenario: "ADFS-Token", name: `AD FS ${fd.get("ctype")} – ${adfsS.name}`, agentId: adfsS.id, domains: [`adfs-${String(fd.get("ctype")).toLowerCase()}.local`],
            issuerId: (adcs || issuers[0]).id, requireApproval: true, renewAtRemainingPercent: 25,
            intro: "Token certificates are not web certificates: use your internal AD CS (recommended). Step 1 adds the new certificate as SECONDARY; after the delay it is promoted to PRIMARY (with approval) and every CRM server reloads federation metadata. Update Microsoft 365 / SaaS relying parties as described in the runbook.",
            targets: [{ template: "adfstoken", parameters: { certificateType: fd.get("ctype"), promoteAfterHours: String(fd.get("hours")), allowDisableAutoRollover: "false" } },
              ...crms.map(id => ({ template: "crmrefresh", agentId: id }))] });
        }
      }));
  }

  const CA_PRESETS = [
    { id: "le", name: "Let's Encrypt", tag: "Free · production DV", url: "https://acme-v02.api.letsencrypt.org/directory", eab: false, note: "Free, browser-trusted DV certificates for public names. No account, no EAB, no cost. Rate limits apply." },
    { id: "le-staging", name: "Let's Encrypt", tag: "STAGING · test", url: "https://acme-staging-v02.api.letsencrypt.org/directory", eab: false, note: "Free test CA (not browser-trusted). Still needs a real public domain." },
    { id: "digicert", name: "DigiCert", tag: "CertCentral · DV/OV/EV", url: "", eab: true, note: "CertCentral → Automation → ACME Directory URLs → Add. Choose product, organization and validity; copy the URL, Key ID and HMAC (shown once). Enable automatic request approval." },
    { id: "sectigo-dv", name: "Sectigo", tag: "DV", url: "https://acme.sectigo.com/v2/DV", eab: true, note: "Sectigo Certificate Manager → Enrollment → ACME → Accounts gives the EAB Key ID and HMAC. Use the endpoint SCM shows if different." },
    { id: "sectigo-ov", name: "Sectigo", tag: "OV", url: "https://acme.sectigo.com/v2/OV", eab: true, note: "OV needs a pre-validated organization in SCM." },
    { id: "globalsign", name: "GlobalSign Atlas", tag: "DV/OV", url: "https://emea.acme.atlas.globalsign.com/directory", eab: true, note: "Atlas → API credentials: API key = Key ID, MAC key = HMAC (valid 30 days or 1000 uses — create just before first issuance). Match RSA/ECC to the product." },
    { id: "zerossl", name: "ZeroSSL", tag: "DV", url: "https://acme.zerossl.com/v2/DV90", eab: true, note: "ZeroSSL dashboard → Developer → EAB credentials. Verify the URL in your dashboard." },
    { id: "google", name: "Google Trust Services", tag: "DV", url: "https://dv.acme-v02.api.pki.goog/directory", eab: true, note: "Create EAB with gcloud publicca external-account-keys. Verify the URL in Google Cloud docs." },
    { id: "custom", name: "Custom ACME", tag: "private / other", url: "", eab: true, note: "Any RFC 8555 ACME server: step-ca, EJBCA, Venafi, DigiCert TLM, Sectigo Universal ACME. EAB optional." },
    { id: "adcs", name: "Microsoft AD CS", tag: "internal PKI", url: "", eab: false, note: "Internal names (server.corp.local, SQL, RDP, AD FS tokens). The agent submits with certreq as the computer account — no ACME or DNS." }
  ];

  function dnsForm(existing) {
    const types = state.cache.dnsTypes || [];
    const agents = state.cache.agents || [];
    openModal(existing ? `Edit ${existing.name}` : "Add DNS provider", `<form class="form">
      <div class="option-grid">${types.map(t => `<button type="button" class="opt ${existing && existing.type === t.type ? "sel" : ""}" data-type="${t.type}" ${existing ? "disabled" : ""}><b>${esc(t.name)}</b><small>${t.automatic ? "automatic" : "manual"}</small></button>`).join("")}</div>
      <div class="note" id="dns-desc">Choose your DNS host. Registrar without API (Dynadot, Namecheap…)? Pick Cloudflare / Azure DNS / Route 53 for a delegated zone and set the <b>CNAME alias zone</b> below, or use a Webhook.</div>
      <input type="hidden" name="type" value="${esc(existing?.type || "")}">
      <label>Display name<input name="name" required value="${esc(existing?.name || "")}"></label>
      <div id="dns-fields" class="form"></div>
      <label>CNAME alias zone <small>optional — challenges go to _acme-challenge.&lt;name&gt;.&lt;alias zone&gt;; create one CNAME per name once</small><input name="aliasZone" value="${esc(existing?.aliasZone || "")}" placeholder="acme.contoso-dns.net"></label>
      <div class="error"></div><div><button class="btn primary" type="submit">${existing ? "Save" : "Add provider"}</button></div></form>`, root => {
      const render = type => {
        const t = types.find(x => x.type === type); if (!t) return;
        root.querySelector("[name=type]").value = type;
        $$(".opt", root).forEach(b => b.classList.toggle("sel", b.dataset.type === type));
        $("#dns-desc", root).textContent = t.description;
        if (!root.querySelector("[name=name]").value) root.querySelector("[name=name]").value = t.name;
        $("#dns-fields", root).innerHTML = t.fields.map(f => {
          const val = existing && !f.secret ? (existing.settings || {})[f.key] || "" : "";
          const input = f.kind === "agent" ? `<select name="s_${f.key}">${agents.filter(a => !a.revoked).map(a => `<option value="${a.id}" ${val === a.id ? "selected" : ""}>${esc(a.name)}${(a.roles || []).includes("DNS") ? " · DNS" : ""}</option>`).join("")}</select>`
            : f.help && f.help.includes("|") ? `<select name="s_${f.key}">${f.help.split("|").map(o => `<option ${val === o ? "selected" : ""}>${o}</option>`).join("")}</select>`
            : `<input name="s_${f.key}" ${f.secret ? `type="password" autocomplete="off" placeholder="${existing ? "stored encrypted — leave empty to keep" : ""}"` : `value="${esc(val)}" placeholder="${esc(f.placeholder || "")}"`} ${f.required && !(f.secret && existing) ? "required" : ""}>`;
          return `<label>${esc(f.label)}${f.secret ? " <small>stored encrypted</small>" : ""}${f.help && !f.help.includes("|") ? ` <small>${esc(f.help)}</small>` : ""}${input}</label>`;
        }).join("");
      };
      $$(".opt", root).forEach(b => b.addEventListener("click", () => render(b.dataset.type)));
      if (existing) render(existing.type);
      bindForm(root, async fd => {
        const type = fd.get("type"); if (!type) throw new Error("Choose a DNS provider type.");
        const settings = {}; for (const [k, v] of fd) if (k.startsWith("s_") && String(v).trim()) settings[k.slice(2)] = String(v).trim();
        const body = { name: fd.get("name"), type, settings, aliasZone: fd.get("aliasZone") || null };
        await api(existing ? `/dns-providers/${existing.id}` : "/dns-providers", { method: existing ? "PUT" : "POST", body });
        closeModal(); toast("DNS provider saved. Click Test to verify it end-to-end."); route();
      });
    });
  }

  async function connectDialog() {
    const pkg = await api("/agent-package");
    openModal("Connect Windows servers", `<div class="grid">${pkg.isLocalOnly ? `<div class="note bad"><b>This console URL is ${esc(pkg.serverUrl)}.</b> Other servers cannot reach “localhost”. Fine for an agent on this machine; for real servers use the production installer (sets the public URL) or set <code>App:PublicUrl</code>.</div>` : ""}
      <p class="section-title">1. How many servers?</p>
      <div class="segmented" id="tok-type"><button type="button" class="seg-btn on" data-v="single">One server</button><button type="button" class="seg-btn" data-v="fleet">Many servers (GPO / SCCM / Intune / RMM)</button></div>
      <div class="form-grid hidden" id="fleet-opts"><label>Max servers<input type="number" id="fleet-max" min="2" max="1000" value="50"></label><label>Valid for (days)<input type="number" id="fleet-days" min="1" max="30" value="7"></label></div>
      <div><button class="btn primary" type="button" id="gen-token">Generate install command</button></div><div id="tok-out"></div></div>`, root => {
      let type = "single";
      $$(".seg-btn", root).forEach(b => b.addEventListener("click", () => { type = b.dataset.v; $$(".seg-btn", root).forEach(x => x.classList.toggle("on", x === b)); $("#fleet-opts", root).classList.toggle("hidden", type !== "fleet"); }));
      $("#gen-token", root).addEventListener("click", async () => {
        const body = type === "fleet" ? { maxUses: Number($("#fleet-max", root).value), validDays: Number($("#fleet-days", root).value) } : { maxUses: 1, validDays: 1 };
        const t = await api("/agents/enrollment-tokens", { method: "POST", body });
        const one = `& ([scriptblock]::Create((irm '${pkg.serverUrl}/install.ps1'))) -Token '${t.token}'`;
        const silent = `AutomateCertAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /SERVERURL="${pkg.serverUrl}" /TOKEN="${t.token}"`;
        $("#tok-out", root).innerHTML = `<div class="grid"><div class="note">${t.maxUses > 1 ? `Fleet token for up to <b>${t.maxUses}</b> servers` : "One-time token for <b>one</b> server"}, valid until ${fmtDate(t.expiresAt)}. Shown only once.</div>
          <p class="section-title">2. Install</p><div class="grid halves">
          <div class="card flat"><h3>Installer (.exe)</h3><p class="card-sub">Customers and mass deployment</p>${pkg.setupAvailable ? `<p><a class="btn" href="/downloads/AutomateCertAgent-Setup.exe">${icon("download")} AutomateCertAgent-Setup.exe (${pkg.setupSizeMb} MB)</a></p><p class="hint">Run it and paste URL + token, or silently:</p>${codeBlock("cmd-silent", silent)}<p class="hint">Add <code>/PIN=1</code> for a self-signed console certificate.</p>` : `<p class="hint">Not built yet — run <code>installer\\build-installers.ps1</code>.</p>`}</div>
          <div class="card flat"><h3>PowerShell one-liner</h3><p class="card-sub">PowerShell (Run as administrator)</p>${pkg.available ? codeBlock("cmd-one", one) + `<p class="hint">Downloads ${pkg.sizeMb} MB, verifies SHA-256, installs and enrolls. Add <code>-PinServerCertificate</code> for a self-signed console.</p>` : `<p class="hint">Package not built — run <code>deploy\\build.ps1</code>.</p>`}</div></div>
          ${pkg.isLocalOnly ? `<p class="section-title">Developer machine</p>${codeBlock("cmd-repo", `.\\deploy\\agent\\Install-Agent.cmd -ServerUrl "${pkg.serverUrl}" -EnrollmentToken "${t.token}"`)}` : ""}</div>`;
        wireCopy(root); $("#gen-token", root).disabled = true;
      });
    });
  }

  // ======================= ACTIONS =======================
  const handlers = {
    "pa": (id, el) => {
      const t = el.dataset.type, i = el.dataset.id, r = el.dataset.route;
      const map = { adopt: "adopt", renew: "profile-renew", approve: "job-approve", diagnose: "job-diagnose", scan: "endpoint-scan", discover: "agent-discover" };
      if (t === "open") { location.hash = r || "#/overview"; $("#copilot").classList.add("hidden"); return; }
      return map[t] && handlers[map[t]](i);
    },
    "copilot-open": () => { $("#copilot").classList.remove("hidden"); renderChat(); $("#chat-text").focus(); },
    "identity-open": () => { location.hash = "#/identity"; },
    "pack-adfs-ssl": () => identityPack("pack-adfs-ssl"), "pack-crm": () => identityPack("pack-crm"), "pack-token": () => identityPack("pack-token"),
    "briefing": async () => {
      const box = $("#briefing"); box.innerHTML = `<div class="card mt"><p class="hint">Preparing briefing…</p></div>`;
      const r = await api("/ai/briefing", { method: "POST" });
      box.innerHTML = `<div class="card mt"><h3>${icon("spark")} Executive briefing</h3>${r.briefing ? md(r.briefing) : `<p class="hint">${r.aiConfigured ? esc(r.error || "No answer.") : "Enable the AI assistant in Settings to get a written briefing. The recommendations below work without AI."}</p>`}</div>`;
    },
    "profile-new": () => profileForm(null),
    "profile-edit": id => profileForm(state.cache.profiles.find(p => p.id === id)),
    "free-cert": async () => {
      const issuers = await api("/issuers");
      const le = issuers.find(i => i.isFree && !i.name.toLowerCase().includes("staging"));
      profileForm(null, { title: "Free certificate (Let's Encrypt)", intro: "Free, browser-trusted certificates for public names. Renewed automatically every ~60 days — ideal for 100- and 47-day lifetimes. Needs an automatic DNS provider (or manual DNS each time).", issuerId: le?.id, targets: [{ template: "iis", parameters: { siteName: "Default Web Site" } }] });
    },
    "adopt": async id => {
      const c = (state.cache.certList || []).find(x => x.id === id) || (await api("/certificates")).find(x => x.id === id);
      if (!c) return;
      const issuers = await api("/issuers");
      const names = (c.dnsNames || "").split(",").map(s => s.trim()).filter(Boolean);
      const domains = names.length ? names : cn(c.subject) ? [cn(c.subject)] : [];
      closeModal();
      profileForm(null, { adoptCertificateId: c.id, vendor: c.vendor, notAfter: c.notAfter, name: domains[0] || "Adopted certificate", domains, agentId: c.agentId,
        issuerId: pickIssuerFor(c, issuers), requireApproval: true, targets: [{ template: "rebind" }] });
    },
    "cert-details": id => { const c = (state.cache.certList || []).find(x => x.id === id); if (c) certDetails(c); },
    "profile-renew": async id => { await api(`/profiles/${id}/renew`, { method: "POST" }); toast("Renewal queued. Follow it under Jobs."); route(); },
    "profile-delete": async id => { if (confirm("Delete this automation? Installed certificates are not removed.")) { await api(`/profiles/${id}`, { method: "DELETE" }); route(); } },
    "job-approve": async id => { await api(`/jobs/${id}/approve`, { method: "POST" }); toast("Approved."); route(); },
    "job-reject": async id => { if (confirm("Reject this change?")) { await api(`/jobs/${id}/reject`, { method: "POST" }); route(); } },
    "job-dns": async id => { await api(`/jobs/${id}/dns-ready`, { method: "POST" }); toast("Validation will continue shortly."); route(); },
    "job-run-now": async id => { await api(`/jobs/${id}/run-now`, { method: "POST" }); toast("Will start within 5 minutes."); route(); },
    "job-cancel": async id => { if (confirm("Cancel this job?")) { await api(`/jobs/${id}/cancel`, { method: "POST" }); route(); } },
    "job-log": async id => { const j = await api(`/jobs/${id}`); openModal(`${j.type}${j.phase ? " · " + j.phase : ""} — ${j.status}`, `<p class="hint">${esc(j.message || "")}</p><pre class="log">${esc(j.log)}</pre>`); },
    "job-diagnose": async id => {
      openModal("Diagnosis", `<p class="hint">Analyzing the job log…</p>`);
      const [r, j] = await Promise.all([api(`/ai/diagnose/${id}`, { method: "POST" }), api(`/jobs/${id}`)]);
      const findings = r.findings.map(f => `<div class="finding"><b>${esc(f.title)}</b><span>${esc(f.cause)}</span><span><b>Fix:</b> ${esc(f.fix)}</span></div>`).join("");
      const aiPart = r.ai ? `<div class="card"><h3>${icon("spark")} AI analysis</h3>${md(r.ai)}</div>` : r.aiError ? `<div class="note warn">AI provider error: ${esc(r.aiError)}</div>` : `<div class="note">Built-in diagnostics only. Enable the AI assistant in Settings for root-cause analysis.</div>`;
      openModal("Diagnosis", `<div class="grid"><p class="section-title">Known issues detected</p>${findings || `<p class="hint">No known signature matched.</p>`}${aiPart}<details><summary>Job log</summary><pre class="log mt">${esc(j.log)}</pre></details></div>`);
    },
    "agent-discover": async id => { await api(`/agents/${id}/discover`, { method: "POST" }); toast("Discovery queued — results in about a minute."); },
    "agent-revoke": async id => { if (confirm("Revoke this server? Its agent will no longer be able to connect.")) { await api(`/agents/${id}/revoke`, { method: "POST" }); route(); } },
    "agent-enroll": () => connectDialog(),
    "endpoint-new": () => openModal("Monitor TLS endpoints", `<form class="form"><label>Endpoints <small>One per line: host or host:port (default 443). Up to 1,500.</small><textarea name="lines" required placeholder="www.contoso.com&#10;vpn.contoso.com:4443&#10;10.0.0.15:8443"></textarea></label>
        <label>Label <small>optional, e.g. "F5 production"</small><input name="label" maxlength="200"></label><div class="error"></div><div><button class="btn primary" type="submit">Start monitoring</button></div></form>`, root =>
      bindForm(root, async fd => { const r = await api("/endpoints", { method: "POST", body: { lines: fd.get("lines"), label: fd.get("label") } }); closeModal(); toast(`${r.added} endpoint(s) added — first scan within a minute.${r.invalid.length ? " Skipped: " + r.invalid.join(", ") : ""}`); route(); })),
    "endpoint-discover": () => openModal("Discover TLS services on a network", `<form class="form"><div class="note">Scans an IPv4 range from the AutomateCert server (TCP connect + TLS handshake only). Maximum /22 per scan. ${esc(LEGAL.scan)}</div>
        <div class="form-grid"><label>Range (CIDR)<input name="cidr" required placeholder="10.0.0.0/24"></label><label>Ports<input name="ports" value="443,8443,636,3389"></label></div>
        <label>Label<input name="label" placeholder="Datacenter A"></label><div class="error"></div><div><button class="btn primary" type="submit">Scan</button></div></form>`, root =>
      bindForm(root, async fd => { toast("Scanning… this can take up to a minute."); const r = await api("/endpoints/discover", { method: "POST", body: Object.fromEntries(fd) }); closeModal(); toast(`Found ${r.found} TLS service(s); ${r.added} new endpoint(s) added.`); location.hash = "#/endpoints"; route(); })),
    "endpoint-scan": async id => { const r = await api(`/endpoints/${id}/scan`, { method: "POST" }); toast(r.lastError ? "Scan failed: " + r.lastError : "Scanned."); route(); },
    "endpoint-delete": async id => { if (confirm("Stop monitoring this endpoint?")) { await api(`/endpoints/${id}`, { method: "DELETE" }); route(); } },
    "ct-sync": async () => { toast("Querying Certificate Transparency logs…"); const r = await api("/transparency/sync", { method: "POST" }); toast(`${r.added} new certificate(s) found.`); route(); },
    "ct-ack": async id => { await api(`/transparency/${id}/ack`, { method: "POST" }); route(); },
    "issuer-new": () => openModal("Add certificate authority", `<form class="form">
        <div class="option-grid">${CA_PRESETS.map(p => `<button type="button" class="opt" data-ca="${p.id}"><b>${esc(p.name)}</b><small>${esc(p.tag)}</small></button>`).join("")}</div>
        <div class="note" id="ca-note">Choose a certificate authority.</div><input type="hidden" name="type" value="Acme">
        <label>Display name<input name="name" required></label>
        <div id="acme-fields" class="form"><label>ACME directory URL<input name="directoryUrl"></label><label>Contact email<input name="contactEmail" type="email"></label>
          <div class="form-grid" id="eab-fields"><label>EAB Key ID<input name="eabKeyId" autocomplete="off"></label><label>EAB HMAC key <small>stored encrypted</small><input name="eabHmacKey" type="password" autocomplete="off"></label></div>
          <label id="cov-field">Subscription / credential coverage ends <small>alerts at 60, 30 and 7 days</small><input name="coverageEndsAt" type="date"></label></div>
        <div id="adcs-fields" class="form hidden"><label>CA config <small>certutil -config - -ping</small><input name="adcsConfig" placeholder="CA01.corp.local\\Corp Issuing CA"></label><label>Template name<input name="adcsTemplate" value="WebServer"></label></div>
        <label class="check"><input type="checkbox" name="accept" required> I accept the certificate authority's subscriber agreement and terms for the certificates AutomateCert requests on our behalf.</label>
        <p class="hint">${esc(LEGAL.ca)}</p>
        <div class="error"></div><div><button class="btn primary" type="submit">Add CA</button></div></form>`, root => {
      const pick = id => {
        const p = CA_PRESETS.find(x => x.id === id), adcs = id === "adcs";
        $$(".opt", root).forEach(b => b.classList.toggle("sel", b.dataset.ca === id));
        $("#ca-note", root).textContent = p.note;
        root.querySelector("[name=type]").value = adcs ? "AdcsInternal" : "Acme";
        $("#acme-fields", root).classList.toggle("hidden", adcs); $("#adcs-fields", root).classList.toggle("hidden", !adcs);
        $("#eab-fields", root).classList.toggle("hidden", !p.eab); $("#cov-field", root).classList.toggle("hidden", id.startsWith("le"));
        root.querySelector("[name=directoryUrl]").value = p.url;
        root.querySelector("[name=name]").value = adcs ? "Internal AD CS" : `${p.name} ${p.tag.split(" ·")[0]}`.trim();
      };
      $$(".opt", root).forEach(b => b.addEventListener("click", () => pick(b.dataset.ca))); pick("digicert");
      bindForm(root, async fd => { const body = Object.fromEntries(fd); delete body.accept; body.coverageEndsAt = body.coverageEndsAt ? new Date(body.coverageEndsAt).toISOString() : null; await api("/issuers", { method: "POST", body }); closeModal(); toast("Certificate authority added."); route(); });
    }),
    "issuer-coverage": async id => {
      const i = (await api("/issuers")).find(x => x.id === id);
      openModal(`Coverage — ${i.name}`, `<form class="form"><p class="hint">When the paid subscription or ACME credential ends. AutomateCert alerts at 60, 30 and 7 days and Autopilot suggests moving automations to another CA.</p><label>Coverage ends<input name="d" type="date" value="${i.coverageEndsAt ? i.coverageEndsAt.slice(0, 10) : ""}"></label><div class="error"></div><div><button class="btn primary" type="submit">Save</button></div></form>`, root =>
        bindForm(root, async fd => { await api(`/issuers/${id}`, { method: "PUT", body: { name: i.name, coverageEndsAt: fd.get("d") ? new Date(fd.get("d")).toISOString() : null, contactEmail: i.contactEmail } }); closeModal(); route(); }));
    },
    "issuer-delete": async id => { if (confirm("Delete this CA?")) { await api(`/issuers/${id}`, { method: "DELETE" }); route(); } },
    "dns-new": async () => { if (!state.cache.agents) state.cache.agents = await api("/agents"); dnsForm(null); },
    "dns-edit": async id => { if (!state.cache.agents) state.cache.agents = await api("/agents"); dnsForm(state.cache.dns.find(d => d.id === id)); },
    "dns-delete": async id => { if (confirm("Delete this DNS provider?")) { await api(`/dns-providers/${id}`, { method: "DELETE" }); route(); } },
    "dns-test": id => openModal("Test DNS provider", `<form class="form"><p class="hint">Creates a temporary TXT record <code>_automatecert-test.&lt;domain&gt;</code>, checks public DNS, then deletes it. Takes up to 2 minutes.</p><label>A domain hosted by this provider<input name="domain" required placeholder="contoso.com"></label><div class="error"></div><div><button class="btn primary" type="submit">Run test</button></div><div id="test-out"></div></form>`, root =>
      bindForm(root, async fd => { $("#test-out", root).innerHTML = `<p class="hint">Testing…</p>`; const r = await api(`/dns-providers/${id}/test`, { method: "POST", body: { domain: fd.get("domain") } }); $("#test-out", root).innerHTML = `<div class="note ${r.ok ? "ok" : "warn"}">${r.steps.map(esc).join("<br>")}</div>`; })),
    "dns-cnames": id => openModal("CNAME delegation records", `<form class="form"><p class="hint">Create these CNAMEs once at your registrar / DNS host (e.g. Dynadot). After that every renewal is automatic.</p><label>Names (comma-separated)<input name="domains" required placeholder="www.contoso.com, contoso.com, *.contoso.com"></label><div class="error"></div><div><button class="btn primary" type="submit">Show and check</button></div><div id="cn-out"></div></form>`, root =>
      bindForm(root, async fd => { const r = await api(`/dns-providers/${id}/cnames?domains=${encodeURIComponent(fd.get("domains"))}`); $("#cn-out", root).innerHTML = table(["Create CNAME", "Pointing to", "Status"], r.map(x => [`<code>${esc(x.name)}</code>`, `<code>${esc(x.target)}</code>`, x.ok ? `<span class="badge b-ok">Found</span>` : `<span class="badge b-warn">Missing</span>`]), ""); })),
    "ca-account-new": async () => {
      const types = await api("/ca-account-types");
      openModal("Connect a CA account", `<form class="form"><div class="option-grid">${types.map(t => `<button type="button" class="opt" data-t="${t.type}"><b>${esc(t.name)}</b><small>${esc(t.description)}</small></button>`).join("")}</div>
        <input type="hidden" name="type"><label>Display name<input name="name" required></label><div id="ca-fields" class="form"></div>
        <label>Alert when available funds fall below <small>optional</small><input name="low" type="number" min="0" step="1"></label>
        <div class="error"></div><div><button class="btn primary" type="submit">Connect and sync</button></div></form>`, root => {
        const pick = type => {
          const t = types.find(x => x.type === type); root.querySelector("[name=type]").value = type;
          $$(".opt", root).forEach(b => b.classList.toggle("sel", b.dataset.t === type));
          if (!root.querySelector("[name=name]").value) root.querySelector("[name=name]").value = t.name;
          $("#ca-fields", root).innerHTML = t.fields.map(f => `<label>${esc(f.label)}${f.help ? ` <small>${esc(f.help)}</small>` : ""}<input name="s_${f.key}" ${f.secret ? 'type="password" autocomplete="off"' : ""} placeholder="${esc(f.placeholder || "")}" ${f.required ? "required" : ""}></label>`).join("");
        };
        $$(".opt", root).forEach(b => b.addEventListener("click", () => pick(b.dataset.t))); pick(types[0].type);
        bindForm(root, async fd => {
          const settings = {}; for (const [k, v] of fd) if (k.startsWith("s_") && String(v).trim()) settings[k.slice(2)] = String(v).trim();
          const r = await api("/ca-accounts", { method: "POST", body: { name: fd.get("name"), type: fd.get("type"), settings, lowBalanceThreshold: fd.get("low") ? Number(fd.get("low")) : null } });
          closeModal(); toast(r.lastError ? "Connected, but sync failed: " + r.lastError : "Connected and synced."); route();
        });
      });
    },
    "ca-sync": async id => { const r = await api(`/ca-accounts/${id}/sync`, { method: "POST" }); toast(r.lastError ? "Sync failed: " + r.lastError : "Synced."); route(); },
    "ca-account-delete": async id => { if (confirm("Remove this CA account connection?")) { await api(`/ca-accounts/${id}`, { method: "DELETE" }); route(); } },
    "user-new": () => {
      const roles = state.cache.roles || [];
      openModal("Add user", `<form class="form"><div class="form-grid"><label>Name<input name="displayName" required></label><label>Email<input name="email" type="email" required></label></div>
        <label>Role</label><div class="option-grid">${roles.map((r, i) => `<label class="opt ${i === roles.length - 1 ? "sel" : ""}"><span class="check"><input type="radio" name="role" value="${r.role}" ${i === roles.length - 1 ? "checked" : ""}> <b>${r.role}</b></span><small>${esc(r.description)}</small></label>`).join("")}</div>
        <label class="check"><input type="checkbox" name="sendWelcome" checked> Email an invitation (the password is never emailed)</label>
        <div class="error"></div><div><button class="btn primary" type="submit">Create user</button></div></form>`, root => {
        $$(".opt input", root).forEach(i => i.addEventListener("change", () => $$(".opt", root).forEach(o => o.classList.toggle("sel", $("input", o).checked))));
        bindForm(root, async fd => {
          const r = await api("/users", { method: "POST", body: { displayName: fd.get("displayName"), email: fd.get("email"), role: fd.get("role"), sendWelcome: fd.has("sendWelcome") } });
          $("#modal-body").innerHTML = `<div class="grid"><div class="note ok">User created${r.invitationEmailed ? " and invitation emailed" : ""}. Share this temporary password over a separate channel (Teams, phone). The user must change it at first sign-in.</div>${codeBlock("tmp-pw", r.temporaryPassword)}</div>`;
          wireCopy($("#modal-body")); route();
        });
      });
    },
    "user-reset-pw": async id => { if (!confirm("Generate a new temporary password for this user?")) return; const r = await api(`/users/${id}/reset-password`, { method: "POST" }); openModal("Temporary password", `<div class="grid"><div class="note">Share it over a separate channel. The user must change it at sign-in.</div>${codeBlock("tmp-pw2", r.temporaryPassword)}</div>`, wireCopy); route(); },
    "user-disable": async id => { if (confirm("Disable this user?")) { await api(`/users/${id}/disable`, { method: "POST" }); route(); } },
    "user-enable": async id => { await api(`/users/${id}/enable`, { method: "POST" }); route(); },
    "user-reset-mfa": async id => { if (confirm("Reset two-step verification for this user?")) { await api(`/users/${id}/reset-mfa`, { method: "POST" }); toast("Two-step verification reset."); route(); } },
    "mfa-enable": () => openModal("Turn on two-step verification", `<div id="enroll"></div>`, root => mfaEnroll($("#enroll", root), () => { closeModal(); route(); })),
    "mfa-codes": () => openModal("New recovery codes", `<form class="form"><p class="hint">This replaces all existing recovery codes.</p><label>Confirm your password<input name="password" type="password" required autocomplete="current-password"></label><div class="error"></div><div><button class="btn primary" type="submit">Generate</button></div></form>`, root =>
      bindForm(root, async fd => {
        const r = await api("/me/mfa/recovery-codes", { method: "POST", body: { password: fd.get("password") } });
        $("#modal-body").innerHTML = `<div class="grid"><div class="codes">${r.recoveryCodes.map(c => `<code>${esc(c)}</code>`).join("")}</div><div class="row-actions"><button class="btn" type="button" id="rc-copy">Copy</button><button class="btn primary" type="button" id="rc-dl">Download</button></div></div>`;
        $("#rc-copy").addEventListener("click", () => copy(r.recoveryCodes.join("\n")));
        $("#rc-dl").addEventListener("click", () => download("automatecert-recovery-codes.txt", "AutomateCert recovery codes\n\n" + r.recoveryCodes.join("\n")));
      })),
    "mfa-disable": () => openModal("Turn off two-step verification", `<form class="form"><div class="note warn">Your account will be protected by password only.</div><label>Confirm your password<input name="password" type="password" required autocomplete="current-password"></label><div class="error"></div><div><button class="btn danger" type="submit">Turn off</button></div></form>`, root =>
      bindForm(root, async fd => { await api("/me/mfa/disable", { method: "POST", body: { password: fd.get("password") } }); closeModal(); toast("Two-step verification turned off."); route(); })),
    "password": () => openModal("Change password", `<form class="form"><label>Current password<input name="currentPassword" type="password" required autocomplete="current-password"></label><label>New password <small>12+ characters</small><input name="newPassword" type="password" minlength="12" required autocomplete="new-password" data-strength="1"></label><div class="pw-meter"></div><div class="error"></div><div><button class="btn primary" type="submit">Change password</button></div></form>`, root => {
      wireStrength(root); bindForm(root, async fd => { await api("/me/password", { method: "POST", body: Object.fromEntries(fd) }); closeModal(); toast("Password changed."); });
    }),
    "send-summary": async () => { toast("Sending summary…"); await api("/reports/send-summary", { method: "POST" }); toast("Executive summary sent to the alert recipients."); },
    "test-email": async () => { toast("Sending…"); const r = await api("/settings/test-email", { method: "POST" }); toast(`Test email sent to ${r.sentTo.join(", ")}.`); },
    "ai-test": async () => { toast("Testing AI connection…"); const r = await api("/ai/test", { method: "POST" }); toast("AI replied: " + r.answer); }
  };

  document.addEventListener("click", async e => {
    const target = e.target.closest("[data-action]"); if (!target) return;
    const h = handlers[target.dataset.action]; if (!h) return;
    e.preventDefault();
    target.disabled = true;
    try { await h(target.dataset.id, target); } catch (ex) { toast(ex.message); } finally { target.disabled = false; }
  });

  document.addEventListener("change", async e => {
    if (e.target.id === "my-notify") {
      try { await api("/me/notifications", { method: "POST", body: { enabled: e.target.checked } }); toast("Saved."); } catch (ex) { toast(ex.message); }
    } else if (e.target.dataset.roleUser) {
      try { await api(`/users/${e.target.dataset.roleUser}/role`, { method: "PUT", body: { role: e.target.value } }); toast("Role updated."); } catch (ex) { toast(ex.message); route(); }
    } else if (e.target.id === "ai-provider") {
      const ep = $("#ai-endpoint"), defaults = { AzureOpenAI: "https://YOUR-RESOURCE.openai.azure.com/openai/v1", OpenAI: "https://api.openai.com/v1", Custom: "http://localhost:11434/v1" };
      ep.placeholder = defaults[e.target.value];
      if (!ep.value || Object.values(defaults).includes(ep.value)) ep.value = e.target.value === "OpenAI" ? defaults.OpenAI : "";
    }
  });

  document.addEventListener("submit", async e => {
    const id = e.target.id;
    if (id === "cert-filter" || id === "job-filter") {
      e.preventDefault();
      const p = new URLSearchParams(); for (const [k, v] of new FormData(e.target)) if (v) p.set(k, v);
      location.hash = `#/${id === "cert-filter" ? "certificates" : "jobs"}${p.toString() ? "?" + p : ""}`;
    } else if (id === "settings-form" || id === "ai-form") {
      e.preventDefault();
      const f = e.target, err = $(".error", f); err.textContent = "";
      const fd = new FormData(f);
      try {
        if (id === "settings-form")
          await api("/settings", { method: "PUT", body: { webhookUrl: fd.get("webhookUrl"), alertEmails: fd.get("alertEmails"), alertDays: fd.get("alertDays"), alertUnmanaged: fd.has("alertUnmanaged"),
            notifyOnSuccess: fd.has("notifyOnSuccess"), requireMfa: fd.has("requireMfa"), sessionHours: Number(fd.get("sessionHours")), ctDomains: fd.get("ctDomains"), weeklySummary: fd.has("weeklySummary") } });
        else await api("/settings/ai", { method: "PUT", body: { enabled: fd.has("enabled"), provider: fd.get("provider"), endpoint: fd.get("endpoint"), model: fd.get("model"), apiKey: fd.get("apiKey") } });
        toast("Saved."); route();
      } catch (ex) { err.textContent = ex.message; }
    }
  });

  // ======================= COPILOT =======================
  const SUGGESTIONS = ["What expires in the next 30 days?", "What should I fix first?", "Which certificates are from DigiCert vs Sectigo?", "Is our AD FS token-signing healthy?", "Why did my last renewal fail?"];
  function renderChat() {
    const c = $("#chat");
    c.innerHTML = !state.chat.length ? `<div class="msg bot">${md("Hi! I work over your **live** certificate data — expiries, vendors, CA balances, AD FS and CRM, endpoints and failures. I can **propose** actions; you decide.")}</div>`
      + `<div class="note mini">${esc(LEGAL.ai)}</div>`
      : state.chat.map(m => m.role === "user" ? `<div class="msg user">${esc(m.content)}</div>` : `<div class="msg bot ${m.error ? "err" : ""}">${md(m.content)}${(m.actions || []).length ? `<div class="row-actions">${m.actions.map(a => paBtn(a)).join("")}</div>` : ""}</div>`).join("");
    c.scrollTop = c.scrollHeight;
    $("#chips").innerHTML = state.chat.length ? "" : SUGGESTIONS.map(s => `<button type="button" class="chip">${esc(s)}</button>`).join("");
  }
  async function ask(text) {
    if (!text.trim()) return;
    state.chat.push({ role: "user", content: text.trim() }, { role: "assistant", content: "Thinking…", pending: true });
    renderChat();
    try {
      const r = await api("/ai/chat", { method: "POST", body: { messages: state.chat.filter(m => !m.pending && !m.error).map(m => ({ role: m.role, content: m.content })) } });
      state.chat = state.chat.filter(m => !m.pending); state.chat.push({ role: "assistant", content: r.answer || "(no answer)", actions: r.actions || [] });
    } catch (ex) { state.chat = state.chat.filter(m => !m.pending); state.chat.push({ role: "assistant", content: ex.message, error: true }); }
    renderChat();
  }
  $("#copilot-fab").addEventListener("click", () => { $("#copilot").classList.toggle("hidden"); renderChat(); $("#chat-text").focus(); });
  $("#copilot-close").addEventListener("click", () => $("#copilot").classList.add("hidden"));
  $("#chips").addEventListener("click", e => { const b = e.target.closest(".chip"); if (b) ask(b.textContent); });
  $("#chat-form").addEventListener("submit", e => { e.preventDefault(); const i = $("#chat-text"); const v = i.value; i.value = ""; ask(v); });

  // ======================= ROUTING =======================
  // Fast navigation: show the last copy of a page (or a skeleton) immediately, then fresh data.
  // Out-of-order responses from quick clicks are ignored.
  const viewCache = new Map();
  let routeSeq = 0, badgeAt = 0;
  const skeleton = () => `<div class="grid kpis">${'<div class="card skel"></div>'.repeat(4)}</div><div class="card skel tall mt"></div>`;
  async function route() {
    const seq = ++routeSeq;
    const [path, query] = (location.hash.replace(/^#\//, "") || "overview").split("?");
    const name = views[path] ? path : "overview";
    const key = location.hash || "#/overview";
    $$("#nav a, #tabbar a").forEach(a => a.classList.toggle(a.closest("#tabbar") ? "on" : "active", a.dataset.view === name));
    $("#title").textContent = TITLES[name] || "Overview";
    $("#subtitle").textContent = SUBTITLES[name] || "";
    document.title = `${TITLES[name] || "AutomateCert"} · AutomateCert`;
    closeSidebar();
    const view = $("#view");
    const cached = viewCache.get(key);
    if (cached) { view.innerHTML = cached.html; actions(cached.actions); view.classList.add("refreshing"); }
    else { actions(""); view.innerHTML = skeleton(); }
    try {
      const r = await views[name](new URLSearchParams(query || ""));
      if (seq !== routeSeq) return;
      if (typeof r === "string") view.innerHTML = r; else { view.innerHTML = r.html; r.mount(view); }
      viewCache.set(key, { html: view.innerHTML, actions: $("#actions").innerHTML });
      if (viewCache.size > 30) viewCache.delete(viewCache.keys().next().value);
      if (!cached) { view.style.animation = "none"; void view.offsetWidth; view.style.animation = ""; }
    } catch (ex) { if (seq === routeSeq) view.innerHTML = `<div class="card"><div class="note bad">${esc(ex.message)}</div></div>`; }
    finally { if (seq === routeSeq) view.classList.remove("refreshing"); }
    if (Date.now() - badgeAt > 30000) { badgeAt = Date.now(); refreshBadge(); }
  }
  async function refreshBadge() {
    try { const pending = await api("/jobs?status=AwaitingApproval"); const b = $("#nav-pending"); if (b) { b.textContent = pending.length; b.classList.toggle("hidden", !pending.length); } } catch { /* ignore */ }
  }

  async function boot() {
    try { state.me = await api("/auth/me", { noAuthRedirect: true }); } catch { showAuth(); return; }
    if (state.me.passwordChangeRequired) { showGate("password"); return; }
    if (state.me.mfaSetupRequired) { showGate("mfa"); return; }
    const templatesReady = api("/templates").then(t => { state.templates = t; }).catch(() => { state.templates = []; });
    $("#auth").classList.add("hidden"); $("#gate").classList.add("hidden"); $("#shell").classList.remove("hidden"); $("#copilot-fab").classList.remove("hidden");
    renderNav();
    const initials = (state.me.displayName || state.me.email).split(/[\s@.]+/).filter(Boolean).slice(0, 2).map(s => s[0].toUpperCase()).join("");
    $("#me").innerHTML = `<div class="who"><span class="avatar">${esc(initials)}</span><div><b>${esc(state.me.displayName)}</b>${esc(state.me.tenant)} · ${esc(state.me.role)}</div></div>
      ${state.me.mfaEnabled ? "" : `<a class="warn-link" href="#/settings">⚠ Turn on two-step verification</a>`}
      <div class="row-actions"><button class="btn sm" id="logout" type="button">Sign out</button></div>`;
    $("#logout").addEventListener("click", async () => { await api("/auth/logout", { method: "POST" }).catch(() => {}); state.chat = []; state.cache = {}; viewCache.clear(); showAuth(); });
    route();
    await templatesReady;
  }

  window.addEventListener("hashchange", route);
  boot();
  setInterval(() => {
    const visible = !$("#shell").classList.contains("hidden") && $("#modal").classList.contains("hidden") && $("#palette").classList.contains("hidden");
    if (visible && ["", "#/overview", "#/jobs", "#/endpoints", "#/autopilot"].includes(location.hash.split("?")[0])) route();
  }, 30000);
})();
