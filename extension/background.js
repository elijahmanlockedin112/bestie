// Bestie bridge: lets the Bestie desktop app (the little creature on your screen) see your
// tabs and do small things with them when you ask it to. It only ever talks to the app on
// your own computer (127.0.0.1). No libraries, no build step.
//
// The app is the server. We say hello, push tab snapshots when they change, and long-poll
// for commands, posting each answer back. MV3 service workers nap after ~30 s of quiet, so a
// 30 s alarm wakes us back up, and we call a cheap extension API between polls to stay awake.

const APP = "http://127.0.0.1:47823";
const HEARTBEAT = "bestie-heartbeat";
const POLL_TIMEOUT_MS = 25000;   // the app holds a poll for ~20 s
const MIN_BACKOFF_MS = 2000;
const MAX_BACKOFF_MS = 30000;

// ---- AI chat tabs ------------------------------------------------------------------------
// Which sites count as AI chats, and which paths are an actual conversation (vs the home /
// new-chat page). Project- and GPT-scoped conversations are included.
const AI_SITES = [
  { ai: "claude", hosts: ["claude.ai"], convo: /^\/(?:project\/[^/]+\/)?chat\/[^/]+/ },
  { ai: "chatgpt", hosts: ["chatgpt.com", "chat.openai.com"], convo: /^\/(?:g\/[^/]+\/)?c\/[^/]+/ },
];

// "Busy" = the page shows a visible stop button for the reply being written. These sites
// change their markup now and then: when busy detection breaks, update these lists first.
// Any visible element matching any selector for that site means busy.
const BUSY_SELECTORS = {
  claude: [
    'button[aria-label^="stop" i]',           // "Stop response"
    '[role="button"][aria-label^="stop" i]',
    '[data-testid="stop-button"]',
  ],
  chatgpt: [
    '[data-testid="stop-button"]',            // the composer's stop button
    'button[aria-label^="stop" i]',           // "Stop streaming" / "Stop generating"
    '[role="button"][aria-label^="stop" i]',
  ],
};
// Stop buttons that aren't about a reply (voice mode, dictation, read aloud, screen sharing).
// Matched against the element's aria-label + data-testid.
const NOT_A_REPLY = /dictat|record|voice|listen|read ?aloud|speak|audio|shar|video|camera|screen/i;

const BUSY_SCAN_MS = 3000;       // scan AI tabs this often while something was busy recently
const IDLE_SCAN_MS = 10000;      // ...and this often once nothing has been busy for a while
const IDLE_AFTER_MS = 60000;
const SCRIPT_TIMEOUT_MS = 2000;  // a tab that doesn't answer in time counts as not busy

let looping = false;             // only one poll loop per service worker
let saidHello = false;
let backoff = MIN_BACKOFF_MS;
let pushTimer = null;
let lastStatus = "";
let busyById = new Map();        // tabId -> busy, AI tabs only, from the latest scan
let scanTimer = null;
let scanDueAt = 0;
let lastScanAt = 0;
let scanning = false;
let rescan = false;
let lastBusyAt = 0;

// ---------------------------------------------------------------- talking to the app

// Always POST: Chrome leaves the Origin header off GET requests to hosts we have permission
// for, and the app refuses anything without our Origin.
async function send(path, body, timeoutMs = 10000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(APP + path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body || {}),
      cache: "no-store",
      signal: ctrl.signal,
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      const err = new Error(data.error || "the app said " + res.status);
      err.status = res.status;
      throw err;
    }
    return data;
  } finally {
    clearTimeout(timer);
  }
}

async function hello() {
  await send("/hello", { browser: browserName(), extensionVersion: chrome.runtime.getManifest().version });
  saidHello = true;
  await setStatus(true);
  schedulePush(0);
  startScanner();
}

// The popup reads this to say "connected" or not.
async function setStatus(connected, problem = null) {
  const key = connected + "|" + problem;
  if (key === lastStatus) return;
  lastStatus = key;
  try { await chrome.storage.session.set({ status: { connected, problem, at: Date.now() } }); } catch (e) {}
}

async function loop() {
  if (looping) return;
  looping = true;
  try {
    for (;;) {
      try {
        if (!saidHello) await hello();
        const data = await send("/poll", {}, POLL_TIMEOUT_MS);
        backoff = MIN_BACKOFF_MS;
        await setStatus(true);
        for (const cmd of data.commands || []) run(cmd);   // answers go back on their own
        await chrome.runtime.getPlatformInfo();             // any extension API call resets the idle timer
      } catch (e) {
        saidHello = false;
        await setStatus(false, e.status === 403 ? "paired-elsewhere" : "not-running");
        await nap(backoff);
        backoff = Math.min(backoff * 2, MAX_BACKOFF_MS);
      }
    }
  } finally {
    looping = false;
  }
}

async function run(cmd) {
  let answer;
  try {
    if (!Object.hasOwn(COMMANDS, cmd.type)) throw new Error("i don't know how to \"" + cmd.type + "\" yet");
    answer = { id: cmd.id, ok: true, result: await COMMANDS[cmd.type](cmd.args || {}) };
  } catch (e) {
    answer = { id: cmd.id, ok: false, error: String((e && e.message) || e) };
  }
  try { await send("/result", answer, 15000); } catch (e) { /* the app left; nothing to do */ }
}

// ---------------------------------------------------------------- tabs

function schedulePush(delay = 400) {
  clearTimeout(pushTimer);
  pushTimer = setTimeout(pushTabs, delay);
}

async function pushTabs() {
  try { await send("/tabs", { tabs: await snapshot() }); }
  catch (e) { /* app asleep; the next hello pushes again */ }
}

async function snapshot() {
  const [tabs, focused] = await Promise.all([
    chrome.tabs.query({}),
    chrome.windows.getLastFocused().catch(() => null),
  ]);
  const focusedId = focused ? focused.id : -1;
  return tabs.filter((t) => !t.incognito).map((t) => {
    const kind = classify(t.url || t.pendingUrl);
    return {
      id: t.id,
      windowId: t.windowId,
      index: t.index,
      title: t.title || "",
      url: t.url || t.pendingUrl || "",
      active: !!t.active,
      focusedWindow: t.windowId === focusedId,
      audible: !!t.audible,
      pinned: !!t.pinned,
      lastAccessed: t.lastAccessed || 0,
      ai: kind.ai,
      convo: kind.convo,
      busy: kind.ai ? busyById.get(t.id) === true : false,
    };
  });
}

// ---------------------------------------------------------------- AI tabs: which, and are they busy

function classify(url) {
  let u;
  try { u = new URL(url || ""); } catch (e) { return { ai: null, convo: false }; }
  if (u.protocol === "https:" || u.protocol === "http:") {
    const host = u.hostname.toLowerCase().replace(/^www\./, "");
    for (const site of AI_SITES)
      if (site.hosts.includes(host)) return { ai: site.ai, convo: site.convo.test(u.pathname) };
  }
  return { ai: null, convo: false };
}

// Runs inside AI tabs. Must stay self-contained. True if a stop-the-reply button is showing.
function pageIsGenerating(selectors, notAReply) {
  const ignore = notAReply ? new RegExp(notAReply, "i") : null;
  const shown = (el) => (el.checkVisibility
    ? el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })
    : !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length));
  for (const sel of selectors) {
    let found;
    try { found = document.querySelectorAll(sel); } catch (e) { continue; }   // one bad selector shouldn't sink the rest
    for (const el of found) {
      const label = (el.getAttribute("aria-label") || "") + " " + (el.getAttribute("data-testid") || "");
      if (ignore && ignore.test(label)) continue;
      if (shown(el)) return true;
    }
  }
  return false;
}

const injecting = new Set();   // tabs whose last check hasn't come back yet (don't pile more on)

async function tabIsBusy(tab) {
  // discarded / frozen / not-yet-loaded tabs can't be scripted (and aren't writing anything)
  if (tab.discarded || tab.frozen || tab.status === "unloaded") return false;
  if (injecting.has(tab.id)) return busyById.get(tab.id) === true;
  const ai = classify(tab.url).ai;
  let timer;
  try {
    injecting.add(tab.id);
    const injected = chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: pageIsGenerating,
      args: [BUSY_SELECTORS[ai] || [], NOT_A_REPLY.source],
      injectImmediately: true,
    }).finally(() => injecting.delete(tab.id));
    const late = new Promise((resolve) => { timer = setTimeout(() => resolve([]), SCRIPT_TIMEOUT_MS); });
    const frames = await Promise.race([injected, late]);
    return !!(frames && frames[0] && frames[0].result === true);
  } catch (e) {
    injecting.delete(tab.id);
    return false;   // error page, no permission, closed mid-scan: not busy, never an error
  } finally {
    clearTimeout(timer);
  }
}

async function scanOnce() {
  const tabs = (await chrome.tabs.query({})).filter((t) => !t.incognito && classify(t.url).ai);
  const live = new Set(tabs.map((t) => t.id));
  for (const id of [...busyById.keys()]) if (!live.has(id)) busyById.delete(id);
  // each tab counts as soon as it answers, so one slow tab doesn't hold up the rest
  await Promise.all(tabs.map(async (t) => {
    const busy = await tabIsBusy(t);
    if ((busyById.get(t.id) === true) !== busy) schedulePush();   // only push when something flipped
    busyById.set(t.id, busy);
    if (busy) lastBusyAt = Date.now();
  }));
}

async function scanAiTabs() {
  if (scanning) { rescan = true; return; }   // one scan at a time; run again right after if asked
  scanning = true;
  try {
    do { rescan = false; await scanOnce(); } while (rescan && saidHello);
  } catch (e) {
    /* tabs API hiccup; try again next tick */
  } finally {
    scanning = false;
  }
}

// Scans run only while we're connected to the app; hello() starts them again after a gap.
async function scanTick() {
  scanTimer = null;
  if (!saidHello) return;
  const started = lastScanAt = Date.now();
  await scanAiTabs();
  const every = Date.now() - lastBusyAt < IDLE_AFTER_MS ? BUSY_SCAN_MS : IDLE_SCAN_MS;
  if (saidHello && !scanTimer) armScan(Math.max(250, started + every - Date.now()));
}

function armScan(delay) {
  clearTimeout(scanTimer);
  scanDueAt = Date.now() + delay;
  scanTimer = setTimeout(scanTick, delay);
}

function startScanner() {
  if (!lastBusyAt) lastBusyAt = Date.now();   // start brisk; relax after a quiet minute
  armScan(0);
}

// Pull the next scan closer (never push it later), but not more than about once a second.
function scanSoon(delay = 250) {
  if (!saidHello) return;
  const due = Math.max(Date.now() + delay, lastScanAt + 1000);
  if (scanTimer && scanDueAt <= due) return;
  armScan(due - Date.now());
}

async function findTab(tabId) {
  const id = Number(tabId);
  if (!Number.isInteger(id)) throw new Error("which tab? i need a tab id");
  let tab;
  try { tab = await chrome.tabs.get(id); } catch (e) { throw new Error("that tab isn't there anymore"); }
  if (tab.incognito) throw new Error("that's a private tab. i don't look at those");
  return tab;
}

// Active tab of the last focused window, or the most recently used active tab anywhere.
async function currentTab(tabId) {
  if (tabId !== undefined && tabId !== null && tabId !== "") return findTab(tabId);
  let [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || tab.incognito) {
    const all = (await chrome.tabs.query({ active: true })).filter((t) => !t.incognito);
    tab = all.sort((a, b) => (b.lastAccessed || 0) - (a.lastAccessed || 0))[0];
  }
  if (!tab) throw new Error("i can't find a tab to look at");
  return tab;
}

// ---------------------------------------------------------------- reading pages

function unreadable(url) {
  if (url !== "about:blank" && /^(chrome|edge|brave|opera|vivaldi|about|devtools|view-source|chrome-extension|chrome-search|chrome-untrusted|extension):/i.test(url))
    return "that's one of the browser's own pages. i'm not allowed to read those";
  if (/^https:\/\/(chromewebstore\.google\.com|chrome\.google\.com\/webstore|microsoftedge\.microsoft\.com\/addons)/i.test(url))
    return "that's the extension store. browsers don't let extensions read it";
  return null;
}

function friendly(e) {
  const m = String((e && e.message) || e);
  if (/file:/i.test(m)) return "i can't read files on your computer unless you turn on \"allow access to file urls\" for bestie bridge";
  if (/error page/i.test(m)) return "that page didn't load, so there's nothing to read";
  if (/No tab with id/i.test(m)) return "that tab isn't there anymore";
  if (/cannot be scripted|Cannot access|gallery/i.test(m)) return "the browser won't let me read that page";
  return "i couldn't read that page (" + m.slice(0, 160) + ")";
}

async function inject(tab, func, args = [], allFrames = false) {
  const why = unreadable(tab.url || tab.pendingUrl || "");
  if (why) throw new Error(why);
  if (tab.discarded) throw new Error("that tab is asleep (the browser unloaded it). open it and ask me again");
  try {
    return await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames }, func, args });
  } catch (e) {
    throw new Error(friendly(e));
  }
}

// Runs inside the page. Must stay self-contained.
function readPage(max, offset) {
  const root = document.body || document.documentElement;
  let text = (root && root.innerText) || "";
  text = text.split(/\r?\n/).map((line) => line.replace(/\s+/g, " ").trim()).filter(Boolean).join("\n");
  const from = Math.max(0, offset || 0);
  const truncated = text.length > from + max;
  return {
    text: text.slice(from, from + max),
    truncated,
    total: text.length,
    title: document.title,
    pdf: document.contentType === "application/pdf",
  };
}

// Runs inside the page (every frame). Password fields are never read.
function readSelection() {
  const el = document.activeElement;
  if (el && (el.tagName === "TEXTAREA" || (el.tagName === "INPUT" && /^(text|search|url|email|tel)$/i.test(el.type)))) {
    try {
      if (el.selectionEnd > el.selectionStart) return el.value.slice(el.selectionStart, el.selectionEnd);
    } catch (e) {}
  }
  const sel = window.getSelection();
  return sel ? String(sel) : "";
}

// ---------------------------------------------------------------- commands

function safeUrl(raw) {
  let s = String(raw == null ? "" : raw).trim();
  if (!s || s === "about:blank") return "about:blank";
  if (!/^[a-z][a-z0-9+.-]*:/i.test(s) && /^[^\s/]+\.[^\s/]+(\/\S*)?$/.test(s)) s = "https://" + s;   // "example.com/x"
  let u;
  try { u = new URL(s); } catch (e) { throw new Error("that doesn't look like a web address: " + s.slice(0, 100)); }
  if (u.protocol !== "http:" && u.protocol !== "https:")
    throw new Error("i only open http and https pages, not " + u.protocol.replace(":", "") + " ones");
  return u.href;
}

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const COMMANDS = {
  async list_tabs() {
    return snapshot();
  },

  async open_tab({ url, active = true }) {
    const href = safeUrl(url);
    const focus = active !== false;
    let tab;
    try {
      tab = await chrome.tabs.create({ url: href, active: focus });
    } catch (e) {   // no browser window open right now
      const win = await chrome.windows.create({ url: href, focused: focus });
      tab = win.tabs && win.tabs[0];
    }
    if (focus && tab) await chrome.windows.update(tab.windowId, { focused: true }).catch(() => {});
    return { tabId: tab ? tab.id : null };
  },

  async focus_tab({ tabId }) {
    const tab = await findTab(tabId);
    await chrome.tabs.update(tab.id, { active: true });
    await chrome.windows.update(tab.windowId, { focused: true });
    return { ok: true };
  },

  async close_tab({ tabId }) {
    const tab = await findTab(tabId);
    await chrome.tabs.remove(tab.id);
    return { ok: true };
  },

  async get_page_text({ tabId, maxChars = 12000, offset = 0 }) {
    const tab = await currentTab(tabId);
    const max = clamp(Number(maxChars) || 12000, 100, 500000);
    const [frame] = await inject(tab, readPage, [max, Math.max(0, Number(offset) || 0)]);
    const page = (frame && frame.result) || {};
    if (!page.text && (page.pdf || /\.pdf([?#]|$)/i.test(tab.url || "")))
      throw new Error("that's a pdf. i can't read pdfs from inside the browser yet");
    return { title: tab.title || page.title || "", url: tab.url || "", text: page.text || "", truncated: !!page.truncated, total: page.total || 0 };
  },

  async get_selection({ tabId }) {
    const tab = await currentTab(tabId);
    let frames;
    try { frames = await inject(tab, readSelection, [], true); }
    catch (e) { frames = await inject(tab, readSelection, []); }
    const hit = (frames || []).map((f) => f && f.result).find((t) => typeof t === "string" && t.trim());
    return { text: (hit || "").slice(0, 50000), title: tab.title || "", url: tab.url || "" };
  },

  async search({ query }) {
    const text = String(query == null ? "" : query).trim();
    if (!text) throw new Error("search for what, though?");
    await chrome.search.query({ text: text.slice(0, 2000), disposition: "NEW_TAB" });
    return { ok: true };
  },

  // ---- working in any tab, including ones in the background --------------------------------

  async navigate_tab({ tabId, url }) {
    const tab = await findTab(tabId);
    await chrome.tabs.update(tab.id, { url: safeUrl(url) });
    return { ok: true };
  },

  async tab_action({ tabId, action }) {
    const tab = await findTab(tabId);
    switch (String(action || "")) {
      case "reload": await chrome.tabs.reload(tab.id); break;
      case "back": await chrome.tabs.goBack(tab.id).catch(() => { throw new Error("there's nowhere to go back to"); }); break;
      case "forward": await chrome.tabs.goForward(tab.id).catch(() => { throw new Error("there's nowhere to go forward to"); }); break;
      case "mute": await chrome.tabs.update(tab.id, { muted: true }); break;
      case "unmute": await chrome.tabs.update(tab.id, { muted: false }); break;
      case "pin": await chrome.tabs.update(tab.id, { pinned: true }); break;
      case "unpin": await chrome.tabs.update(tab.id, { pinned: false }); break;
      case "duplicate": { const t = await chrome.tabs.duplicate(tab.id); return { ok: true, tabId: t ? t.id : null }; }
      default: throw new Error("i don't know the tab action \"" + action + "\"");
    }
    return { ok: true };
  },

  async move_tab({ tabId, index = -1, windowId }) {
    const tab = await findTab(tabId);
    const opts = { index: Number(index) };
    if (windowId !== undefined && windowId !== null) opts.windowId = Number(windowId);
    await chrome.tabs.move(tab.id, opts);
    return { ok: true };
  },

  async group_tabs({ tabIds, title, color }) {
    const ids = (tabIds || []).map(Number).filter((n) => n > 0);
    if (ids.length === 0) throw new Error("group which tabs?");
    const groupId = await chrome.tabs.group({ tabIds: ids });
    const upd = {};
    if (title) upd.title = String(title).slice(0, 60);
    if (color && ["grey", "blue", "red", "yellow", "green", "pink", "purple", "cyan", "orange"].includes(color)) upd.color = color;
    if (Object.keys(upd).length) await chrome.tabGroups.update(groupId, upd);
    return { groupId };
  },

  async ungroup_tabs({ tabIds }) {
    await chrome.tabs.ungroup((tabIds || []).map(Number));
    return { ok: true };
  },

  async find_in_tab({ tabId, query = "", max = 40 }) {
    const tab = await currentTab(tabId);
    const [frame] = await inject(tab, scanElements, [String(query || ""), clamp(Number(max) || 40, 1, 150)]);
    return (frame && frame.result) || { elements: [] };
  },

  async click_in_tab({ tabId, ref }) {
    const tab = await currentTab(tabId);
    const [frame] = await inject(tab, clickRef, [String(ref || "")]);
    const r = (frame && frame.result) || {};
    if (!r.ok) throw new Error(r.error || "that click didn't work");
    return r;
  },

  async type_in_tab({ tabId, ref, text, submit = false }) {
    const tab = await currentTab(tabId);
    const [frame] = await inject(tab, typeRef, [String(ref || ""), String(text == null ? "" : text), !!submit]);
    const r = (frame && frame.result) || {};
    if (!r.ok) throw new Error(r.error || "typing didn't work");
    return r;
  },

  async scroll_tab({ tabId, direction = "down" }) {
    const tab = await currentTab(tabId);
    const [frame] = await inject(tab, scrollPage, [String(direction)]);
    return (frame && frame.result) || { ok: true };
  },

  // Press "Save" on a prefilled Google Calendar event page (one bestie opened from a calendar link).
  // Only works on calendar.google.com, so nothing else can use it to click around.
  async gcal_save({ tabId, timeoutMs = 20000 }) {
    const deadline = Date.now() + clamp(Number(timeoutMs) || 20000, 3000, 60000);
    const isCal = (u) => /^https:\/\/calendar\.google\.com\//.test(u || "");
    let tab = await findTab(tabId);
    while (Date.now() < deadline) {
      tab = await findTab(tabId);
      const url = tab.url || tab.pendingUrl || "";
      if (/^https:\/\/accounts\.google\.com\//.test(url)) throw new Error("you need to be signed in to google in this browser first");
      if (tab.status === "complete" && isCal(url)) break;
      await sleep(250);
    }
    if (!isCal(tab.url)) throw new Error("that tab isn't google calendar");
    while (Date.now() < deadline) {
      let state = "waiting";
      try {
        const [frame] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: pressCalendarSave });
        state = (frame && frame.result) || "waiting";
      } catch (e) { /* page mid-navigation; try again */ }
      if (state === "clicked") {
        // saved once the page leaves the event editor
        for (let i = 0; i < 30; i++) {
          await sleep(250);
          const t = await findTab(tabId).catch(() => null);
          if (!t || !/eventedit|action=TEMPLATE/i.test(t.url || "")) return { ok: true, saved: true };
        }
        return { ok: true, saved: true, unsure: true };
      }
      await sleep(400);
    }
    throw new Error("the save button never showed up");
  },
};

// ---- page-side helpers for working in tabs. Each runs inside the page and must stay self-contained.

// Lists the clickable/typeable things on the page, tagging each with a stable data-bestie-ref.
// "risky" marks things that send, buy, delete, post, confirm or sign out, so the app can ask first.
function scanElements(query, max) {
  const q = (query || "").toLowerCase();
  const RISKY = /\b(send|submit|buy|purchase|pay|checkout|check out|place order|order now|delete|remove|post|publish|tweet|reply|confirm|sign ?out|log ?out|unsubscribe|transfer|donate|book now|reserve|trash|discard|accept|agree|install)\b/i;
  const shown = (el) => {
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== "hidden" && cs.display !== "none" && cs.opacity !== "0";
  };
  const nodes = Array.from(document.querySelectorAll(
    'a[href], button, input, select, textarea, summary, [role=button], [role=link], [role=checkbox], [role=tab], [role=menuitem], [role=option], [role=textbox], [contenteditable=""], [contenteditable=true]'));
  let counter = window.__bestieRef || 0;
  const out = [];
  for (const el of nodes) {
    if (!shown(el)) continue;
    const tag = el.tagName.toLowerCase();
    const type = (el.getAttribute("type") || "").toLowerCase();
    if (tag === "input" && type === "hidden") continue;
    let kind;
    if (tag === "a") kind = "link";
    else if (tag === "select") kind = "select";
    else if (tag === "textarea" || el.isContentEditable || el.getAttribute("role") === "textbox") kind = "textbox";
    else if (tag === "input") kind = ["checkbox", "radio"].includes(type) ? type : ["submit", "button", "reset", "image"].includes(type) ? "button" : "textbox";
    else kind = el.getAttribute("role") || "button";
    const label = (el.getAttribute("aria-label") || (kind === "textbox" ? "" : el.innerText) || el.getAttribute("placeholder") ||
      el.getAttribute("title") || el.getAttribute("name") || (kind === "button" ? el.value : "") || "").replace(/\s+/g, " ").trim().slice(0, 80);
    if (!label && kind !== "textbox") continue;
    if (q && !(label.toLowerCase().includes(q) || (el.getAttribute("href") || "").toLowerCase().includes(q))) continue;
    let ref = el.getAttribute("data-bestie-ref");
    if (!ref) { ref = "e" + (++counter); el.setAttribute("data-bestie-ref", ref); }
    const item = { ref, kind, text: label };
    if (tag === "a") item.href = String(el.href || "").slice(0, 200);
    if (type === "password") item.password = true;
    else if (kind === "textbox") item.value = String(el.value !== undefined ? el.value : el.innerText || "").slice(0, 80);
    if (kind === "checkbox" || kind === "radio") item.checked = !!el.checked;
    const hint = label + " " + (el.getAttribute("data-testid") || "") + " " + (el.id || "");
    if (RISKY.test(hint) || (kind === "button" && el.form && (el.type === "submit" || tag === "input"))) item.risky = true;
    out.push(item);
    if (out.length >= max) break;
  }
  window.__bestieRef = counter;
  return { title: document.title, url: location.href, count: out.length, elements: out };
}

function clickRef(ref) {
  const el = document.querySelector('[data-bestie-ref="' + CSS.escape(ref) + '"]');
  if (!el) return { ok: false, error: "that element isn't on the page anymore (it may have changed). use find_in_tab again" };
  el.scrollIntoView({ block: "center", inline: "center" });
  const label = (el.getAttribute("aria-label") || el.innerText || el.value || "").replace(/\s+/g, " ").trim().slice(0, 80);
  const opts = { bubbles: true, cancelable: true, view: window };
  el.dispatchEvent(new MouseEvent("mousedown", opts));
  el.dispatchEvent(new MouseEvent("mouseup", opts));
  el.click();
  return { ok: true, description: "clicked " + el.tagName.toLowerCase() + (label ? " “" + label + "”" : "") };
}

function typeRef(ref, text, submit) {
  const el = document.querySelector('[data-bestie-ref="' + CSS.escape(ref) + '"]');
  if (!el) return { ok: false, error: "that field isn't on the page anymore. use find_in_tab again" };
  const type = (el.getAttribute("type") || "").toLowerCase();
  if (type === "password" || type === "file") return { ok: false, error: "i don't type into password or file fields" };
  el.scrollIntoView({ block: "center" });
  el.focus();
  if (el.tagName === "SELECT") {
    const opt = Array.from(el.options).find((o) => o.text.trim().toLowerCase() === text.trim().toLowerCase() || o.value === text);
    if (!opt) return { ok: false, error: "there's no option called " + text };
    el.value = opt.value;
    el.dispatchEvent(new Event("change", { bubbles: true }));
  } else if (el.isContentEditable || el.getAttribute("role") === "textbox") {
    document.execCommand("selectAll", false, null);
    document.execCommand("insertText", false, text);
  } else {
    // the native setter + events, so react/vue/etc. notice the change
    const proto = el.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value").set.call(el, text);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }
  if (submit) {
    if (el.form && el.form.requestSubmit) el.form.requestSubmit();
    else {
      const k = { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true, cancelable: true };
      el.dispatchEvent(new KeyboardEvent("keydown", k));
      el.dispatchEvent(new KeyboardEvent("keypress", k));
      el.dispatchEvent(new KeyboardEvent("keyup", k));
    }
  }
  return { ok: true };
}

function scrollPage(dir) {
  const step = window.innerHeight * 0.85;
  if (dir === "top") window.scrollTo(0, 0);
  else if (dir === "bottom") window.scrollTo(0, document.documentElement.scrollHeight);
  else window.scrollBy(0, dir === "up" ? -step : step);
  return { ok: true, y: Math.round(window.scrollY), height: document.documentElement.scrollHeight };
}

// Runs inside calendar.google.com. Must stay self-contained.
function pressCalendarSave() {
  const buttons = Array.from(document.querySelectorAll("button, [role=button]"));
  const save = document.getElementById("xSaveBu") || buttons.find((b) => {
    const label = ((b.getAttribute("aria-label") || b.innerText || "") + "").trim().toLowerCase();
    return label === "save";
  });
  if (!save || save.offsetParent === null || save.disabled || save.getAttribute("aria-disabled") === "true") return "waiting";
  save.click();
  return "clicked";
}

// ---------------------------------------------------------------- small stuff

function browserName() {
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  const known = [["Microsoft Edge", "Edge"], ["Brave", "Brave"], ["Opera", "Opera"], ["Vivaldi", "Vivaldi"], ["Google Chrome", "Chrome"]];
  for (const [brand, name] of known) {
    const hit = brands.find((b) => b.brand === brand);
    if (hit) return name + " " + hit.version;
  }
  const m = /(Edg|OPR|Chrome)\/(\d+)/.exec(navigator.userAgent || "");
  if (m) return ({ Edg: "Edge", OPR: "Opera", Chrome: navigator.brave ? "Brave" : "Chrome" })[m[1]] + " " + m[2];
  return "some chromium browser";
}

// A backoff nap that wake(true) can cut short (e.g. when the popup opens).
let cutNapShort = null;
function nap(ms) {
  return new Promise((resolve) => {
    const done = () => { clearTimeout(timer); cutNapShort = null; resolve(); };
    const timer = setTimeout(done, ms);
    cutNapShort = done;
  });
}

function wake(hurry = false) {
  if (hurry && cutNapShort) { backoff = MIN_BACKOFF_MS; cutNapShort(); }
  loop();
  schedulePush();
}

function startHeartbeat() {
  chrome.alarms.create(HEARTBEAT, { periodInMinutes: 0.5 });
}

// ---------------------------------------------------------------- wiring (all listeners at top level)

chrome.runtime.onInstalled.addListener(() => { startHeartbeat(); wake(); });
chrome.runtime.onStartup.addListener(() => { startHeartbeat(); wake(); });
chrome.alarms.onAlarm.addListener((alarm) => { if (alarm.name === HEARTBEAT) wake(true); });
chrome.runtime.onMessage.addListener((msg) => { if (msg && msg.type === "wake") wake(true); });

chrome.tabs.onCreated.addListener(() => schedulePush());
chrome.tabs.onRemoved.addListener(() => schedulePush());
chrome.tabs.onActivated.addListener(() => schedulePush());
chrome.tabs.onMoved.addListener(() => schedulePush());
chrome.tabs.onAttached.addListener(() => schedulePush());
chrome.tabs.onReplaced.addListener(() => schedulePush());
chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if ("title" in info || "url" in info || "status" in info || "audible" in info || "pinned" in info) schedulePush();
  if (classify((tab && tab.url) || info.url).ai) scanSoon();   // an AI tab changed: check it right away
});
chrome.windows.onFocusChanged.addListener(() => schedulePush());

chrome.alarms.get(HEARTBEAT).then((a) => { if (!a) startHeartbeat(); });
loop();   // every time the service worker starts, for whatever reason
