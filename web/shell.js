/* Plate Kinetics Live - the shell around the analysis page: the side bar (views, instruments, datasets), the overview
   with one live tile per instrument, the experiment strip above the analysis (status, operator, Save), the
   Experiments view (the .pk and .xlsx files: search, open, rename, Excel, Recycle Bin, restore), all runs of Omega's
   own databases (BMG), plate layouts, Settings and About, the problems of the program as banners and the events of
   the runs as notifications. Its look is web/shell.css, its logic (the feed of live curves, texts, numbers) web/live.js.
   The same file serves both programs (BMG and Archimed); what differs is said by live.js (X.ME, X.PROGRAM).

   The server adds both scripts to the head and one line before the page's boot(): SAAShell.mount(window.PKApp).
   mount() then moves the page's own parts (found by id, never copied) into the shell BEFORE the page draws itself for
   the first time, so its code measures the arrangement it will be shown in. The page's code is not changed; what the
   shell does to its elements is: move them, add attributes to the dataset tabs, and style them. The analysis of a
   dataset is read and put back through the page's own functions (app.getDatasetAnalysis, app.applyDatasetAnalysis).

   A page that is not recognised (no window.PKApp, or a part is missing) is left as it is: the overview is then put
   above it ("classic" mode), without live curves. In the online view (window.SAAOnline) only the annotation of an
   experiment can be changed (names, groups, notes, analysis settings: saved on the instrument PC for one who is signed
   in, kept in this browser for anyone else); nothing else is changed or set there. Everyone there can download each
   experiment's .pk and Excel file; one who is signed in also has the Experiments view and all BMG experiments (to
   search, open and download, nothing more) and the PC's status (problems, events, the checks of the PC).

   Languages: every text here is written in English; its Chinese is in web/live.js (ZH), which mount() adds to the
   page's own Chinese pack. The page starts in English unless the user has chosen a language (the page remembers it).
   - Fixed labels (buttons, headings, tooltips, aria-labels, placeholders) are marked en('...') and are NOT under
     translate="no": the page's own translator translates them, also those added later.
   - Texts made from data (states, figures, times, notes, results of actions) are made with say() in the language the
     page is in and sit under translate="no", so the page leaves them alone; they are made again when the page
     switches its language (it sets the lang attribute of <html>). User data (names of datasets, protocols and wells,
     serial numbers) is always under translate="no".
   - Messages of the backend are English (or English and Chinese); they are shown through the page's tr().
   Every request goes through api(): its one call of fetch is the line the online view rewrites. */
(function () {
'use strict';
const X = window.SAALogic;
if (!X || window.SAAShell) return;
const say = X.say;                 // a text in the language of the page (English until mount() hands over the page's own)
const en = X.en;                   // a fixed label: the page's translator shows it in the page's language

/* ---------- small things ---------- */
const $ = (sel, root) => (root || document).querySelector(sel);
/** h('div', { cls: 'x', onclick: f }, child, 'text', [more]) */
function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const k in props) {
    const v = props[k];
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (v === true) el.setAttribute(k, '');
    else if (v !== false && v != null) el.setAttribute(k === 'cls' ? 'class' : k, v);
  }
  el.append(...kids.flat().filter(k => k != null && k !== false));
  return el;
}
const setText = (el, text) => { if (el.textContent !== text) el.textContent = text; };
const setAttr = (el, name, value) => { if (el.getAttribute(name) !== value) el.setAttribute(name, value); };
const show = (el, on) => { if (el && el.hidden === on) el.hidden = !on; };
const button = (label, onclick, cls) => h('button', { cls: 'btn' + (cls ? ' ' + cls : ''), type: 'button', onclick }, label);
/** A button whose label is made with say(): the page's translator leaves it alone. */
const dataButton = (label, onclick, cls) => { const b = button(label, onclick, cls); b.setAttribute('translate', 'no'); return b; };
const fin = Number.isFinite;
const enc = encodeURIComponent;
const LF = String.fromCharCode(10);
const ICONS = {
  overview: 'M2.5 2.5h4.5v4.5h-4.5zM9 2.5h4.5v4.5H9zM2.5 9h4.5v4.5h-4.5zM9 9h4.5v4.5H9z',
  analysis: 'M2 13.5h12M3 11c2.2 0 2.6-7 5-7s2.8 4.5 5 4.5',
  history: 'M8 4.5V8l2.3 1.6M2.6 8a5.4 5.4 0 1 0 1.7-3.9M2.4 2.6v2.6H5',
  plus: 'M8 3v10M3 8h10',
  close: 'M4 4l8 8M12 4l-8 8',
  experiments: 'M1.8 4.2c0-.7.5-1.2 1.2-1.2h3.1l1.5 1.6H13c.7 0 1.2.5 1.2 1.2v6.2c0 .7-.5 1.2-1.2 1.2H3c-.7 0-1.2-.5-1.2-1.2zM1.8 6.4h12.4',
  bmg: 'M2.8 4c0-1.1 2.3-2 5.2-2s5.2.9 5.2 2-2.3 2-5.2 2-5.2-.9-5.2-2zM2.8 4v8c0 1.1 2.3 2 5.2 2s5.2-.9 5.2-2V4M2.8 8c0 1.1 2.3 2 5.2 2s5.2-.9 5.2-2',
  layouts: 'M2 3.5h12v9H2zM2 6.5h12M2 9.5h12M5 3.5v9M8 3.5v9M11 3.5v9',
  settings: 'M8 5.6a2.4 2.4 0 1 0 0 4.8 2.4 2.4 0 0 0 0-4.8zM8 1.8v1.6M8 12.6v1.6M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M1.8 8h1.6M12.6 8h1.6M3.6 12.4l1.1-1.1M11.3 4.7l1.1-1.1',
  help: 'M8 14.2a6.2 6.2 0 1 0 0-12.4 6.2 6.2 0 0 0 0 12.4zM6.2 6.3a1.9 1.9 0 0 1 3.7.6c0 1.3-1.9 1.6-1.9 2.8M8 11.5v.2',
  about: 'M8 14.2a6.2 6.2 0 1 0 0-12.4 6.2 6.2 0 0 0 0 12.4zM8 7.3v4M8 4.9v.2',
  alert: 'M8 2.3l6.2 11H1.8zM8 6.6v3.1M8 11.5v.2',
  info: 'M8 14.2a6.2 6.2 0 1 0 0-12.4 6.2 6.2 0 0 0 0 12.4zM8 7.3v4M8 4.9v.2',
  check: 'M3 8.5l3.2 3L13 4.8',
  person: 'M8 8.2a2.7 2.7 0 1 0 0-5.4 2.7 2.7 0 0 0 0 5.4zM2.8 14c.4-2.7 2.6-4.2 5.2-4.2s4.8 1.5 5.2 4.2',
  more: 'M3.5 8h.2M8 8h.2M12.5 8h.2',
  folder: 'M1.8 4.2c0-.7.5-1.2 1.2-1.2h3.1l1.5 1.6H13c.7 0 1.2.5 1.2 1.2v6.2c0 .7-.5 1.2-1.2 1.2H3c-.7 0-1.2-.5-1.2-1.2z',
  download: 'M8 2.5v8M4.8 7.4L8 10.6l3.2-3.2M2.8 13.3h10.4',
  back: 'M9.5 3.5L5 8l4.5 4.5'
};
function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16'); svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.5'); svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round'); svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICONS[name]);
  svg.appendChild(path);
  return svg;
}

let app = null;                    // window.PKApp, or null on a page that is not recognised
let core = null;                   // the page's PKCore (its languages): app.C, else window.PKCore when there is one
let mode = '';                     // 'shell' | 'classic'
let view = 'overview';             // overview | analysis | experiments | bmg | layouts | settings | about (shell mode only)
let online = false;                // the online view of this PC (window.SAAOnline, set after the shell is loaded): it only shows
const token = () => (document.querySelector('meta[name="saa-token"]') || {}).content || '';
const PREFS_KEY = 'saa.panel.v1';
const prefs = { auto: {} };
try { Object.assign(prefs, JSON.parse(localStorage.getItem(PREFS_KEY))); } catch (e) { /* no storage: defaults */ }
for (const k of ['auto', 'dismissed', 'seen', 'paths', 'runIds', 'filter', 'sync']) if (!prefs[k] || typeof prefs[k] !== 'object' || Array.isArray(prefs[k])) prefs[k] = {};
let prefsTimer = 0;
const savePrefs = () => { clearTimeout(prefsTimer); prefsTimer = 0; try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch (e) { /* the choice lasts for this session */ } };
const savePrefsSoon = () => { if (!prefsTimer) prefsTimer = setTimeout(savePrefs, 3000); };
const autoOn = key => prefs.auto[key] !== false;
/** A message of the backend (English) in the language of the page. */
const tr = text => (text && core && typeof core.tr === 'function' ? core.tr(text) : text);
const inChinese = () => /^zh/i.test(document.documentElement.lang || '');
/** The program's window (WebView2): messages go to it with postMessage. null in a browser. */
const host = () => (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function' ? window.chrome.webview : null);
/** What the program's window says about itself before the page runs: { window, user, pc, edition, recordsHere }. */
const hostInfo = () => (window.PKHost && typeof window.PKHost === 'object' ? window.PKHost : {});

/** Every request to the program goes through here. opt = { method ('GET', 'POST' with a body), body (an object is
    sent as JSON, bytes as they are), seconds (15): how long to wait, as: 'blob' for a file, keepalive: the request
    outlives the page (it is being closed) }. An answer that is not OK throws an Error that carries its status and the
    server's own words. A POST names this site as its origin (under the page's no-referrer policy it would say "null";
    the online view checks the origin of every POST). */
async function api(path, opt) {
  const o = opt || {};
  const headers = { 'X-SAA-Token': token() };
  let body;
  if (o.body !== undefined) {
    const raw = o.body instanceof Blob || o.body instanceof ArrayBuffer || ArrayBuffer.isView(o.body);
    body = raw ? o.body : JSON.stringify(o.body);
    headers['Content-Type'] = raw ? 'application/octet-stream' : 'application/json';
  }
  const method = o.method || (body !== undefined ? 'POST' : 'GET');
  const init = { method, cache: 'no-store', headers, body, signal: AbortSignal.timeout((o.seconds || 15) * 1000) };
  if (method === 'POST') init.referrerPolicy = 'same-origin';
  if (o.keepalive) init.keepalive = true;
  let res;
  try {
    res = await fetch(path, init);
  } catch (e) { throw new Error(say('The Plate Kinetics Live background program does not answer')); }
  if (res.ok && o.as === 'blob') {
    // (a file the online view passes on is cut off when the PC drops out: then no half file is saved)
    try { return { blob: await res.blob(), name: X.fileNameOf(res.headers.get('Content-Disposition')) }; }
    catch (e) { throw new Error(say('The file did not arrive in full: please try again')); }
  }
  const data = await res.json().catch(() => null);
  if (!res.ok) {
    // the program's words ({ error, errorZh }), or the online server's ({ error: its code, message, messageZh })
    const own = data && typeof data === 'object' ? (inChinese() ? data.messageZh || data.errorZh || data.message || data.error : data.message || data.error || data.errorZh) : '';
    // online, what needs a sign-in: the sign-in of this device has ended (or never was); online.js looks again
    const signIn = online && res.status === 403 && !!data && data.error === 'sign-in required';
    if (signIn && window.SAAOnline && typeof window.SAAOnline.refresh === 'function') window.SAAOnline.refresh();
    const error = new Error(signIn ? say('Sign in to see this') : res.status === 403 ? (online ? say('The request was refused: please reload the page')
      : say('The background program refused the request: close the window and start the program again'))
      : own ? tr(String(own)) : res.status === 404 ? say('The background program does not have this data') : say('The request failed ({0})', res.status));
    error.status = res.status;
    error.signIn = signIn;
    throw error;
  }
  return data;
}
/** Online: an experiment's complete file, its .pk (data, information, the saved annotation) or its Excel file (GET
    /api/download; while it is measuring, the data so far), saved as a download. Open to everyone who has the link, for
    the runs the view shows; the online server passes the program's file on. */
async function download(runId, kind, b) {
  if (b) b.disabled = true;
  try {
    const got = await api('/api/download?runId=' + enc(runId) + '&kind=' + kind, { as: 'blob', seconds: 300 });
    saveBlob(got.blob, got.name || runId + '.' + kind);
  } catch (e) { toast(say('The file could not be downloaded: {0}', e.message), 8000); }
  if (b) b.disabled = false;
}
/** The online view's two buttons of an experiment: its .pk and its Excel file (for run ids the online server takes). */
function downloadButtons(runId) {
  if (!online || !downloadable(runId)) return null;
  // (the labels are names of file types: the same in every language; the tooltips are translated)
  const make = (kind, label, hint) => {
    const b = h('button', { cls: 'btn sm ghost', type: 'button', title: hint, 'aria-label': hint, onclick: () => download(runId, kind, b) }, icon('download'), h('span', { translate: 'no' }, label));
    return b;
  };
  return h('span', { cls: 'saa-dl' }, make('pk', PK, en('Download the experiment file (.pk)')), make('xlsx', EXCEL, en('Download the Excel file')));
}
const downloadable = runId => /^[A-Za-z0-9_-]{1,80}$/.test(String(runId || ''));
/** A file the program sends (an export, the support file) saved as a download; in the program's window a "Save as"
    dialog asks where (in the folder of the experiment shown). */
function saveBlob(blob, name) {
  const url = URL.createObjectURL(blob), a = h('a', { href: url, download: name || 'download' });
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
function toast(text, ms) {
  const box = document.getElementById('toasts');
  if (!box || !text) return;
  const el = h('div', { cls: 'toast', translate: 'no' }, text);
  box.appendChild(el);
  setTimeout(() => el.remove(), ms || 4000);
}
/** A dialog in the page's own style (window.confirm is blocked in some hosts). foot(close) returns its buttons, the
    harmless one first: it gets the focus. Escape, the cross and a click outside close it too. Returns close().
    The title and the buttons are fixed labels; what the content says from data is under translate="no". */
function dialog(title, content, foot, onClose, wide) {
  const last = document.activeElement;
  const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
  function close() {
    if (!back.isConnected) return;
    back.remove();
    document.removeEventListener('keydown', onKey, true);
    if (last && last.focus) last.focus();
    if (onClose) onClose();
  }
  const back = h('div', { cls: 'modal-back', onmousedown: e => { if (e.target === back) close(); } },
    h('div', { cls: 'modal saa-dialog' + (wide ? ' saa-dialog-wide' : ''), role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
      h('div', { cls: 'modal-head' }, h('h3', null, title), h('button', { cls: 'btn icon ghost', type: 'button', 'aria-label': en('Close'), onclick: close }, icon('close'))),
      h('div', { cls: 'modal-body' }, content),
      h('div', { cls: 'modal-foot' }, foot(close))));
  document.addEventListener('keydown', onKey, true);
  document.body.appendChild(back);
  const first = back.querySelector('.modal-body input, .modal-body select') || back.querySelector('.modal-foot button');
  if (first) first.focus();
  return close;
}
/** Asks before something that cannot be taken back; resolves to true only through the confirming button. */
function confirmDialog(title, lines, okLabel) {
  return new Promise(resolve => {
    let yes = false;
    dialog(title, lines.map(t => h('p', { translate: 'no' }, t)), close => [button(en('Cancel'), close), button(okLabel, () => { yes = true; close(); }, 'primary')], () => resolve(yes));
  });
}
/** Asks for a line of text; resolves to it, or to null when the dialog is closed. */
function askText(title, label, value, okLabel) {
  return new Promise(resolve => {
    let answer = null;
    const input = h('input', { type: 'text', cls: 'saa-wide-input', value: value || '', maxlength: '200', 'aria-label': label });
    const ok = close => { answer = input.value.trim(); close(); };
    let shut = null;
    input.addEventListener('keydown', e => { if (e.key === 'Enter' && input.value.trim()) { e.preventDefault(); ok(shut); } });
    shut = dialog(title, [h('label', { cls: 'saa-field' }, h('span', null, label), input)],
      close => [button(en('Cancel'), close), button(okLabel, () => { if (input.value.trim()) ok(close); }, 'primary')], () => resolve(answer));
    input.select();
  });
}

/* ---------- what the user is doing: redrawing the page under a half-typed name or a drag would destroy it ---------- */
const ui = { pointerDown: false, lastInput: 0 };
const own = el => !!(el && el.closest && el.closest('[data-saa]'));          // our own fields: the page never redraws them
function busy() {
  const el = document.activeElement;
  // a name typed into a list the page rebuilds (a group, a dataset, a grouping) would be lost by a redraw, however
  // long the pause: it holds until the box is left
  const naming = !!(el && el.matches && el.matches('#groupList .gname, #dsTabs input, #gsets .gset-in'));
  return X.holdReason({
    pointerDown: ui.pointerDown, overlay: !!document.querySelector('.modal-back, .pop'),
    entry: X.isEntry(el) && !own(el), idleMs: naming ? 0 : Date.now() - ui.lastInput
  });
}

/* ===================== state ===================== */
let state = null, offline = '', retry = 0, feed = null, recent = null, recentAt = 0, health = null, settings = null, layoutList = null;
const tiles = new Map();           // instrument key -> its tile in the overview
const rows = new Map();            // instrument key -> its row in the side bar
const readers = () => (state && Array.isArray(state.instruments) ? state.instruments : []);
const readerOf = key => readers().find(i => i.key === key) || null;
const title = inst => X.reader(inst.key).name;
const subtitle = inst => inst.label || [inst.key, inst.serial].filter(Boolean).join(' · ');
const badge = key => String(X.reader(key).n || '•');
/** The names on screen: those of the settings, else those the state gives its instruments (the online view has no settings). */
function useScreenNames() {
  const names = {};
  for (const i of readers()) if (i.displayName) names[i.key] = { displayName: String(i.displayName) };
  X.setScreenNames(Object.assign(names, settings ? settings.instruments : {}));
}
const experimentsFolder = () => String((state && state.storage && state.storage.experimentsFolder) || (health && health.experimentsFolder) || '');
const edition = () => String(hostInfo().edition || (health && health.edition) || '');

/** The dataset on the page for a run (channel 0 = its first channel), or null. */
function datasetOf(runId, channel) {
  if (!app) return null;
  const id = X.datasetId(runId, channel || 0);
  return app.S.ds.find(d => d.id === id) || null;
}
const dsById = id => (app ? app.S.ds.find(d => d.id === id) || null : null);
const channelsOf = runId => { let n = 0; while (datasetOf(runId, n)) n++; return n; };
/** Which instrument a dataset came from, by its id (a run's id begins with the instrument's key): the instrument or null. */
function readerOfDataset(ds) {
  if (!ds || typeof ds.id !== 'string') return null;
  return readers().filter(i => ds.id.startsWith(i.key + '-')).sort((a, b) => b.key.length - a.key.length)[0] || null;
}

/* ===================== building ===================== */
let shell = null, overview, analysis, tilesBox, noteBox, sideNote, recentBox, navButtons = {}, readerBox, titleMain, titleSub,
  settingsCard, settingsButton, dataTabs = null, bannerBox, eventsBox, olderButton = null, pageSave = null;

function pageParts() {
  const bar = $('header.topbar');
  const parts = { bar, brand: bar && $('.brand', bar), tabs: $('#dsTabs'), actions: bar && $('.top-actions', bar), notice: $('#notice'), work: $('#workspace'), toasts: $('#toasts') };
  return Object.values(parts).every(Boolean) ? parts : null;
}

function mount(pkApp) {
  if (mode) return;
  app = pkApp || null;
  online = !!window.SAAOnline;
  // The words of the shell go into the page's own Chinese pack and its texts are made with the page's translator, so
  // they follow the language the page is in. (Without the hook a page that has the same core is used the same way.)
  core = app ? app.C : window.PKCore || null;
  if (core) { X.addChinese(core); X.useWords(app ? app.L : core.T); }
  // an experiment is saved into its own file: no unsaved change of the page's project file (its "●", the question
  // when the page is closed)
  if (app && app.S) app.S.savedElsewhere = ds => !!expOf(ds);
  // the plate layouts the page saves and applies (its menu "Layouts"): the program's own library (/api/layouts, the one
  // the next experiment of an instrument uses); the online view does not reach it: the page keeps them in the browser,
  // and saves them for one who is signed in for editing only
  if (app && app.S) app.S.layoutStore = online
    ? { note: en('Layouts are kept in this browser only: the online view does not reach the plate layouts of the program'),
      canSave: () => editor(), why: en('Saving layouts here needs an editor account: sign in first') }
    : { note: en('Layouts are kept in the plate layouts of this program, which the next experiment of an instrument can use'),
      list: () => api('/api/layouts', { seconds: 30 }),
      save: l => api('/api/layouts', { method: 'POST', body: { layout: l }, seconds: 30 }).then(a => { layoutsChanged(); return a && a.id; }),
      remove: id => api('/api/layouts/delete?id=' + enc(id), { method: 'POST', seconds: 30 }).then(() => { layoutsChanged(); }) };
  // datasets the page changed that may not be in view (a grouping set copied into them): saved into their experiments
  // as a change of the one in view is (web/live.js createSync().touch)
  if (app && app.S) app.S.datasetsEdited = ids => {
    for (const id of Array.isArray(ids) ? ids : []) { const x = expOf(dsById(id)); if (x) sync.touch(x, id); }
    syncSoon();
  };
  const parts = app ? pageParts() : null;
  mode = parts ? 'shell' : 'classic';
  document.documentElement.classList.add(parts ? 'saa-mode-shell' : 'saa-mode-classic');
  if (online) document.documentElement.classList.add('saa-view-only');
  buildOverview();
  if (parts) buildShell(parts); else document.body.prepend(bannerBox, overview);
  eventsBox = h('div', { cls: 'saa-events', 'data-saa': true, 'aria-live': 'polite' });
  document.body.appendChild(eventsBox);
  if (app) setTimeout(start, 0); else start();          // from the hook: after the page's boot()
}

/** The analysis settings, set once per kind of assay, become a strip across the top of the analysis view, shown on
    demand: open, they sit right above the curves and the plate they change. */
function settingsStrip(p) {
  settingsCard = $('#settingsCard');
  if (!settingsCard) return;
  p.work.prepend(settingsCard);
  settingsButton = h('button', { cls: 'btn saa-toggle', type: 'button', title: en('Threshold, lag time, plateau and the other settings of the algorithms'),
    onclick: () => showSettings(settingsCard.hidden) }, en('Analysis settings'));
  const menu = $('#btnMenu', p.actions);
  if (menu) menu.before(settingsButton); else p.actions.append(settingsButton);
  showSettings(prefs.settings === true, true);
}

function buildShell(p) {
  const nav = (name, label) => (navButtons[name] = h('button', { cls: 'saa-nav-item', type: 'button', onclick: () => go(name) }, icon(name), h('span', null, label)));
  const navAction = (name, label, onclick) => h('button', { cls: 'saa-nav-item', type: 'button', onclick }, icon(name), h('span', null, label));
  // the online view of this PC: no layouts, no settings, nothing to save but annotations; the experiments and Omega's
  // runs only for one who is signed in, read-only (editorViews)
  buildLists();
  if (!online) { buildLayouts(); buildSettingsView(); }
  buildAbout();
  olderButton = h('button', { cls: 'saa-older', type: 'button', translate: 'no', hidden: true, onclick: () => { prefs.showOlder = !prefs.showOlder; savePrefs(); markTabs(); } });
  const side = h('aside', { cls: 'saa-side' },
    p.brand,
    h('nav', { cls: 'saa-nav', 'aria-label': en('Views') }, nav('overview', en('Overview')), nav('analysis', en('Analysis')),
      nav('experiments', en('Experiments')), X.PROGRAM === 'bmg' ? nav('bmg', en('All BMG experiments')) : null,
      online ? null : nav('layouts', en('Plate layouts'))),
    h('div', { cls: 'saa-sec' }, h('span', null, en('Instruments'))),
    sideNote = h('div', { cls: 'saa-note saa-side-note', translate: 'no', hidden: true }),
    readerBox = h('div', { cls: 'saa-readers' }),
    h('div', { cls: 'saa-sec' }, h('span', null, en('Data')),
      h('button', { cls: 'saa-mini', type: 'button', title: en('Import a data file'), 'aria-label': en('Import a data file'), onclick: () => { const b = $('#btnImport'); if (b) b.click(); } }, icon('plus'))),
    p.tabs,
    olderButton,
    h('div', { cls: 'saa-side-foot' },
      online ? navAction('history', en('History'), openHistory) : nav('settings', en('Settings')),
      online ? null : navAction('help', en('Help'), openHelp),
      nav('about', en('About'))));
  // the page's version badge offers the page for offline use (a thing of the website): in the program it opens About
  const versionBadge = $('#brandVersion', p.brand);
  if (versionBadge) {
    const toAbout = e => { if (e.type === 'keydown' && e.key !== 'Enter' && e.key !== ' ') return; e.preventDefault(); e.stopImmediatePropagation(); go('about'); };
    versionBadge.addEventListener('click', toAbout, true);
    versionBadge.addEventListener('keydown', toAbout, true);
  }
  p.bar.prepend(h('div', { cls: 'saa-title', translate: 'no' }, titleMain = h('h1'), titleSub = h('span')));
  dataTabs = p.tabs;
  dataTabs.setAttribute('data-empty', say('No data yet'));        // in the page's language from start() on (relang)
  pageSave = $('#btnSave', p.actions);
  stripProtocol = protocolBox('saa-pd-card');
  stripProtocol.el.hidden = true;
  analysis = h('div', { cls: 'saa-view saa-analysis' }, buildStrip(), stripProtocol.el, p.notice, p.work);
  shell = h('div', { id: 'saaShell', cls: 'saa-shell' }, side,
    h('div', { cls: 'saa-main' }, bannerBox, p.bar, overview, analysis, filesView, omegaView, layoutsView, settingsView, aboutView));
  document.body.prepend(shell);
  settingsStrip(p);
  // a dataset picked in the side bar is what the user wants to see
  p.tabs.addEventListener('click', e => { if (e.target.closest('.ds-tab') && !e.target.closest('[data-close]')) go('analysis'); });
  p.tabs.addEventListener('keydown', e => { if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('ds-tab')) go('analysis'); });
  new MutationObserver(tabsChanged).observe(p.tabs, { childList: true });
  editorViews();
  go('overview', true);
}
/** Online, the Experiments view and all BMG experiments are there for one who is signed in (to look, open and download:
    nothing is changed there); they come and go with the sign-in, and what they had read goes with it. */
const editorView = name => name === 'experiments' || name === 'bmg';
function editorViews() {
  const on = !online || editor();
  for (const name in navButtons) if (editorView(name)) show(navButtons[name], on);
  if (on) return;
  files.list = null; files.removed = []; files.error = ''; files.at = 0;
  omega.list = null; omega.error = '';
  health = null;
  if (mode === 'shell' && editorView(view)) go('overview');
}
/** Online: this device was signed in or out (online.js: the event "saaonline:me"). */
function signedInChanged() {
  editorViews();
  bannerSig = '';
  if (view === 'about') renderAbout(true);
  render();
}

function buildOverview() {
  // the banners: problems of the program (a save failed, the disk is nearly full, a start-up repair, ...). Above the
  // overview of a page that is not recognised; buildShell() puts them at the top of the main column.
  bannerBox = h('div', { cls: 'saa-banners', 'data-saa': true });
  overview = h('section', { cls: 'saa-view saa-overview', 'data-saa': true, 'aria-label': en('Overview') },
    noteBox = h('div', { cls: 'saa-note', translate: 'no', hidden: true }),
    tilesBox = h('div', { cls: 'saa-tiles' }),
    recentBox = h('div', { cls: 'saa-recent', hidden: true }));
}

/* ===================== views ===================== */
function showSettings(on, quiet) {
  show(settingsCard, on);
  settingsButton.setAttribute('aria-pressed', String(on));
  if (quiet) return;
  prefs.settings = on;
  savePrefs();
  if (on) settingsCard.scrollIntoView({ block: 'nearest' });
}
function go(next, quiet) {
  const views = { overview, analysis, experiments: filesView, bmg: omegaView, layouts: layoutsView, settings: settingsView, about: aboutView };
  if (mode !== 'shell' || !views[next] || (online && !editor() && editorView(next))) return;
  const changed = view !== next;
  view = next;
  setAttr(shell, 'data-view', next);
  for (const name in views) if (views[name]) show(views[name], name === next);
  for (const name in navButtons) navButtons[name].classList.toggle('on', name === next);
  if (quiet) return;
  if (changed) {
    window.scrollTo(0, 0);
    // what the page drew while its cards were not shown was measured as zero wide
    if (next === 'analysis') app.inv('plate', 'chart', 'results');
  }
  if (next === 'experiments') loadFiles();             // read again every time it is shown: a run may have been added
  if (next === 'bmg') loadOmega(false);                // read once (the databases can be large): Refresh reads again
  if (next === 'layouts' && changed) loadLayouts();
  if (next === 'settings' && changed) openSettingsView();
  if (next === 'about' && changed) renderAbout(true);
  render();
}
/** Brings a dataset to the front in the analysis view. False when it is not on the page. */
function openDataset(ds) {
  const S = app.S, at = S.ds.indexOf(ds);
  if (at < 0) return false;
  if (at !== S.cur) { S.cur = at; S.zoom = null; S.sort = { key: null, dir: 1 }; app.invAll(); }
  go('analysis');
  return true;
}
/** The curves of an instrument's run when they are on the page, else its tile. */
function openReader(key) {
  const inst = readerOf(key), tile = tiles.get(key);
  const ds = inst && inst.run && datasetOf(inst.run.id, tile ? tile.channel : 0);
  if (ds && openDataset(ds)) return;
  go('overview');
  if (tile) { tile.el.scrollIntoView({ block: 'nearest' }); tile.el.classList.remove('flash'); void tile.el.offsetWidth; tile.el.classList.add('flash'); }
}

let lastCurrent = null;
/** Marks the datasets of the list that are runs of an instrument: its number in front, the dot while it is measured;
    folds away the runs that ended more than 7 days ago and were not opened since. Called when the page has redrawn
    its list and with every news of the instruments (a run that has ended is no longer live; after a restart the
    instruments are known later than the list is drawn). */
function markTabs() {
  if (!app || !dataTabs) return;
  const S = app.S, tabs = dataTabs.querySelectorAll('.ds-tab[data-i]');
  if (tabs.length !== S.ds.length) return;          // the page has not redrawn its list yet: these are old indexes
  const live = new Set(readers().filter(i => i.state === 'measuring' && i.run).map(i => i.run.id)), cur = app.curDS();
  const fold = { live, seen: prefs.seen, current: cur ? cur.id : '', now: Date.now() };
  let folded = 0;
  for (const el of tabs) {
    const ds = S.ds[+el.dataset.i], inst = readerOfDataset(ds);
    if (inst) setAttr(el, 'data-reader', badge(inst.key)); else el.removeAttribute('data-reader');
    el.toggleAttribute('data-live', !!(inst && inst.state === 'measuring' && inst.run && ds.id.startsWith(inst.run.id)));
    const away = X.foldable(ds, fold);
    el.toggleAttribute('data-folded', away);
    if (away) folded++;
  }
  dataTabs.classList.toggle('saa-show-older', !!prefs.showOlder);
  if (olderButton) {
    show(olderButton, folded > 0);
    setText(olderButton, prefs.showOlder ? say('Hide older experiments') : say('Show {0:older experiment}', folded));
    setAttr(olderButton, 'title', say('Experiments that ended more than a week ago and were not opened since'));
    setAttr(olderButton, 'aria-expanded', String(!!prefs.showOlder));
  }
}
/** The page has redrawn its dataset list: mark the datasets that are runs of an instrument, and follow what it opened. */
function tabsChanged() {
  if (!app) return;
  markTabs();
  const cur = app.curDS();
  // a file was imported, a project opened or the example loaded while another view was showing: that is what to look at
  if (cur && lastCurrent && cur !== lastCurrent && view !== 'analysis' && !readerOfDataset(cur)) go('analysis');
  lastCurrent = cur;
  renderTitle();
  renderStrip();
}

const TITLES = { experiments: en('Experiments'), bmg: en('All BMG experiments'), layouts: en('Plate layouts'), settings: en('Settings'), about: en('About') };
function renderTitle() {
  if (mode !== 'shell') return;
  const list = readers(), measuring = list.filter(i => i.state === 'measuring').length;
  if (view === 'overview') {
    setText(titleMain, say('Overview'));
    const awake = state && state.keepAwake && state.keepAwake.active ? say('This PC is kept awake while an experiment is measuring') : '';
    setText(titleSub, !state || !list.length ? awake : [say('{0:instrument}', list.length), measuring ? say('{0} measuring', measuring) : say('No run in progress'), awake].filter(Boolean).join(' · '));
    return;
  }
  if (view !== 'analysis') {
    setText(titleMain, say(TITLES[view] || view));
    const shown = view === 'bmg' ? omega.list : view === 'experiments' ? files.list : view === 'layouts' ? layoutList : null;
    const count = shown ? (view === 'layouts' ? say('{0:layout}', shown.length) : say('{0:experiment}', shown.length)) : '';
    setText(titleSub, view === 'settings' || view === 'about' ? '' : [count, view === 'experiments' && !online ? experimentsFolder() : ''].filter(Boolean).join(' · '));
    return;
  }
  const ds = app.curDS(), inst = readerOfDataset(ds);
  if (settingsButton) settingsButton.disabled = !ds;          // the settings strip is part of a dataset's view
  setText(titleMain, ds ? ds.name : say('Analysis'));
  const live = !!(inst && inst.run && ds.id.startsWith(inst.run.id));
  // for the run in view: where it stands, and what the feed has to say (waiting for the user, a failed read, auto-load off)
  const note = live && feed ? X.tileLive(feed.describe(inst.run.id), true, true).note : '';
  setText(titleSub, !ds ? '' : inst ? [X.readerNames(inst.key).some(n => n && ds.name.includes(n)) ? '' : title(inst), live ? X.stateText(inst) : '', live ? X.progressText(inst) : '', note].filter(Boolean).join(' · ')
    : ds.file && ds.file !== ds.name ? ds.file : '');
}

/** The page has switched its language: everything made with say() is made again (the page's translator takes care of
    the fixed labels). Also called once at the start, after the page has set its language. */
function relang() {
  if (dataTabs) setAttr(dataTabs, 'data-empty', say('No data yet'));      // shown by the style sheet: no translator sees it
  for (const t of tiles.values()) { t.lastSig = null; t.groupSig = null; t.channels.replaceChildren(); }
  bannerSig = '';
  renderEvents();
  renderRecent();
  renderFiles();
  renderOmega();
  renderLayouts();
  if (view === 'settings') renderSettings();
  if (view === 'about') renderAbout(false);
  stripSig = '';
  render();
}

/* ===================== polling and drawing ===================== */
/** Feeds the page and redraws the shell; again shortly while the feed is waiting for the page or the user. */
function kick() {
  clearTimeout(retry);
  retry = feed && feed.step() ? setTimeout(kick, 400) : 0;
  render();
}
let firstState = true, pollTimer = 0;
async function poll() {
  clearTimeout(pollTimer);
  try {
    state = await api('/api/state'); offline = '';
    if (readers().some(i => i.displayName)) useScreenNames();
    if (feed) feed.track(readers());
    for (const i of readers()) if (i.run && i.run.analysisRev !== undefined) sync.remote(i.run.id, i.run.analysisRev);     // a newer annotation saved elsewhere
    for (const i of readers()) if (i.run) noteProtocol(i.run.id, i.run.protocolDetails);
    // while the program reads its instrument's files for the first time ("starting") it has no instrument to report
    // yet: that is not "not found", so the overview stays and says nothing about it until the first look is done
    if (firstState && !state.starting) { firstState = false; if (mode === 'shell' && !readers().length) go('analysis'); }
  } catch (e) { offline = e.message; }
  // whatever goes wrong in the drawing, the next look must come: a window that stops asking shows "measuring" for ever
  try {
    kick();
    if (!offline) { announce(); openQueued(); }
    if (Date.now() - recentAt > 60000) loadRecent();
    if (view === 'experiments' && Date.now() - files.at > 30000) loadFiles();      // a run that is measured is written every cycle
  } catch (e) { console.error(e); }
  pollTimer = setTimeout(poll, 2000);
}
function render() {
  const list = readers();
  markTabs();
  renderBanners();
  const notes = [offline ? say('{0}; what is shown below is the state before the connection was lost.', offline) : '',
    state && !state.starting && !list.length ? say('{0} was not found on this PC: only imported data can be analysed.', X.ME.instruments) : '',
    app ? '' : say('Live curves need the analysis page that comes with this program: this page cannot load them. The instrument status is shown as usual.')].filter(Boolean);
  setText(noteBox, notes.join(' '));
  show(noteBox, notes.length > 0);
  if (sideNote) {               // the side bar is in sight in every view: what the overview's note says must not be missed elsewhere
    setText(sideNote, notes.join(' '));
    show(sideNote, notes.length > 0);
    setAttr(shell, 'data-offline', offline ? '1' : '0');
  }
  for (const inst of list) {
    if (!tiles.has(inst.key)) { tiles.set(inst.key, makeTile(inst.key)); tilesBox.appendChild(tiles.get(inst.key).el); }
    updateTile(tiles.get(inst.key), inst);
    if (readerBox) {
      if (!rows.has(inst.key)) { rows.set(inst.key, makeRow(inst.key)); readerBox.appendChild(rows.get(inst.key).el); }
      updateRow(rows.get(inst.key), inst);
    }
  }
  for (const group of [tiles, rows]) for (const x of group.values()) show(x.el, list.some(inst => inst.key === x.key));
  renderTitle();
  renderStrip();
}

/* ---------- problems: one banner each, errors first; "recovered" and the like can be closed ---------- */
let bannerSig = '';
function renderBanners() {
  // online the PC's status is for one who is signed in: the state of anyone else carries no problems (and its "storage"
  // alone says nothing there)
  const lines = online && !editor() && !(state && Array.isArray(state.problems)) ? [] : X.problemLines(state, prefs.dismissed, inChinese());
  const sig = JSON.stringify(lines.map(p => [p.key, p.severity, p.text, p.since, p.dismissible])) + document.documentElement.lang;
  if (sig === bannerSig) return;
  bannerSig = sig;
  bannerBox.replaceChildren(...lines.map(p => h('div', { cls: 'saa-banner', 'data-sev': p.severity, role: p.severity === 'error' ? 'alert' : 'status', translate: 'no' },
    icon(p.severity === 'info' ? 'info' : 'alert'), h('span', null, p.text),
    p.dismissible ? h('button', { cls: 'saa-banner-x', type: 'button', title: say('Close this message'), 'aria-label': say('Close this message'),
      onclick: () => { prefs.dismissed[p.key] = p.since; savePrefs(); bannerSig = ''; renderBanners(); } }, icon('close')) : null)));
}

/* ---------- events: a notification in the window for each, once (the installed program's tray also tells Windows) ---------- */
/** The events of the state that were not announced in this browser yet: each as a card at the bottom right, with a
    sound when the settings ask for one - not in the installed program's window, whose tray icon already sounds, and
    not in the online view. The first time the window is opened the events there are only noted: nothing old is
    announced. (What was announced is kept in the browser, read again each time: a second window does not repeat it.) */
function announce() {
  if (!state || !Array.isArray(state.events)) return;
  try { const stored = JSON.parse(localStorage.getItem(PREFS_KEY)); if (stored && Array.isArray(stored.events)) prefs.events = stored.events; } catch (e) { /* this window's own list */ }
  const first = !Array.isArray(prefs.events), seen = new Set(first ? [] : prefs.events);
  const fresh = X.newEvents(state.events, seen);
  if (!fresh.length && !first) return;
  for (const e of fresh) seen.add(X.eventKey(e));
  prefs.events = [...seen].slice(-200);
  savePrefs();
  if (first) return;
  const shown = fresh.filter(e => X.wanted(e, settings));
  if (!shown.length) return;
  cards.unshift(...shown.reverse());
  cards.splice(4);
  renderEvents();
  const sound = settings ? settings.notify.sound : true;
  if (sound && !online && edition() !== 'installed') beep();
}
const cards = [];                  // the events shown, the newest first (four at most); made again in another language
function renderEvents() {
  if (!eventsBox) return;
  const close = e => { const at = cards.indexOf(e); if (at >= 0) cards.splice(at, 1); renderEvents(); };
  eventsBox.replaceChildren(...cards.map(e => {
    const kind = String(e.kind || ''), at = X.stampMs(e.at);
    return h('div', { cls: 'saa-event', 'data-kind': kind, role: 'status', translate: 'no' },
      h('div', { cls: 'saa-event-head' }, icon(kind === 'completed' ? 'check' : 'alert'), h('b', null, X.eventTitle(e)), h('span', null, fin(at) ? X.clockText(at, Date.now()) : ''),
        h('button', { cls: 'saa-event-x', type: 'button', title: say('Close this message'), 'aria-label': say('Close this message'), onclick: () => close(e) }, icon('close'))),
      e.name ? h('div', { cls: 'saa-event-name' }, String(e.name)) : null,
      X.eventText(e, inChinese()) ? h('p', null, X.eventText(e, inChinese())) : null,
      e.runId && e.instrument ? h('div', { cls: 'saa-event-foot' }, dataButton(say('Open'), () => { close(e); openRecorded(e.instrument, e.runId, { name: e.name }); }, 'sm')) : null);
  }));
}
/** A short two-note chime (no sound file: the browser makes it). Silent where the browser does not allow sound. */
function beep() {
  try {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    const ctx = new Ctx(), t0 = ctx.currentTime;
    [[880, 0], [1175, 0.18]].forEach(([f, d]) => {
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.type = 'sine'; o.frequency.value = f;
      g.gain.setValueAtTime(0.0001, t0 + d); g.gain.exponentialRampToValueAtTime(0.18, t0 + d + 0.02); g.gain.exponentialRampToValueAtTime(0.0001, t0 + d + 0.32);
      o.connect(g); g.connect(ctx.destination); o.start(t0 + d); o.stop(t0 + d + 0.35);
    });
    setTimeout(() => { try { ctx.close(); } catch (err) { /* gone */ } }, 1200);
  } catch (e) { /* no sound here */ }
}

/* ---------- files handed over by a second start of the program (--open): "openQueue" of the state ---------- */
const queued = new Set();
function openQueued() {
  if (online || !state || !Array.isArray(state.openQueue)) return;
  for (const item of state.openQueue) {
    if (!item || !item.id || queued.has(item.id)) continue;
    queued.add(item.id);
    api('/api/pk-file?id=' + enc(item.id), { seconds: 60 }).then(answer => openPk(Object.assign({ name: item.name, path: item.path }, answer)))
      .catch(e => { if (e.status !== 404) toast(say('{0} could not be opened: {1}', item.name || item.path || '', e.message), 8000); });
  }
}

/* ---------- an instrument in the side bar ---------- */
function makeRow(key) {
  const r = { key };
  r.el = h('div', { cls: 'saa-reader' },
    h('button', { cls: 'saa-reader-main', type: 'button', title: en('Show the curves of this instrument'), onclick: () => openReader(key) },
      r.badge = h('span', { cls: 'saa-badge', translate: 'no' }),
      h('span', { cls: 'saa-reader-text', translate: 'no' }, r.name = h('b'), r.sub = h('span')),
      r.bar = h('span', { cls: 'saa-bar' }, r.fill = h('i'))));
  return r;
}
function updateRow(r, inst) {
  const run = inst.run, measuring = inst.state === 'measuring', timing = run ? X.runTiming(run, measuring, Date.now()) : null;
  setAttr(r.el, 'data-s', inst.state);
  setAttr(r.el, 'data-early', X.endedEarly(inst) ? '1' : '0');
  setText(r.badge, badge(inst.key));
  setText(r.name, title(inst));
  // the same figure as the tile, in little room: the cycle the run is in while measuring, else what was completed
  const cycles = !run ? '' : measuring ? X.cycleText(Math.max(run.cyclesStarted || 0, run.cyclesComplete || 0, 1), run.plannedCycles)
    : X.cyclesText(run.cyclesComplete, run.plannedCycles);
  setText(r.sub, [X.stateText(inst), cycles].filter(Boolean).join(' · '));
  show(r.bar, !!(timing && fin(timing.fraction) && measuring));
  if (timing && fin(timing.fraction)) r.fill.style.width = (timing.fraction * 100).toFixed(1) + '%';
}

/* ---------- an instrument in the overview ---------- */
function makeTile(key) {
  const t = { key, inst: null, channel: 0, sig: '', plateSig: '', seen: null, fresh: 0, freshAt: 0, at: null, dragging: null };
  const stat = (label, cls) => { const name = h('span', null, label), value = h('b', { translate: 'no' }), note = h('em', { translate: 'no' }); const el = h('div', { cls: 'saa-stat' + (cls ? ' ' + cls : '') }, name, value, note); return { el, name, value, note }; };
  t.auto = h('input', { type: 'checkbox', onchange: () => { prefs.auto[key] = t.auto.checked; savePrefs(); kick(); } });
  t.liveButton = dataButton('', () => { feed.load(key, t.inst.run.id); kick(); }, 'sm');
  t.wells = stat(en('Wells positive'), 'pos'); t.samples = stat(en('Groups positive'), 'pos'); t.first = stat(en('Earliest rise'));
  t.canvas = h('canvas');
  t.plate = h('canvas');
  t.el = h('article', { cls: 'saa-tile' },
    h('header', { cls: 'saa-tile-head' },
      t.badge = h('span', { cls: 'saa-badge', translate: 'no' }),
      h('div', { cls: 'saa-tile-name', translate: 'no' }, t.name = h('b'), t.sub = h('span')),
      h('div', { cls: 'saa-tile-side', translate: 'no' }, t.state = h('span', { cls: 'saa-state' }), t.temp = h('span', { cls: 'saa-temp' }))),
    t.run = h('div', { cls: 'saa-run', translate: 'no' },
      h('div', { cls: 'saa-proto' }, t.protocol = h('b'), t.filters = h('span'), t.channels = h('div', { cls: 'saa-channels seg sm', hidden: true })),
      t.progress = h('div', { cls: 'saa-progress' }, t.cycles = h('b'), t.cycleNote = h('span'), h('span', { cls: 'spacer' }), t.percent = h('span')),
      t.bar = h('div', { cls: 'saa-bar' }, t.fill = h('i'), t.knob = h('span', { cls: 'saa-knob', tabindex: '0', role: 'slider', hidden: true })),
      t.times = h('div', { cls: 'saa-times' }),
      t.op = h('div', { cls: 'saa-op', hidden: true }, icon('person'), t.opName = h('span')),
      (t.protocolBox = protocolBox('saa-pd-tile')).el),
    t.message = h('div', { cls: 'saa-warn', translate: 'no' }),
    t.idle = h('div', { cls: 'saa-idle' },
      t.idleText = h('p', { translate: 'no' }),
      t.next = h('div', { cls: 'saa-next', hidden: true }, t.nextText = h('span', { translate: 'no' }), button(en('Change'), () => go('settings'), 'sm ghost')),
      t.last = h('div', { cls: 'saa-last' })),
    t.chartBox = h('div', { cls: 'saa-chart' },
      h('button', { cls: 'saa-chart-hit', type: 'button', title: en('Open the curves of this run in Analysis'), 'aria-label': en('Open in Analysis'), onclick: () => openReader(key) }, t.canvas),
      t.empty = h('div', { cls: 'saa-chart-empty' }, t.emptyText = h('span', { translate: 'no' }))),
    t.stats = h('div', { cls: 'saa-stats' }, t.wells.el, t.samples.el, t.first.el),
    t.detail = h('div', { cls: 'saa-detail' },
      h('div', { cls: 'saa-plate' }, t.plate, h('div', { cls: 'saa-legend' }, h('span', { cls: 'p' }, en('Positive')), h('span', { cls: 'n' }, en('No rise')), h('span', { cls: 'e' }, en('No data')))),
      t.groups = h('div', { cls: 'saa-samples', translate: 'no' })),
    t.live = h('div', { cls: 'hint saa-live' }, t.liveText = h('span', { translate: 'no' })),
    h('footer', { cls: 'saa-tile-foot' },
      t.open = button(en('View analysis'), () => openReader(key), 'primary'),
      h('span', { cls: 'spacer' }),
      t.autoBox = h('label', { cls: 'chk', title: en('When this is off, new data of this instrument does not go into the analysis page by itself') }, t.auto, en('Load curves automatically'))));
  setAttr(t.op, 'title', en('Operator'));
  if (window.ResizeObserver) new ResizeObserver(() => { t.sig = t.plateSig = ''; drawTile(t); drawPlate(t); }).observe(t.el);
  wireLookBack(t);
  return t;
}
/** Looking back in the run of a tile: press or drag along its progress bar, or the keys on its dot (left / right a
    reading, Page Up / Down ten, Home the first, End or Esc live). The tile then shows the run as it was then; at the
    right end it is live again. It only looks: no setting changes. */
function wireLookBack(t) {
  const planned = () => (t.inst && t.inst.run ? t.inst.run.plannedCycles : 0);
  const set = at => { if (at === t.at) return; t.at = at; if (t.inst) updateTile(t, t.inst); };
  const place = e => { const r = t.bar.getBoundingClientRect(); return r.width ? Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)) : 1; };
  t.bar.addEventListener('pointerdown', e => {
    if (e.button !== 0 || !t.ds || !t.ds.t || t.ds.t.length < 2) return;
    e.preventDefault();
    t.dragging = e.pointerId;
    try { t.bar.setPointerCapture(e.pointerId); } catch (err) { /* ignore */ }
    set(X.lookBackAt(t.ds, place(e), planned()));
  });
  t.bar.addEventListener('pointermove', e => { if (t.dragging === e.pointerId && t.ds) set(X.lookBackAt(t.ds, place(e), planned())); });
  const end = e => { if (t.dragging === e.pointerId) t.dragging = null; };
  t.bar.addEventListener('pointerup', end);
  t.bar.addEventListener('pointercancel', end);
  t.knob.addEventListener('keydown', e => {
    if (!t.ds || !t.ds.t || t.ds.t.length < 2) return;
    const step = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1, PageDown: -10, PageUp: 10 }[e.key];
    if (step != null) set(X.lookBackStep(t.ds, t.at, step));
    else if (e.key === 'Home') set(X.lookBackAt(t.ds, 0, planned()));
    else if (e.key === 'End' || (e.key === 'Escape' && t.at != null)) set(null);
    else return;
    e.preventDefault();
    e.stopPropagation();
  });
}
function updateTile(t, inst) {
  const run = inst.run, now = Date.now(), measuring = inst.state === 'measuring', software = X.softwareOf(inst);
  t.inst = inst;
  setAttr(t.el, 'data-s', inst.state);
  setAttr(t.el, 'data-early', X.endedEarly(inst) ? '1' : '0');
  setText(t.badge, badge(inst.key));
  setText(t.name, title(inst));
  setText(t.sub, subtitle(inst));
  const version = inst.version ? say('{0} software version {1}', software, inst.version) : '';
  if (t.sub.title !== version) t.sub.title = version;
  setText(t.state, X.stateText(inst));
  setText(t.temp, X.temperatureText(inst));

  // the curves of the run and what the analysis says about them
  const count = run ? channelsOf(run.id) : 0;
  if (t.channel >= count) t.channel = 0;
  const ds = run ? datasetOf(run.id, t.channel) : null, parts = X.tileParts(inst, !!ds);
  const liveNow = X.tileLive(run && feed ? feed.describe(run.id) : { text: '', button: '' }, !!ds, !!feed);
  if (!ds || !t.ds || ds.id !== t.ds.id) t.at = null;            // another run or channel: live again
  t.ds = ds;

  show(t.run, parts.run);
  let stale = '';
  if (run) {
    const timing = X.runTiming(run, measuring, now), progress = X.progressParts(inst);
    setText(t.protocol, X.runName(run.protocol));
    setText(t.filters, [run.excitation && run.emission ? run.excitation + ' / ' + run.emission : run.excitation || '', run.gain != null ? say('Gain {0}', run.gain) : ''].filter(Boolean).join(' · '));
    setText(t.cycles, progress.main);
    setText(t.cycleNote, progress.note);
    const looking = t.at != null && !!ds;
    setText(t.percent, looking ? say('Looking back: {0}', X.timeText(t.at, app ? app.S.view.timeUnit : 'h')) : fin(timing.fraction) ? Math.floor(timing.fraction * 100) + '%' : '');
    t.percent.classList.toggle('look', looking);
    t.fill.style.width = fin(timing.fraction) ? (timing.fraction * 100).toFixed(1) + '%' : '0';
    // the dot: the moment the tile shows (the newest reading while live); only with the run's curves on the page
    const canLook = !!(ds && ds.t && ds.t.length > 1);
    show(t.knob, canLook);
    setAttr(t.bar, 'title', canLook ? say('Look back in this run: drag the dot; at the right end it is live again') : '');
    setAttr(t.knob, 'aria-label', say('Look back in this run'));
    t.bar.classList.toggle('look', canLook);
    if (canLook) t.knob.style.left = (X.lookBackPlace(ds, t.at, run.plannedCycles) * 100).toFixed(2) + '%';
    if (looking) setAttr(t.knob, 'aria-valuetext', X.timeText(t.at, app ? app.S.view.timeUnit : 'h')); else t.knob.removeAttribute('aria-valuetext');
    setAttr(t.el, 'data-look', looking ? '1' : '0');
    setText(t.times, X.timesText(inst, now));
    const lastData = X.stampMs(run.updatedAt), tip = fin(lastData) ? say('Latest data: {0}', X.clockText(lastData, now)) : '';
    if (t.progress.title !== tip) t.progress.title = tip;
    // a run that should be going but has brought nothing for two cycles: say so
    const quiet = measuring ? X.quietSeconds(run, now) : 0;
    if (quiet) stale = say('No new data for {0}: please check the instrument and the {1} software.', X.spanText(quiet), software);
    show(t.channels, count > 1);
    if (count > 1 && t.channels.childElementCount !== count)
      t.channels.replaceChildren(...Array.from({ length: count }, (_, i) => h('button', { type: 'button', onclick: () => { t.channel = i; t.sig = t.plateSig = ''; render(); } }, say('Channel {0}', i + 1))));
    [...t.channels.children].forEach((b, i) => b.classList.toggle('on', i === t.channel));
    const operator = run.operator ? String(run.operator) : '';
    setText(t.opName, operator);
    show(t.op, !!operator);
    // how the run was set up: once the program has said something about it (an older program says nothing)
    show(t.protocolBox.el, protocols.has(run.id));
    if (protocols.has(run.id)) renderProtocol(t.protocolBox, protocols.get(run.id));
  }
  // what the instrument's problems, its run's end and its software say: a line each (the style sheet keeps the breaks)
  const message = [inst.state === 'finished' ? X.interruptionLine(run) : '', tr(inst.message), stale, ...X.instrumentProblems(state, inst.key, inChinese())].filter(Boolean).join(LF);
  setText(t.message, message);
  show(t.message, !!message);

  let sum = null;
  if (ds && app.ensureAnalysis) {
    try { sum = X.runSummary(ds, app.ensureAnalysis(ds), (app.setOf ? app.setOf(ds) : app.S.set).grpPosPct, spec => (app.resolveColor ? app.resolveColor(spec, dark()) : null), t.at); }
    catch (e) { sum = null; }
  }
  if (sum && t.at == null) {
    // wells that turned positive with the newest cycle (not those a changed threshold made positive)
    const mark = ds.id + '|' + app.S.setVer;
    if (t.seen && t.seen.mark === mark && ds.t.length > t.seen.cycles) { t.fresh = X.newlyPositive(t.seen.ids, sum.positiveIds).length; t.freshAt = ds.t.length; }
    else if (!t.seen || t.seen.mark !== mark) t.fresh = 0;
    t.seen = { mark, cycles: ds.t.length, ids: sum.positiveIds };
  }
  const unit = app ? app.S.view.timeUnit : 'h';
  setText(t.wells.value, sum ? sum.positive + ' / ' + sum.wells : '—');
  setText(t.wells.note, sum && t.at == null && t.fresh && t.freshAt === ds.t.length && measuring ? say('+{0} this cycle', t.fresh) : '');
  t.wells.el.classList.toggle('some', !!(sum && sum.positive));
  setText(t.samples.value, sum && sum.groups.length ? sum.groupsPositive + ' / ' + sum.groups.length : '—');
  setText(t.samples.note, sum && !sum.groups.length ? say('No groups yet') : '');
  t.samples.el.classList.toggle('some', !!(sum && sum.groupsPositive));
  setText(t.first.value, sum ? X.timeText(sum.firstTime, unit) : '—');
  setText(t.first.note, sum && !sum.positive ? say('No rise yet') : '');
  renderGroups(t, sum, unit);

  show(t.idle, parts.idle);
  if (parts.idle) {
    setText(t.idleText, say('Idle. Start a run in the {0} software: its curves appear here by themselves.', software));
    const next = settings && !online ? X.nextText(settings.nextExperiment[inst.key], layoutList) : '';
    setText(t.nextText, next ? say('Next experiment: {0}', next) : '');
    show(t.next, !!next);
    const last = (recent || []).find(r => r.key === inst.key), sig = last ? last.id : '';
    if (t.lastSig !== sig) {
      t.lastSig = sig;
      t.last.replaceChildren(...(last ? [h('span', null, en('Last run')), runRow(last)] : []));
    }
    show(t.last, !!last);
  }
  show(t.chartBox, parts.chart);
  t.chartBox.classList.toggle('none', !ds);
  show(t.empty, !ds);
  setText(t.emptyText, liveNow.empty);
  // the button of the curves (Reload / Load the latest curves) sits where its text is: in the empty chart, or under the numbers
  const home = ds ? t.live : t.empty;
  if (t.liveButton.parentNode !== home) home.appendChild(t.liveButton);
  setText(t.liveButton, liveNow.button);
  show(t.liveButton, !!liveNow.button);
  setText(t.liveText, liveNow.note);
  show(t.live, !!ds && !!(liveNow.note || liveNow.button));
  show(t.stats, parts.numbers);
  show(t.detail, parts.numbers);
  show(t.open, parts.open);
  show(t.autoBox, !!feed && parts.auto);
  t.auto.checked = autoOn(inst.key);
  drawTile(t);
  drawPlate(t);
}
/** The groups (replicate wells of a sample) of a run that have turned positive, the one that turned last on top. */
function renderGroups(t, sum, unit) {
  const list = sum ? sum.groups : [], sig = unit + '|' + (sum ? sum.wells : '') + '|' + list.map(g => [g.id, g.name, g.color, g.positive, g.wells, g.call, g.time].join()).join('|');
  if (t.groupSig === sig) return;
  t.groupSig = sig;
  if (!list.length) {
    t.groups.replaceChildren(h('p', { cls: 'hint' }, sum ? say('Select replicate wells in “Analysis” and group them (or click “Group by name”): the groups that turned positive are then shown here.') : ''));
    return;
  }
  const MAX = 5, positive = list.filter(g => g.call).sort((a, b) => (fin(b.time) ? b.time : -1) - (fin(a.time) ? a.time : -1));
  const row = g => h('div', { cls: 'saa-sample', title: say('{0}: {1} / {2} wells positive', g.name, g.positive, g.wells) },
    h('i', { style: g.color ? 'background:' + g.color : null }), h('span', null, g.name || say('Unnamed')), h('b', null, g.positive + '/' + g.wells), h('em', null, X.timeText(g.time, unit).replace(/ [a-z]+$/, '')));
  t.groups.replaceChildren(...[
    h('div', { cls: 'saa-samples-head' }, positive.length ? [h('span', null, say('Latest positive groups')), h('span', null, say('Positive at ({0})', unit === 'min' ? 'min' : 'h'))] : say('No group has turned positive yet')),
    ...positive.slice(0, MAX).map(row),
    positive.length > MAX ? h('div', { cls: 'hint' }, say('{0} more: see all of them in Analysis', positive.length - MAX)) : null].filter(Boolean));
}

/* ---------- the small chart and the small plate of a tile ---------- */
const dark = () => (app && app.isDark ? app.isDark() : !!(window.matchMedia && matchMedia('(prefers-color-scheme: dark)').matches));
const cssColor = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
function canvasFor(c, w, hgt) {
  const ratio = window.devicePixelRatio || 1, g = c.getContext('2d');
  c.width = Math.round(w * ratio); c.height = Math.round(hgt * ratio);
  g.setTransform(ratio, 0, 0, ratio, 0, 0);
  g.clearRect(0, 0, w, hgt);
  return g;
}
function drawTile(t) {
  const ds = t.ds, box = t.chartBox, w = box.clientWidth, hgt = box.clientHeight;
  if (!ds || !w || !hgt) { if (!ds) t.sig = ''; return; }
  const unit = app.S.view.timeUnit === 'min' ? 'min' : 'h';
  const sig = [ds.id, ds.ver, app.S.setVer, ds.t.length, w, hgt, window.devicePixelRatio, dark(), unit, t.at].join('|');
  if (sig === t.sig) return;
  t.sig = sig;
  const m = X.miniCurves(ds, ds.res, Math.max(60, Math.floor(w / 2)), null, t.at);
  const g = canvasFor(t.canvas, w, hgt);
  const pad = { l: 40, r: 10, t: 10, b: 22 }, pw = w - pad.l - pad.r, ph = hgt - pad.t - pad.b;
  const lo = Math.min(0, m.yMin), span = (m.yMax - lo) * 1.06, ySteps = X.axisSteps(span, 4), top = Math.max(span, ySteps[ySteps.length - 1] || 1);
  const tf = unit === 'min' ? 60 : 1, tMax = m.tMax > 0 ? m.tMax : 1, xSteps = X.axisSteps(tMax * tf, 4);
  const sx = v => pad.l + (v / tMax) * pw, sy = v => pad.t + ph - ((v - lo) / top) * ph;
  g.font = '10.5px ' + (cssColor('--font-mono') || 'monospace');
  g.lineWidth = 1;
  g.strokeStyle = cssColor('--grid'); g.fillStyle = cssColor('--muted');
  g.textAlign = 'right'; g.textBaseline = 'middle';
  for (const v of ySteps) {
    const y = Math.round(sy(lo + v)) + 0.5;
    g.beginPath(); g.moveTo(pad.l, y); g.lineTo(w - pad.r, y); g.stroke();
    g.fillText(X.shortNumber(lo + v), pad.l - 6, y);
  }
  g.textAlign = 'center'; g.textBaseline = 'top';
  for (const v of xSteps) if (sx(v / tf) < w - pad.r - 18) g.fillText(String(v), sx(v / tf), pad.t + ph + 6);
  g.textAlign = 'right';
  g.fillText(unit, w - pad.r, pad.t + ph + 6);
  const neutral = cssColor('--muted'), positive = cssColor('--pos');
  g.lineJoin = 'round';
  for (const curve of m.curves) {
    g.beginPath();
    let pen = false;
    curve.y.forEach((v, i) => {
      if (!fin(v)) { pen = false; return; }
      if (pen) g.lineTo(sx(m.t[i]), sy(v)); else g.moveTo(sx(m.t[i]), sy(v));
      pen = true;
    });
    g.strokeStyle = curve.positive ? positive : neutral;
    g.globalAlpha = curve.positive ? 0.85 : 0.45;
    g.lineWidth = curve.positive ? 1.2 : 1;
    g.stroke();
  }
  g.globalAlpha = 1;
  if (t.at != null && fin(t.at) && m.tMax > 0) {          // looking back: a line at that moment, what came after it veiled
    const x = Math.round(sx(Math.min(t.at, tMax))) + 0.5;
    g.globalAlpha = 0.6;
    g.fillStyle = cssColor('--surface');
    g.fillRect(x, pad.t, Math.max(0, w - pad.r - x), ph);
    g.globalAlpha = 1;
    g.strokeStyle = cssColor('--ink-2');
    g.lineWidth = 1;
    g.beginPath(); g.moveTo(x, pad.t); g.lineTo(x, pad.t + ph); g.stroke();
  }
}
/** One square per well: red = positive, grey = measured and not positive, outline = nothing measured there. */
function drawPlate(t) {
  const ds = t.ds, c = t.plate, box = c.parentNode;
  if (!ds || !box.clientWidth) { if (!ds) t.plateSig = ''; return; }
  const rowsN = ds.plate.rows, cols = ds.plate.cols, res = ds.res || {};
  const cell = Math.max(4, Math.min(22, Math.floor(Math.min(box.clientWidth, 184) / cols))), gap = cell >= 12 ? 3 : cell >= 8 ? 2 : 1;
  const w = cols * cell, hgt = rowsN * cell;
  const calls = ds.order.map(id => { const well = ds.wells[id], r = res[id]; return !well || !well.y || well.excluded || !r || !r.n ? 0 : r.positive && (t.at == null || (fin(r.ttt) && r.ttt <= t.at + 1e-9)) ? 2 : 1; });
  const sig = [ds.id, cell, window.devicePixelRatio, dark(), calls.join('')].join('|');
  if (sig === t.plateSig) return;
  t.plateSig = sig;
  c.style.width = w + 'px'; c.style.height = hgt + 'px';
  const g = canvasFor(c, w, hgt), colours = [cssColor('--hair-2'), cssColor('--muted'), cssColor('--pos')], radius = cell >= 12 ? 3 : cell >= 8 ? 2 : 1;
  ds.order.forEach((id, i) => {
    const well = ds.wells[id];
    if (!well) return;
    const x = well.c * cell + gap / 2, y = well.r * cell + gap / 2, size = cell - gap;
    g.beginPath();
    if (calls[i]) {
      if (g.roundRect) g.roundRect(x, y, size, size, radius); else g.rect(x, y, size, size);
      g.fillStyle = colours[calls[i]]; g.globalAlpha = calls[i] === 2 ? 1 : 0.5; g.fill();
    } else {
      g.globalAlpha = 1; g.lineWidth = 1; g.strokeStyle = colours[0];
      g.rect(Math.round(x) + 0.5, Math.round(y) + 0.5, Math.max(1, size - 1), Math.max(1, size - 1)); g.stroke();
    }
  });
  g.globalAlpha = 1;
}

/* ---------- recorded runs under the tiles ---------- */
async function loadRecent() {
  recentAt = Date.now();
  try { recent = await api('/api/runs'); } catch (e) { return; }
  if (!Array.isArray(recent)) recent = [];
  for (const r of recent) if (r && r.analysisRev !== undefined) sync.remote(r.id, r.analysisRev);
  render();
  renderRecent();
}
function renderRecent() {
  if (!recent) return;
  const list = recent.slice(0, 6);
  show(recentBox, list.length > 0);
  recentBox.replaceChildren(
    h('div', { cls: 'saa-recent-head' }, h('h2', null, en('Recent runs')), h('span', { cls: 'spacer' }),
      recent.length > list.length ? dataButton(say('All {0} runs', recent.length), () => ((online && !editor()) || mode !== 'shell' ? openHistory() : go('experiments')), 'sm ghost') : null),
    h('div', { cls: 'saa-runs' }, list.map(r => runRow(r))));
}
/** A recorded run with its Open button (online also its .pk and Excel file). The analysis view comes to the front once
    the run really is on the page (a run with fewer than two cycles cannot be shown: the feed says so and the view stays). */
function runRow(r, close) {
  const open = button(en('Open'), () => {
    if (typeof close === 'function') close();
    openRecorded(r.key, r.id, { label: r.label, protocol: r.protocol, startedAt: r.startedAt });
  }, 'sm');
  open.disabled = !feed;
  return h('div', { cls: 'saa-runrow' },
    h('span', { cls: 'saa-badge', translate: 'no' }, badge(r.key)),
    h('div', { translate: 'no' }, h('b', null, r.protocol ? X.runName(r.protocol) : '—'), h('span', { cls: 'hint' }, [X.reader(r.key).name, (r.startedAt || '').replace('T', ' ').slice(0, 16), X.recordCycles(r, readers())].filter(Boolean).join(' · '))),
    open, downloadButtons(r.id));
}
/** A run the program recorded, through the feed (live while it is measured); its saved analysis follows. */
function openRecorded(key, id, info) {
  if (!feed) { toast(say('This page cannot show curves')); return; }
  feed.load(key, id, info && (info.protocol || info.label) ? info : null, () => go('analysis'));
  kick();
}
async function openHistory() {
  const list = h('div', { cls: 'saa-runs' }, h('p', { translate: 'no' }, say('Loading…')));
  const close = dialog(en('History'), list, shut => [button(en('Close'), shut)]);
  try {
    const runs = await api('/api/runs');
    list.replaceChildren(...runs.map(r => runRow(r, close)));
    if (!runs.length) list.replaceChildren(h('p', { translate: 'no' }, say('No run has been recorded yet')));
  } catch (e) { list.replaceChildren(h('p', { cls: 'saa-err', translate: 'no' }, e.message)); }
}

/* ===================== the experiment in the analysis view: status, operator, its annotation ===================== */
/* Each run of an instrument is an experiment with a .pk file (and an .xlsx) in the Experiments folder. What the user
   does to it in the analysis - names, groups, notes, settings, the operator - is its annotation, one for every device
   that shows the experiment. It is saved by itself a few seconds after a change (Save, Ctrl+S: at once): POST
   /api/analysis, or, for a .pk opened from outside the Experiments folder, by the program's window itself ({cmd:
   'saveAnalysisTo'}). When an experiment is opened its saved annotation is put in (GET /api/analysis, or the one handed
   over with the file); without one, the sample names of Omega's plate layout ("omegaLayout"). The rules - when it is
   saved, what comes from the instrument, the status chip - are web/live.js's createSync(); here they are wired to the
   page, the program and the window. Online the annotation can be changed too: signed in, it is saved on the instrument
   PC (the online server forwards that save and nothing else); not signed in, it stays in this browser. Nothing else
   can be changed online. */
const given = new Map();           // dataset id of a run that is being opened -> the analysis that came with its file (or null)
/** Online: whether this device is signed in for changing annotations (online.js publishes it as window.SAAOnline.me). */
const editor = () => !!(online && window.SAAOnline && window.SAAOnline.me && window.SAAOnline.me.editor === true);
/** A .pk opened from outside the Experiments folder, saved by the program's window into that file. */
const fileOnly = x => !online && !!host() && !!x.path && !X.inFolder(x.path, experimentsFolder());
const sync = X.createSync({
  now: () => Date.now(),
  look: dsId => {
    if (!app || typeof app.getDatasetAnalysis !== 'function' || !dsById(dsId)) return null;
    try { const a = app.getDatasetAnalysis(dsId); return a ? { sig: X.analysisSig(a, '', true), analysis: a } : null; } catch (e) { return null; }
  },
  apply: (dsId, analysis, undo) => app.applyDatasetAnalysis(dsId, analysis, { undo }),
  datasets: runId => (app ? app.S.ds.filter(d => realRunId(d.id) === runId).map(d => d.id) : []),
  online: () => online,
  canSave: () => (online ? editor() : !!app && typeof app.getDatasetAnalysis === 'function'),
  fileOnly,
  canBook: x => x.bookMode === 'program' || !!x.file,
  extra: (x, dsId) => {
    const info = hostInfo();
    return { savedBy: !online && info.user ? info.user + (info.pc ? ' (' + info.pc + ')' : '') : '', operator: operatorOf(x), channel: X.runOfDataset(dsId).channel + 1 };
  },
  save: saveAnnotation,
  savedText: x => (fileOnly(x) ? say('Saved into {0}', x.path.split(/[\\/]/).pop())
    : x.path && !X.inFolder(x.path, experimentsFolder()) ? say('Saved into this program\'s own file of the experiment: {0}', String(x.file || '').split('/').pop()) : ''),
  load: x => api('/api/analysis?runId=' + enc(x.runId), { seconds: 30 }),
  revert: x => api('/api/analysis/revert?runId=' + enc(x.runId), { method: 'POST', seconds: 60 }),
  // the workbook, made by the program ("now": Ctrl+S, "leave": the experiment was left or closed)
  workbook: (x, o) => api('/api/analysis/xlsx?runId=' + enc(x.runId) + '&xlsx=' + (o.how === 'now' ? 'now' : 'leave'), { method: 'POST', seconds: 120, keepalive: o.keepalive }),
  file: (x, file) => { if (file) { x.file = String(file); tellCurrent(); } },
  store: { get: dsId => prefs.sync[dsId] || null, set: (dsId, v) => { if (v) prefs.sync[dsId] = v; else delete prefs.sync[dsId]; savePrefsSoon(); } },
  busy: () => !!busy(),
  toast,
  changed: () => { stripSig = ''; renderStrip(); },
  signIn: () => { if (window.SAAOnline && typeof window.SAAOnline.signIn === 'function') window.SAAOnline.signIn(); },
  signedOut: () => { if (window.SAAOnline && typeof window.SAAOnline.refresh === 'function') window.SAAOnline.refresh(); }
});
const expFor = runId => sync.exp(runId, { file: null, path: prefs.paths[runId] || null, info: {} });
/** The run id of a dataset id ("Omega2-20260120-093000-ch2" -> "Omega2-20260120-093000"; a dataset made safe from an
    odd run id has it in prefs.runIds). */
function realRunId(dsId) {
  const base = X.runOfDataset(dsId).runId;
  return prefs.runIds[base] || base;
}
/** The experiment of a dataset (null: an imported table, an example, a run of Omega's own database). */
function expOf(ds) {
  if (!ds || typeof ds.id !== 'string') return null;
  const runId = realRunId(ds.id), known = sync.known(runId);
  if (known) return known;
  if (!X.isRunId(runId) && !readers().some(i => i.run && i.run.id === runId)) return null;
  return expFor(runId);
}
/** A dataset of a run was just made by the feed: its saved annotation goes in, or the names of Omega's plate layout. */
function created(ds, c) {
  const realId = c.run && c.run.id ? String(c.run.id) : c.runId;
  if (realId !== c.runId) { prefs.runIds[c.runId] = realId; savePrefs(); }
  const x = expFor(realId), handed = given.has(c.runId) ? given.get(c.runId) : undefined;
  prefs.seen[ds.id] = Date.now();                     // opened now: not folded away in the data list for a week
  savePrefsSoon();
  sync.opening(x, ds.id);                             // nothing is saved from it before its annotation is in
  restoreInto(ds.id, x, c.run, handed);
}
async function restoreInto(dsId, x, run, handed) {
  let edited = false;
  try {
    if (handed !== undefined) {
      // the annotation in the file: the version a file outside Experiments is saved over, else the first known
      if (handed && typeof handed === 'object' && (fileOnly(x) || !x.saved)) x.saved = handed;
      if (!online) sync.load(x);                      // what the program has: its place in Experiments, its newest version
    } else await sync.load(x);
    const ds = dsById(dsId);
    if (!ds || !app || typeof app.applyDatasetAnalysis !== 'function') return;
    // the user has begun to work on it: what is there stays
    if (ds.undo && ds.undo.some(s => !String(s).startsWith('{"k":"set"'))) { edited = true; return; }
    const analysis = handed !== undefined ? handed : x.saved;
    try {
      if (analysis && typeof analysis === 'object') app.applyDatasetAnalysis(ds.id, analysis, { undo: false });
      else {
        const names = X.namesAnalysis(ds, run && run.omegaLayout);
        if (names) app.applyDatasetAnalysis(ds.id, names, { undo: false });
      }
    } catch (e) { toast(say('The saved analysis could not be put back: {0}', pageError(e)), 8000); }
  } finally {
    sync.opened(x, dsId, edited);
    stripSig = '';
    renderStrip();
  }
}
/** A message of the page's own functions in the language in use (a newer format has its own sentence here). */
const pageError = e => (/newer version of Plate Kinetics Live/.test(String(e && e.message)) ? say(X.PROBLEMS.newer) : tr(String((e && e.message) || e)));

/* ---------- how a run was set up: protocolDetails, as the program hands it over ---------- */
/* The program reads the settings of a run from the instrument software's files and gives them ready to show
   (web/live.js protocolView) with the run of /api/run, /api/pk and /api/pk-file (dotnet/protocol-details-contract.md;
   a run summary of /api/state or an experiment of /api/experiments that carries it is taken too). They are kept here
   by run id (null: the program knows nothing of it); shown collapsed under "Protocol details" beside the run's other
   information: on its tile (once known) and above its analysis ("No record" when nothing is known). */
const protocols = new Map();       // run id -> protocolDetails (an object, or null)
/** The program said how a run was set up (or that it does not know: null). */
function noteProtocol(runId, details) {
  if (!runId || details === undefined) return;
  const value = details && typeof details === 'object' ? details : null;
  if (protocols.has(runId) && X.stableJson(protocols.get(runId)) === X.stableJson(value)) return;
  protocols.set(runId, value);
  stripSig = '';
}
const protocolAsked = new Set();
/** An experiment in view whose curves were not fetched in this session (put back from the page's own saved session):
    its run is asked for once, for what the program says about its protocol. */
function askProtocol(runId) {
  if (protocols.has(runId) || protocolAsked.has(runId) || !X.isRunId(runId)) return;
  protocolAsked.add(runId);
  api('/api/run?key=' + enc(runId.replace(/-\d{8}-\d{6}$/, '')) + '&id=' + enc(runId), { seconds: 60 })
    .then(run => { noteProtocol(runId, run && typeof run === 'object' && run.protocolDetails !== undefined ? run.protocolDetails : null); renderStrip(); })
    .catch(() => { /* not known here (a file from elsewhere): "No record" */ });
}
/** A collapsed "Protocol details" (a <details> element: it opens on click and stays as the user left it). */
function protocolBox(cls) {
  const box = { sig: null };
  // (made with say(), under translate="no": on a tile it sits among the run's data, which the page's translator leaves alone)
  box.el = h('details', { cls: 'saa-pd ' + cls },
    box.summary = h('summary', { translate: 'no' }),
    box.body = h('div', { cls: 'saa-pd-body', translate: 'no' }));
  return box;
}
/** Fills a "Protocol details" with what the program said: its sections (label, value), for a PCR the temperature
    program one line per stage; "No record" when nothing is known. Every value is put in as text. */
function renderProtocol(box, details) {
  const zh = inChinese(), view = X.protocolView(details, zh), sig = (zh ? 'zh' : 'en') + JSON.stringify(view);
  setText(box.summary, say('Protocol details'));
  setAttr(box.summary, 'title', say('The settings of this experiment, as the instrument software recorded them'));
  if (box.sig === sig) return;
  box.sig = sig;
  if (!view) { box.body.replaceChildren(h('p', { cls: 'saa-pd-none' }, say('No record'))); return; }
  const section = (title, ...kids) => h('section', { cls: 'saa-pd-sec' }, title ? h('h4', null, title) : null, ...kids);
  const parts = view.sections.map(sec => section(sec.title, h('dl', null, ...sec.rows.flatMap(r => [h('dt', null, r.label), h('dd', null, r.value)]))));
  if (view.stages.length) {
    parts.push(section(say('Temperature program'), h('div', { cls: 'saa-pd-steps' }, ...view.stages.map(line => h('p', null, line)),
      view.ramp ? h('p', { cls: 'saa-pd-ramp' }, say('Ramp rate {0} for every step', view.ramp)) : null)));
  }
  box.body.replaceChildren(...parts);
}

let strip = null, stripSig = '', stripProtocol = null;
function buildStrip() {
  const s = strip = {};
  s.el = h('div', { cls: 'saa-exp', 'data-saa': true, hidden: true },
    s.state = h('span', { cls: 'saa-exp-state', translate: 'no' }),
    s.text = h('span', { cls: 'saa-exp-text', translate: 'no' }),
    s.opBox = h('span', { cls: 'saa-exp-op' }, icon('person'), h('span', { cls: 'saa-exp-label' }, en('Operator')),
      s.opShow = dataButton('', () => editOperator(), 'sm ghost'),
      s.opInput = h('input', { type: 'text', maxlength: '100', hidden: true, 'aria-label': en('Operator'),
        onkeydown: e => { if (e.key === 'Enter') { e.preventDefault(); s.opInput.blur(); } if (e.key === 'Escape') { e.preventDefault(); s.opInput.value = s.opInput.dataset.was || ''; s.opInput.blur(); } },
        onblur: () => commitOperator() })),
    h('span', { cls: 'spacer' }),
    // the annotation: the same as the instrument's, being saved, changed only here, or newer on the instrument
    s.sync = h('span', { cls: 'saa-sync', translate: 'no', role: 'status', hidden: true }, s.syncText = h('span')),
    s.saved = h('span', { cls: 'saa-exp-saved', translate: 'no' }),
    s.signin = button(en('Sign in'), () => sync.saveNow(expOf(app.curDS()), app.curDS().id), 'sm'),
    s.restore = button(en('Restore from instrument'), () => restoreExperiment(), 'sm'),
    s.undo = button(en('Undo last save'), () => undoLastSave(), 'sm ghost'),
    s.folder = button(en('Show in folder'), () => { const x = expOf(app.curDS()); if (x && x.file) openFolder(x.file); }, 'sm ghost'),
    s.export = button(en('Export current data'), () => { const x = expOf(app.curDS()); if (x) exportCurrent(x.runId, x.file, app.curDS().name); }, 'sm ghost'),
    s.downloads = h('span', { cls: 'saa-exp-dl', hidden: true }),         // online: the experiment's .pk and Excel file
    s.save = button(en('Save'), () => saveExperiment(), 'sm primary'));
  setAttr(s.save, 'title', en('Save the names, groups, notes and settings into the experiment file (Ctrl+S)'));
  setAttr(s.export, 'title', en('An Excel file of the data measured so far'));
  setAttr(s.restore, 'title', en('Drop the changes on this device and load the annotation saved in the experiment'));
  setAttr(s.undo, 'title', en('Bring the annotation back to how it was before the last save'));
  setAttr(s.signin, 'title', en('Sign in to save them on the instrument PC'));
  return s.el;
}
/** Who ran the experiment: as typed here, as saved, as the program recorded it, the default of the settings, the user. */
function operatorOf(x) {
  if (x.operator != null) return x.operator;
  if (x.saved && x.saved.operator) return String(x.saved.operator);
  const inst = readers().find(i => i.run && i.run.id === x.runId);
  if (inst && inst.run.operator) return String(inst.run.operator);
  if (x.info && x.info.operator) return String(x.info.operator);
  return (settings && settings.operatorDefault) || (state && state.operator ? String(state.operator) : '') || '';
}
function editOperator() {
  const x = expOf(app.curDS());
  if (!x || online) return;
  const s = strip;
  s.opInput.value = s.opInput.dataset.was = operatorOf(x);
  s.opInput.dataset.run = x.runId;                 // what is typed belongs to this experiment, whatever is shown later
  show(s.opShow, false); show(s.opInput, true);
  s.opInput.focus(); s.opInput.select();
}
function commitOperator() {
  const s = strip;
  if (s.opInput.hidden) return;
  const x = sync.known(s.opInput.dataset.run || '');
  if (x) { const v = s.opInput.value.trim(); if (v !== operatorOf(x)) x.operator = v; }
  show(s.opInput, false); show(s.opShow, true);
  stripSig = ''; renderStrip();
  syncSoon();
}
/** The strip above the analysis of an experiment: how it stands, who ran it, where its annotation stands (the chip:
    the same as the instrument's, being saved, changed only on this device, newer on the instrument), and what can be
    done about it. Online only the annotation can be changed: no operator, no files, no undo of a save. */
function renderStrip() {
  if (!strip || mode !== 'shell') return;
  const ds = app.curDS(), x = view === 'analysis' ? expOf(ds) : null;
  // another experiment in view while its operator was being typed: what was typed goes to the one it was typed for
  if (!strip.opInput.hidden && (!x || strip.opInput.dataset.run !== x.runId)) { commitOperator(); return; }
  if (pageSave) show(pageSave, !x);                      // the page's "Save project" is for imported tables; an experiment is saved into its file
  show(strip.el, !!x);
  if (stripProtocol) {
    show(stripProtocol.el, !!x);
    if (x) { askProtocol(x.runId); renderProtocol(stripProtocol, protocols.get(x.runId)); }
  }
  if (!x) return;
  if (!x.loaded && !x.loading && x.checkedAt === -Infinity && !fileOnly(x)) sync.load(x);
  const inst = readers().find(i => i.run && i.run.id === x.runId), live = !!(inst && inst.state === 'measuring');
  const status = live ? 'measuring' : inst && inst.run && inst.run.status ? inst.run.status : x.info && x.info.status ? x.info.status : '';
  const why = inst && inst.state === 'finished' ? X.interruptionLine(inst.run) : '';
  const st = sync.status(x, ds.id), words = X.syncTexts(st, { online, now: Date.now() });
  // Save: in the program's window always (Ctrl+S makes the workbook too); online for one who is signed in, when a change
  // did not get through or would go over a newer version
  const canSave = online ? editor() && (st.state === 'local' || st.state === 'newer') : typeof app.getDatasetAnalysis === 'function';
  const sig = [x.runId, status, why, operatorOf(x), st.state, st.reason, st.canRestore, st.canUndo, words.chip, words.note, x.file, live, canSave, editor(), !!x.inflight,
    document.documentElement.lang, inst ? X.progressText(inst) : ''].join('\u0001');
  if (sig === stripSig) return;
  stripSig = sig;
  setText(strip.state, status ? X.statusText(status) : '');
  setAttr(strip.state, 'data-status', status || 'none');
  show(strip.state, !!status);
  setText(strip.text, why || (live ? X.progressText(inst) : ''));
  setText(strip.opShow, operatorOf(x) || say('not given'));
  strip.opShow.disabled = online;                         // online the operator stays as it was recorded
  setText(strip.syncText, words.chip);
  setAttr(strip.sync, 'data-sync', st.state);
  setAttr(strip.sync, 'title', words.note || words.chip);
  show(strip.sync, !!words.chip);
  setText(strip.saved, words.note);
  setAttr(strip.saved, 'data-state', st.state !== 'local' ? 'ok' : st.reason === 'signin' || st.reason === 'offline' ? 'dirty' : 'error');
  show(strip.saved, !!words.note);
  show(strip.signin, online && !editor() && st.state === 'local');
  show(strip.restore, st.canRestore);
  show(strip.undo, st.canUndo);
  show(strip.folder, !online && !!x.file);
  show(strip.export, !online && live);
  if (strip.downloads.dataset.run !== x.runId) { strip.downloads.dataset.run = x.runId; strip.downloads.replaceChildren(...[downloadButtons(x.runId)].filter(Boolean)); }
  show(strip.downloads, online && downloadable(x.runId));
  show(strip.save, canSave);
  strip.save.disabled = !!x.inflight;
  const quiet = st.state === 'same' || st.state === 'none' || st.state === 'saving';
  strip.save.classList.toggle('ghost', quiet);
  strip.save.classList.toggle('primary', !quiet);
  tellCurrent();
}
/** Save, Ctrl+S: the annotation of the experiment in view at once, and its workbook; online and not signed in, the
    sign-in of the online view. */
function saveExperiment() {
  if (!app) return;
  const ds = app.curDS(), x = expOf(ds);
  if (!x) return;
  if (strip && !strip.opInput.hidden) commitOperator();
  sync.saveNow(x, ds.id);
}
/** Restore from instrument: the changes of this device go, the annotation saved in the experiment comes. */
function restoreExperiment() {
  const x = expOf(app.curDS());
  if (x) sync.restore(x);
}
/** Undo last save (the program's window): POST /api/analysis/revert, after a question. */
async function undoLastSave() {
  const ds = app.curDS(), x = expOf(ds);
  if (!x || online) return;
  const at = X.stampMs(x.savedAt), when = [fin(at) ? X.clockText(at, Date.now()) : '', x.savedBy].filter(Boolean).join(' · ') || '—';
  if (await confirmDialog(en('Undo last save'), [say('The annotation goes back to how it was before the last save ({0}).', when)], en('Undo last save'))) sync.undoSave(x);
}
/** An annotation saved: into the program's file of the experiment (POST /api/analysis; xlsx=now or xlsx=leave asks for
    its workbook too, an automatic save says nothing: the program makes it later), or, for a .pk opened from outside the
    Experiments folder, by the program's window into that file. A refusal that no second try would change carries the
    status 409. */
async function saveAnnotation(x, analysis, o) {
  const refused = text => Object.assign(new Error(text), { status: 409 });
  if (fileOnly(x)) {
    const answer = await askHost({ cmd: 'saveAnalysisTo', path: x.path, analysis }, 'analysisSaved', x.path, 60);
    if (!answer || !answer.ok) throw refused(tr(String((answer && answer.error) || '')) || say('The window could not save the file'));
    return { savedAt: answer.savedAt || X.localStamp(Date.now()) };
  }
  if (!online && x.path && !x.file && !X.inFolder(x.path, experimentsFolder()))
    throw refused(say('This file lies outside the Experiments folder and was not recorded on this PC: it can only be saved into in the program\'s window'));
  try { return await api('/api/analysis?runId=' + enc(x.runId) + (o.xlsx ? '&xlsx=' + o.xlsx : ''), { method: 'POST', body: { analysis }, seconds: 120, keepalive: o.keepalive }); }
  catch (e) { if (e.status === 404) e.message = say('this experiment was not recorded by this program'); throw e; }
}
/** The annotation of the experiment in view looked at now (and every second): web/live.js's createSync().tick(). */
let syncTimer = 0;
function syncNow() {
  clearTimeout(syncTimer);
  syncTimer = 0;
  if (mode !== 'shell' || !app || app.S.restoring || app.S.restorePending) return;
  let current = null;
  if (view === 'analysis') {
    const ds = app.curDS(), x = expOf(ds);
    if (x) current = { x, dsId: ds.id, visible: document.visibilityState !== 'hidden' };
  }
  try { sync.tick(current); } catch (e) { console.error(e); }
  renderStrip();
}
/** After something the user did: looked at shortly (the page has done its part by then). */
const syncSoon = () => { if (!syncTimer) syncTimer = setTimeout(syncNow, 250); };
/** The current data of a run that is still measuring as an Excel file (GET /api/export-xlsx). */
async function exportCurrent(runId, file, name) {
  if (file) tellHost({ cmd: 'currentExperiment', file });
  try {
    const got = await api('/api/export-xlsx?runId=' + enc(runId), { as: 'blob', seconds: 120 });
    saveBlob(got.blob, got.name || (name || runId) + '.xlsx');
  } catch (e) { toast(say('The Excel file could not be made: {0}', e.message), 8000); }
}

/* ---------- the program's window: messages both ways ---------- */
const answers = new Map();         // 'recycled|<file>' / 'analysisSaved|<path>' -> what waits for the window's answer
function tellHost(message) { const bridge = host(); if (bridge) { try { bridge.postMessage(message); } catch (e) { /* the window is going */ } } }
/** Sends a message to the program's window and waits for its answer ({cmd: answer, ...} of the same file). */
function askHost(message, answer, key, seconds) {
  const bridge = host();
  if (!bridge) return Promise.reject(new Error(say('Only the program\'s window can do this')));
  return new Promise((resolve, reject) => {
    const id = answer + '|' + key;
    const timer = setTimeout(() => { answers.delete(id); reject(new Error(say('The window did not answer'))); }, (seconds || 30) * 1000);
    answers.set(id, got => { clearTimeout(timer); resolve(got); });
    bridge.postMessage(message);
  });
}
let currentTold = null;
/** The window opens the "Save as" dialog of every download in the folder of the experiment that is shown. */
function tellCurrent() {
  if (!host() || mode !== 'shell') return;
  const x = view === 'analysis' ? expOf(app.curDS()) : null, file = x && x.file ? x.file : '';
  if (file === currentTold) return;
  currentTold = file;
  tellHost({ cmd: 'currentExperiment', file });
}

/* ===================== the Experiments folder and all BMG experiments ===================== */
/* Experiments: the .pk files the program writes for every run (GET /api/experiments: items and the "removed" ones whose
   files the user deleted); one is opened (GET /api/pk) like a recorded run. All BMG experiments: every run of Omega's
   own databases (GET /api/bmg/history); Open shows one (GET /api/bmg/run), Save writes its .pk and .xlsx into
   Experiments (POST /api/bmg/save). */
const LIMIT = 300;                 // rows drawn at most: a search finds the others
const EXCEL = 'Excel';             // the tag of an experiment whose .xlsx is there: the same word in every language
const PK = '.pk';                  // (a file type: the same in every language too)
const files = { list: null, removed: [], error: '', query: '', at: 0, loading: false, sig: '', body: null, foot: null, removedBox: null, filters: null };
const omega = { list: null, error: '', query: '', loading: false, sig: '', body: null, foot: null, saved: new Map() };   // saved: key -> file
let filesView = null, omegaView = null, layoutsView = null, settingsView = null, aboutView = null;

/** A view with a search box and a list; `list` is files or omega (its rows go into list.body). */
function listView(label, list, redraw, buttons, note, placeholder) {
  list.body = h('div', { cls: 'saa-runs' });
  list.foot = h('p', { cls: 'hint saa-list-foot', translate: 'no', hidden: true });
  return h('section', { cls: 'saa-view saa-list', 'data-saa': true, 'aria-label': label, hidden: true },
    h('div', { cls: 'saa-list-card' },
      h('div', { cls: 'saa-list-head' },
        h('input', { cls: 'saa-search', type: 'search', placeholder, 'aria-label': placeholder,
          oninput: e => { list.query = e.target.value; redraw(); } }),
        h('span', { cls: 'spacer' }), buttons),
      note, list.body, list.foot));
}
function buildLists() {
  const f = prefs.filter;
  const select = (name, label, options) => {
    const el = h('select', { cls: 'saa-select', 'aria-label': label, onchange: () => { f[name] = el.value; savePrefs(); files.sig = ''; renderFiles(); } },
      options.map(([value, text]) => h('option', { value, selected: (f[name] || '') === value }, text)));
    return el;
  };
  files.instrument = h('select', { cls: 'saa-select', 'aria-label': en('Instrument'), translate: 'no', onchange: () => { f.instrument = files.instrument.value; savePrefs(); files.sig = ''; renderFiles(); } });
  files.filters = h('div', { cls: 'saa-filters' },
    files.instrument,
    select('date', en('Started'), [['', en('Any time')], ['today', en('Today')], ['week', en('Last 7 days')], ['month', en('Last 30 days')], ['year', en('Last 12 months')]]),
    select('status', en('Status'), [['', en('Any status')], ['measuring', en('Measuring')], ['completed', en('Completed')], ['stopped', en('Stopped early')],
      ['interrupted', en('Interrupted')], ['unknown', en('End not seen')], ['problem', en('Files with a problem')]]),
    select('sort', en('Order'), [['', en('Newest first')], ['oldest', en('Oldest first')], ['name', en('By name')]]));
  files.removedBox = h('details', { cls: 'saa-removed', hidden: true });
  // (online: read-only - no folder, no deleted experiments to restore)
  filesView = listView(en('Experiments'), files, renderFiles, online ? null : button(en('Open data folder'), () => openFolder('')), files.filters,
    en('Search by name, sample, operator, instrument or date'));
  filesView.querySelector('.saa-list-card').appendChild(files.removedBox);
  if (X.PROGRAM === 'bmg') omegaView = listView(en('All BMG experiments'), omega, renderOmega, button(en('Refresh'), () => loadOmega(true)),
    h('p', { cls: 'hint saa-compat' }, en('Verified with Omega 6.20 and 6.40, plate-mode kinetics, 96 and 384 wells. Runs of other versions or modes are listed too, but they may not open correctly.')),
    en('Search by name, date or instrument'));
}
/** Draws the rows of a list again when what they show has changed. */
function renderList(list, rowsShown, row, empty, extra) {
  if (!list.body) return;
  const sig = [list.list ? JSON.stringify(list.list) : '', list.error, list.query, list.saved ? [...list.saved].join() : '', document.documentElement.lang, extra || ''].join('\u0001');
  if (sig === list.sig) return;
  list.sig = sig;
  const none = !list.list ? list.error || say('Loading…') : rowsShown.length ? '' : empty;
  list.body.replaceChildren(...(none ? [h('p', { cls: !list.list && list.error ? 'saa-err' : 'hint', translate: 'no' }, none)] : rowsShown.slice(0, LIMIT).map(row)));
  const foot = [rowsShown.length > LIMIT ? say('{0} more: search to find them', rowsShown.length - LIMIT) : '', list.list ? list.error : ''].filter(Boolean).join(' · ');
  setText(list.foot, foot);
  show(list.foot, !!foot);
}
function renderFiles() {
  if (!files.body) return;
  const f = prefs.filter, all = files.list || [];
  // the instruments of the list for its filter (one that is chosen stays, even when no experiment of it is left)
  const names = X.instrumentsOf(all);
  if (f.instrument && !names.includes(f.instrument)) names.push(f.instrument);
  const options = [['', say('All instruments')], ...names.map(n => [n, n])], osig = JSON.stringify(options);
  if (files.instrument.dataset.sig !== osig) {
    files.instrument.dataset.sig = osig;
    files.instrument.replaceChildren(...options.map(([value, text]) => h('option', { value, selected: (f.instrument || '') === value }, text)));
  }
  const filter = { instrument: f.instrument || '', date: f.date || '', status: f.status || '', sort: f.sort || '' };
  const found = X.experimentRows(all, files.query, filter, Date.now());
  const filtered = files.query || filter.instrument || filter.date || filter.status;
  renderList(files, found, fileRow, all.length ? say('No experiment matches the search') : say('No experiment file has been saved yet'), JSON.stringify(filter));
  // the experiments whose files were deleted: they can be made again from the program's own record (not online)
  const removed = online ? [] : X.removedRows(files.removed, files.query), rsig = JSON.stringify(removed) + document.documentElement.lang + filtered;
  if (files.removedBox.dataset.sig !== rsig) {
    files.removedBox.dataset.sig = rsig;
    show(files.removedBox, removed.length > 0);
    files.removedBox.replaceChildren(h('summary', { translate: 'no' }, say('{0:deleted experiment} - the program keeps their records', removed.length)),
      h('div', { cls: 'saa-runs' }, removed.slice(0, LIMIT).map(removedRow)));
  }
}
function renderOmega() {
  const found = X.omegaRows(omega.list, omega.query);
  renderList(omega, found, omegaRow, omega.list && omega.list.length ? say('No experiment matches the search') : say('No run was found in the Omega databases'));
}
async function loadFiles() {
  if (files.loading || (online && !editor())) return;
  files.loading = true;
  files.at = Date.now();
  try { const got = X.experimentList(await api('/api/experiments', { seconds: 60 })); files.list = got.items; files.removed = got.removed; files.error = ''; noteFiles(); }
  catch (e) { files.error = quietOnline(e) ? '' : e.message; }
  files.loading = false;
  renderFiles();
  renderTitle();
}
/** Online, an answer "sign-in required" to a view of one who was signed in: the sign-in has ended. Nothing is said
    (online.js is asked again, and the views go with the sign-in). */
const quietOnline = e => !!(online && e && e.signIn);
/** What the list knows of the experiments that are open: their files, operators and states. */
function noteFiles() {
  for (const e of files.list || []) if (e.runId) noteProtocol(String(e.runId), e.protocolDetails);
  for (const e of files.list || []) {
    const x = e.runId ? sync.known(e.runId) : null;
    if (!x) continue;
    x.file = e.file || x.file;
    x.info = Object.assign({}, x.info, { operator: e.operator || x.info.operator, status: e.status || x.info.status, name: e.name });
  }
  stripSig = '';
}
async function loadOmega(again) {
  if (omega.loading || (omega.list && !again) || (online && !editor())) return;
  omega.loading = true;
  try { const list = await api('/api/bmg/history', { seconds: 120 }); omega.list = Array.isArray(list) ? list : []; omega.error = ''; }
  catch (e) { omega.error = quietOnline(e) ? '' : e.message; }
  omega.loading = false;
  renderOmega();
  renderTitle();
}

/** An experiment of the Experiments folder: its name, what it is, Open, Show in folder, and more in a menu (online:
    Open and its two files to download). */
function fileRow(e) {
  const measuring = e.status === 'measuring', problem = X.experimentProblem(e);
  const open = button(en('Open'), () => openFile(e, open), 'sm');
  open.disabled = !feed || e.problem === 'damaged' || e.problem === 'newer';
  const what = h('div', { cls: 'saa-file', translate: 'no' },
    h('b', null, e.name || e.file || '—', e.xlsx ? h('span', { cls: 'saa-tag' }, EXCEL) : null, e.hasAnalysis ? h('span', { cls: 'saa-tag' }, say('Analysis saved')) : null),
    h('span', { cls: 'hint' }, X.experimentMeta(e, inChinese())),
    problem ? h('span', { cls: 'saa-why' }, problem) : null);
  if (online) return h('div', { cls: 'saa-runrow saa-exprow' + (e.problem ? ' bad' : '') }, what, open, downloadButtons(e.runId));
  const more = h('button', { cls: 'btn sm ghost icon', type: 'button', title: en('More actions'), 'aria-label': en('More actions'), 'aria-haspopup': 'menu' }, icon('more'));
  more.addEventListener('click', () => rowMenu(more, [
    [en('Rename…'), () => renameFile(e), measuring || !!e.problem],
    [en('Make the Excel file again'), () => regenerate(e), measuring || !!e.problem],
    [en('Export current data'), () => exportCurrent(e.runId, e.file, e.name), !measuring || !e.runId],
    [en('Move to the Recycle Bin…'), () => recycle(e), measuring]]));
  return h('div', { cls: 'saa-runrow saa-exprow' + (e.problem ? ' bad' : '') }, what, open,
    button(en('Show in folder'), () => openFolder(e.file || ''), 'sm ghost'),
    more);
}
/** A small menu under a button: [label, action, disabled]. */
function rowMenu(anchor, items) {
  closeMenu();
  const menu = h('div', { cls: 'saa-menu', role: 'menu', 'data-saa': true }, items.map(([label, act, off]) => {
    const b = h('button', { type: 'button', role: 'menuitem', onclick: () => { closeMenu(); act(); } }, label);
    b.disabled = !!off;
    return b;
  }));
  document.body.appendChild(menu);
  const r = anchor.getBoundingClientRect(), w = menu.offsetWidth;
  menu.style.left = Math.max(8, Math.min(window.innerWidth - w - 8, r.right - w)) + 'px';
  menu.style.top = Math.min(window.innerHeight - menu.offsetHeight - 8, r.bottom + 4) + 'px';
  const first = menu.querySelector('button:not(:disabled)');
  if (first) first.focus();
  const away = ev => { if (!menu.contains(ev.target) && ev.target !== anchor) closeMenu(); };
  const key = ev => { if (ev.key === 'Escape') { ev.stopPropagation(); closeMenu(); anchor.focus(); } };
  menuClose = () => { menu.remove(); document.removeEventListener('pointerdown', away, true); document.removeEventListener('keydown', key, true); window.removeEventListener('scroll', closeMenu, true); menuClose = null; };
  document.addEventListener('pointerdown', away, true);
  document.addEventListener('keydown', key, true);
  window.addEventListener('scroll', closeMenu, true);
}
let menuClose = null;
function closeMenu() { if (menuClose) menuClose(); }

async function openFile(e, b) {
  const inst = readers().find(i => i.run && i.run.id === e.runId);
  const x = e.runId ? expFor(e.runId) : null;
  if (x) {
    x.file = e.file || x.file;
    x.info = Object.assign({}, x.info, { operator: e.operator, status: e.status, name: e.name });
    if (x.path) { x.path = null; delete prefs.paths[e.runId]; savePrefs(); }      // opened from Experiments now: saved there
  }
  if (inst && inst.state === 'measuring') { openRecorded(inst.key, inst.run.id, null); return; }       // measured now: live from the program
  b.disabled = true;
  try {
    const run = await api('/api/pk?file=' + enc(e.file || ''), { seconds: 60 }), got = X.runOf(run, {});
    showRun(run, { name: X.experimentDatasetName(e, got && got.run.key), startedAt: e.startedAt, label: X.instrumentText(e.instrument) }, e.runId || e.name);
  } catch (err) { if (!quietOnline(err)) toast(say('Could not read the curve data: {0}', err.message), 6000); }
  b.disabled = !feed;
}
async function renameFile(e) {
  const name = await askText(en('Rename the experiment'), en('New name'), e.name || '', en('Rename'));
  if (!name || name === e.name) return;
  try {
    const answer = await api('/api/experiments/rename?file=' + enc(e.file || '') + '&name=' + enc(name), { method: 'POST', seconds: 60 });
    toast(say('Renamed: {0}', name));
    if (e.runId && sync.known(e.runId) && answer && answer.file) sync.known(e.runId).file = String(answer.file);
  } catch (err) { toast(say('The experiment could not be renamed: {0}', err.message), 8000); }
  loadFiles();
}
async function regenerate(e) {
  try {
    const answer = await api('/api/experiments/regenerate-xlsx?file=' + enc(e.file || ''), { method: 'POST', seconds: 120 });
    toast(X.xlsxText(answer && answer.xlsx) || say('The Excel file was made again: {0}', e.name || ''), 8000);
  } catch (err) { toast(say('The Excel file could not be made: {0}', err.message), 8000); }
  loadFiles();
}
async function recycle(e) {
  const yes = await confirmDialog(en('Move to the Recycle Bin'), [say('“{0}” and its Excel file go to the Windows Recycle Bin.', e.name || e.file),
    say('The program keeps its own record of the run: the experiment can be made again from the list of deleted experiments.')], en('Move to the Recycle Bin'));
  if (!yes) return;
  try { await api('/api/experiments/recycle?file=' + enc(e.file || ''), { method: 'POST', seconds: 60 }); }
  catch (err) {
    // the background service cannot use the Recycle Bin of the user: the program's window does it
    if (err.status !== 409 || !host()) { toast(say('The files could not be moved to the Recycle Bin: {0}', err.message), 8000); return; }
    try {
      const answer = await askHost({ cmd: 'recycle', file: e.file }, 'recycled', e.file, 60);
      if (!answer || !answer.ok) throw new Error(tr(String((answer && answer.error) || '')) || say('The request failed ({0})', 409));
    } catch (failed) { toast(say('The files could not be moved to the Recycle Bin: {0}', failed.message), 8000); return; }
  }
  toast(say('Moved to the Recycle Bin: {0}', e.name || e.file));
  loadFiles();
}
function removedRow(r) {
  const again = button(en('Restore'), async () => {
    again.disabled = true;
    try {
      const answer = await api('/api/experiments/restore?runId=' + enc(r.runId || ''), { method: 'POST', seconds: 120 });
      toast(say('The experiment files were made again: {0}', String((answer && answer.file) || r.name || '').split('/').pop().replace(/\.pk$/i, '')));
      loadFiles();
    } catch (err) { toast(say('The experiment could not be restored: {0}', err.message), 8000); again.disabled = false; }
  }, 'sm');
  return h('div', { cls: 'saa-runrow' },
    h('div', { cls: 'saa-file', translate: 'no' }, h('b', null, r.name || r.runId || '—'), h('span', { cls: 'hint' }, [String(r.startedAt || '').replace('T', ' ').slice(0, 16), X.instrumentText(r.instrument)].filter(Boolean).join(' · '))),
    again);
}

/** A run of Omega's database: its name, what it is, Open and Save (Show in folder once it is saved). */
function omegaRow(r) {
  const open = button(en('Open'), () => openOmega(r, open), 'sm');
  open.disabled = !feed;
  const what = h('div', { cls: 'saa-file', translate: 'no' }, h('b', null, X.omegaTitle(r)), h('span', { cls: 'hint' }, X.omegaMeta(r)));
  if (online) return h('div', { cls: 'saa-runrow' }, what, open);         // online: to look at only
  const saved = omega.saved.get(r.key);
  const save = saved != null ? button(en('Show in folder'), () => openFolder(saved), 'sm ghost') : button(en('Save'), () => saveOmega(r, save), 'sm ghost');
  return h('div', { cls: 'saa-runrow' }, what, open, save);
}
async function openOmega(r, b) {
  b.disabled = true;
  try {
    showRun(await api('/api/bmg/run?key=' + enc(r.key), { seconds: 120 }),
      { name: X.omegaName(r), startedAt: r.startedAt, protocol: r.testName, label: X.instrumentText({ model: r.reader, serial: r.serial }) }, 'bmg-' + r.key);
  } catch (e) { if (!quietOnline(e)) toast(say('Could not read the curve data: {0}', e.message)); }
  b.disabled = !feed;
}
async function saveOmega(r, b) {
  b.disabled = true;
  try {
    const answer = await api('/api/bmg/save?key=' + enc(r.key), { method: 'POST', seconds: 300 });
    const file = answer && answer.file ? String(answer.file) : '';
    omega.saved.set(r.key, file);
    files.at = 0;                                        // the Experiments folder has a new file
    toast(say('Saved: {0}', file.split('/').pop().replace(/\.pk$/i, '') || X.omegaTitle(r)));
    renderOmega();
  } catch (e) { toast(say('Saving failed: {0}', e.message)); b.disabled = false; }
}

/** Puts a run that came whole (a .pk file, a run of Omega's database, a file the window was asked to open) on the
    page as a dataset, as Open does with a recorded run of the history, and shows it in Analysis. analysis: the
    analysis that came with the file (null: none; undefined: ask the program for the saved one). */
function showRun(json, extra, fallbackId, analysis) {
  if (!feed) { toast(say('This page cannot show curves')); return; }
  const got = X.runOf(json, extra);
  if (!got) { toast(say('No curves were found in {0}', extra.name || '')); return; }
  const id = X.safeId(got.run.id || fallbackId);
  if (analysis !== undefined) given.set(id, analysis);
  feed.load(got.run.key, id, got.info, () => go('analysis'), () => Promise.resolve(got.run));
  kick();
}
/** An experiment file handed over whole: { run, analysis, name, path, info } (the program's window or GET /api/pk-file). */
function openPk(p) {
  const got = p && X.runOf(p.run, {});
  if (!got) { toast(say('No curves were found in {0}', (p && (p.name || p.path)) || '')); return; }
  const runId = String(got.run.id || p.name || 'file'), ds = datasetOf(X.safeId(runId), 0);
  if (ds) { openDataset(ds); toast(say('This experiment is open already: {0}', ds.name)); return; }
  const x = expFor(runId);
  if (p.path) { x.path = String(p.path); prefs.paths[runId] = x.path; savePrefs(); }
  if (p.info && typeof p.info === 'object') x.info = Object.assign({}, x.info, p.info);
  // (its annotation goes in with the dataset: restoreInto)
  showRun(p.run, { name: p.name || (p.info && p.info.name) || '' }, runId, p.analysis && typeof p.analysis === 'object' ? p.analysis : null);
}

/** Shows a file of the Experiments folder in Explorer ('' = the folder itself): through the program's window when the
    page is in it, else through the server. A server that cannot open Explorer (a Windows service) answers 409: then
    the place is shown, to be found by hand. */
async function openFolder(file) {
  const bridge = host();
  if (bridge) { bridge.postMessage({ cmd: 'openFolder', file: file || '' }); return; }
  try { await api('/api/open-folder?file=' + enc(file || '')); }
  catch (e) {
    if (e.status !== 409) { toast(say('The folder could not be opened: {0}', e.message)); return; }
    const place = X.placeOf(experimentsFolder(), file);
    dialog(en('Data folder'), [h('p', { translate: 'no' }, say('Explorer cannot be opened from here. The location is:')), h('p', { cls: 'saa-path', translate: 'no' }, place)],
      shut => [button(en('Close'), shut)]);
  }
}
/** The manual: the program's window opens it itself; else the program (it runs in the user's session). */
async function openHelp() {
  const lang = inChinese() ? 'zh' : 'en';
  if (host()) { tellHost({ cmd: 'help', lang }); return; }
  try { await api('/api/open-help?lang=' + lang); }
  catch (e) { toast(say('The manual could not be opened: {0}', e.message), 8000); }
}

/* ===================== plate layouts: prepared before a run, used by the next experiment of an instrument ===================== */
const lay = { draft: null, saved: '', sel: new Set(), anchor: null, dragging: false, error: '', loading: false, list: null, body: null, editor: null };
const GROUP_COLORS = ['--s1', '--s2', '--s3', '--s4', '--s5', '--s6', '--s7', '--s8'];
function buildLayouts() {
  lay.file = h('input', { type: 'file', accept: '.xlsx', hidden: true, onchange: e => { const f = e.target.files[0]; e.target.value = ''; if (f) importLayout(f); } });
  lay.body = h('div', { cls: 'saa-runs' });
  lay.listCard = h('div', { cls: 'saa-list-card' },
    h('div', { cls: 'saa-list-head' },
      h('p', { cls: 'hint saa-lay-intro' }, en('Names and groups of the wells, prepared before a run. Choose one for the next experiment of an instrument: its wells are named when the run starts.')),
      h('span', { cls: 'spacer' }),
      button(en('New 96-well layout'), () => editNew(8, 12), 'sm'), button(en('New 384-well layout'), () => editNew(16, 24), 'sm'),
      button(en('Import from Excel…'), () => lay.file.click(), 'sm'), lay.file),
    lay.body);
  lay.editor = h('div', { cls: 'saa-lay-edit', hidden: true });
  layoutsView = h('section', { cls: 'saa-view saa-list', 'data-saa': true, 'aria-label': en('Plate layouts'), hidden: true }, lay.listCard, lay.editor);
}
async function loadLayouts() {
  if (lay.loading) return;
  lay.loading = true;
  try { const got = await api('/api/layouts', { seconds: 30 }); layoutList = Array.isArray(got) ? got : []; lay.error = ''; }
  catch (e) { lay.error = e.message; }
  lay.loading = false;
  renderLayouts();
  renderTitle();
}
function renderLayouts() {
  if (!lay.body) return;
  show(lay.listCard, !lay.draft);
  show(lay.editor, !!lay.draft);
  if (lay.draft) { renderEditor(); return; }
  if (!layoutList) { lay.body.replaceChildren(h('p', { cls: lay.error ? 'saa-err' : 'hint', translate: 'no' }, lay.error || say('Loading…'))); return; }
  if (!layoutList.length) { lay.body.replaceChildren(h('p', { cls: 'hint', translate: 'no' }, say('No plate layout has been saved yet'))); return; }
  lay.body.replaceChildren(...layoutList.map(l => {
    const changed = X.stampMs(String(l.updatedAt || ''));
    const users = readers().filter(i => settings && settings.nextExperiment[i.key] && settings.nextExperiment[i.key].layoutId === l.id).map(i => X.reader(i.key).name);
    return h('div', { cls: 'saa-runrow' },
      h('div', { cls: 'saa-file', translate: 'no' }, h('b', null, l.name || say('Unnamed')),
        h('span', { cls: 'hint' }, [X.layoutMeta(l), fin(changed) ? say('changed {0}', X.clockText(changed, Date.now())) : ''].filter(Boolean).join(' · ')),
        users.length ? h('span', { cls: 'saa-next-use' }, say('Next experiment of {0}', users.join(', '))) : null),
      button(en('Edit'), () => editLayoutDraft(l), 'sm'),
      button(en('Use for the next experiment…'), () => useLayout(l), 'sm ghost'),
      button(en('Delete'), () => deleteLayout(l), 'sm ghost'));
  }));
}
/** The analysis page saved, renamed or deleted a layout (its menu "Layouts"): this view and "next experiment" follow. */
function layoutsChanged() {
  if (lay.body && view === 'layouts' && !lay.draft) { loadLayouts(); return; }
  api('/api/layouts', { seconds: 30 }).then(l => { layoutList = Array.isArray(l) ? l : []; render(); }).catch(() => { /* looked at again later */ });
}
function editNew(rowsN, cols) { editLayoutDraft(X.newLayout(rowsN, cols)); }
function editLayoutDraft(l) {
  lay.draft = JSON.parse(JSON.stringify(l));
  if (!lay.draft.wells || typeof lay.draft.wells !== 'object') lay.draft.wells = {};
  if (!lay.draft.plate) lay.draft.plate = { rows: 8, cols: 12 };
  lay.saved = l.id ? JSON.stringify(l) : '';
  lay.sel = new Set(); lay.anchor = null;
  lay.built = null;
  renderLayouts();
}
const layDirty = () => !!lay.draft && JSON.stringify(lay.draft) !== lay.saved;
async function leaveEditor() {
  if (layDirty() && !(await confirmDialog(en('Leave the layout'), [say('The changes to this layout are not saved.')], en('Leave without saving')))) return;
  lay.draft = null;
  renderLayouts();
  loadLayouts();
}
/** The editor of one layout: its name, the plate (click, drag, Shift for a block, Ctrl for single wells, a row or
    column by its header), and the name and group for the selected wells. */
function renderEditor() {
  const d = lay.draft, plate = d.plate;
  if (!lay.built || lay.built.rows !== plate.rows || lay.built.cols !== plate.cols) buildEditor();
  const e = lay.built;
  if (document.activeElement !== e.name) e.name.value = d.name || '';
  setText(e.size, say('{0} wells', plate.rows * plate.cols));
  const groups = X.layoutGroups(d), index = new Map(groups.map((g, i) => [g.name, i]));
  for (const [id, cell] of e.cells) {
    const w = d.wells[id] || {}, gi = w.group ? index.get(w.group) : -1;
    setText(cell.firstChild, w.name || '');
    cell.title = [id, w.name, w.group].filter(Boolean).join(' · ');
    cell.classList.toggle('sel', lay.sel.has(id));
    cell.style.setProperty('--saa-g', gi >= 0 ? 'var(' + GROUP_COLORS[gi % GROUP_COLORS.length] + ')' : 'transparent');
    cell.classList.toggle('grouped', gi >= 0);
  }
  const ids = [...lay.sel];
  setText(e.selected, ids.length ? say('{0:well} selected', ids.length) + (ids.length <= 6 ? ': ' + ids.join(', ') : '') : say('Select wells on the plate: drag, or click a row or column'));
  const one = ids.length === 1 ? d.wells[ids[0]] || {} : null;
  if (document.activeElement !== e.sample) e.sample.value = one ? one.name || '' : '';
  if (document.activeElement !== e.group) e.group.value = one ? one.group || '' : '';
  for (const b of [e.setName, e.setGroup, e.clear]) b.disabled = !ids.length;
  e.list.replaceChildren(...groups.map(g => h('option', { value: g.name })));
  e.legend.replaceChildren(...(groups.length ? groups.map((g, i) => h('span', { cls: 'saa-lay-chip', style: '--saa-g: var(' + GROUP_COLORS[i % GROUP_COLORS.length] + ')' }, g.name + ' (' + g.wells + ')'))
    : [h('span', { cls: 'hint' }, say('No groups yet'))]));
  setText(e.meta, X.layoutMeta(d));
  e.save.disabled = !layDirty() && !!d.id;
  show(e.del, !!d.id);
  setText(e.state, layDirty() ? say('Unsaved changes') : d.id ? say('Saved') : '');
}
function buildEditor() {
  const d = lay.draft, rowsN = d.plate.rows, cols = d.plate.cols, e = lay.built = { rows: rowsN, cols, cells: new Map() };
  const pick = (ids, how) => {
    if (how === 'add') ids.forEach(id => lay.sel.add(id));
    else if (how === 'toggle') ids.forEach(id => (lay.sel.has(id) ? lay.sel.delete(id) : lay.sel.add(id)));
    else lay.sel = new Set(ids);
    renderEditor();
  };
  e.grid = h('div', { cls: 'saa-lay-grid', role: 'grid', 'aria-label': en('Plate'), tabindex: '0', style: '--saa-cols:' + cols + ';--saa-rows:' + rowsN, 'data-big': cols > 12 ? '1' : '0' });
  e.grid.appendChild(h('button', { cls: 'saa-lay-head', type: 'button', title: en('Select all wells'), onclick: () => pick(X.plateWells(rowsN, cols)) }, ''));
  for (let c = 0; c < cols; c++) e.grid.appendChild(h('button', { cls: 'saa-lay-head', type: 'button', translate: 'no', onclick: ev => pick(Array.from({ length: rowsN }, (_, r) => X.wellId(r, c)), ev.ctrlKey || ev.metaKey ? 'add' : '') }, String(c + 1)));
  for (let r = 0; r < rowsN; r++) {
    e.grid.appendChild(h('button', { cls: 'saa-lay-head', type: 'button', translate: 'no', onclick: ev => pick(Array.from({ length: cols }, (_, c) => X.wellId(r, c)), ev.ctrlKey || ev.metaKey ? 'add' : '') }, 'ABCDEFGHIJKLMNOP'[r]));
    for (let c = 0; c < cols; c++) {
      const id = X.wellId(r, c), cell = h('div', { cls: 'saa-lay-cell', role: 'gridcell', translate: 'no', 'data-well': id }, h('span'));
      e.cells.set(id, cell);
      e.grid.appendChild(cell);
    }
  }
  e.grid.addEventListener('pointerdown', ev => {
    const cell = ev.target.closest('.saa-lay-cell');
    if (!cell || ev.button !== 0) return;
    ev.preventDefault();
    e.grid.focus();
    const id = cell.dataset.well;
    if (ev.shiftKey && lay.anchor) pick(X.wellRange(lay.anchor, id), ev.ctrlKey || ev.metaKey ? 'add' : '');
    else if (ev.ctrlKey || ev.metaKey) { lay.anchor = id; pick([id], 'toggle'); }
    else { lay.anchor = id; lay.dragging = true; pick([id]); }
  });
  e.grid.addEventListener('pointerover', ev => {
    const cell = ev.target.closest('.saa-lay-cell');
    if (!lay.dragging || !cell || !lay.anchor) return;
    pick(X.wellRange(lay.anchor, cell.dataset.well));
  });
  e.grid.addEventListener('keydown', ev => {
    if (ev.key === 'Delete' || ev.key === 'Backspace') { ev.preventDefault(); ev.stopPropagation(); applyToSelection({ name: '', group: '' }); }
    else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'a') { ev.preventDefault(); ev.stopPropagation(); pick(X.plateWells(rowsN, cols)); }
    else if (ev.key === 'Escape') { ev.stopPropagation(); pick([]); }
  });
  e.name = h('input', { type: 'text', cls: 'saa-wide-input', maxlength: '200', placeholder: en('Name of the layout'), 'aria-label': en('Name of the layout'),
    oninput: () => { lay.draft.name = e.name.value; renderEditor(); } });
  e.size = h('span', { cls: 'hint', translate: 'no' });
  e.state = h('span', { cls: 'hint saa-lay-state', translate: 'no' });
  e.list = h('datalist', { id: 'saaLayoutGroups' });
  e.sample = h('input', { type: 'text', maxlength: '200', 'aria-label': en('Sample name'), placeholder: en('Sample name'),
    onkeydown: ev => { if (ev.key === 'Enter') { ev.preventDefault(); applyToSelection({ name: e.sample.value }); } } });
  e.group = h('input', { type: 'text', maxlength: '100', list: 'saaLayoutGroups', 'aria-label': en('Group'), placeholder: en('Group'),
    onkeydown: ev => { if (ev.key === 'Enter') { ev.preventDefault(); applyToSelection({ group: e.group.value }); } } });
  e.setName = button(en('Name the wells'), () => applyToSelection({ name: e.sample.value }), 'sm');
  e.setGroup = button(en('Group the wells'), () => applyToSelection({ group: e.group.value }), 'sm');
  e.clear = button(en('Clear the selected wells'), () => applyToSelection({ name: '', group: '' }), 'sm ghost');
  e.selected = h('p', { cls: 'saa-lay-selected', translate: 'no' });
  e.legend = h('div', { cls: 'saa-lay-legend', translate: 'no' });
  e.meta = h('p', { cls: 'hint', translate: 'no' });
  e.save = button(en('Save'), () => saveLayout(), 'primary');
  e.del = button(en('Delete'), () => deleteLayout(lay.draft), 'ghost');
  lay.editor.replaceChildren(
    h('div', { cls: 'saa-list-card' },
      h('div', { cls: 'saa-list-head' },
        h('button', { cls: 'btn sm ghost', type: 'button', onclick: () => leaveEditor() }, icon('back'), en('All layouts')),
        e.name, e.size, h('span', { cls: 'spacer' }), e.state, e.del, e.save),
      h('div', { cls: 'saa-lay-body' },
        h('div', { cls: 'saa-lay-plate' }, e.grid),
        h('div', { cls: 'saa-lay-side' },
          e.selected,
          h('label', { cls: 'saa-field' }, h('span', null, en('Sample name')), h('span', { cls: 'saa-field-row' }, e.sample, e.setName)),
          h('label', { cls: 'saa-field' }, h('span', null, en('Group')), h('span', { cls: 'saa-field-row' }, e.group, e.setGroup)),
          e.list, e.clear,
          h('h4', null, en('Groups of this layout')), e.legend, e.meta,
          h('p', { cls: 'hint' }, en('Tip: drag over the plate to select a block of wells, hold Shift to extend it, Ctrl to add single wells. Delete clears the selected wells.'))))));
}
function applyToSelection(patch) {
  if (!lay.draft || !lay.sel.size) return;
  lay.draft = X.editLayout(lay.draft, [...lay.sel], patch);
  renderEditor();
}
async function saveLayout() {
  const d = lay.draft;
  if (!d) return;
  if (!String(d.name || '').trim()) { toast(say('Give the layout a name first')); lay.built.name.focus(); return; }
  try {
    const answer = await api('/api/layouts', { method: 'POST', body: { layout: d }, seconds: 30 });
    if (answer && answer.id) d.id = String(answer.id);
    lay.saved = JSON.stringify(d);
    toast(say('Layout saved: {0}', d.name));
  } catch (e) { toast(say('The layout could not be saved: {0}', e.message), 8000); }
  renderEditor();
  loadLayouts();
}
async function deleteLayout(l) {
  if (!l || !l.id) return;
  const yes = await confirmDialog(en('Delete the layout'), [say('The layout “{0}” is deleted. Experiments that were named with it keep their names.', l.name || '')], en('Delete'));
  if (!yes) return;
  try {
    await api('/api/layouts/delete?id=' + enc(l.id), { method: 'POST' });
    toast(say('Deleted: {0}', l.name || ''));
    if (lay.draft && lay.draft.id === l.id) lay.draft = null;
  } catch (e) { toast(say('The layout could not be deleted: {0}', e.message), 8000); }
  loadLayouts();
}
async function importLayout(file) {
  try {
    const answer = await api('/api/layouts/import-xlsx', { method: 'POST', body: await file.arrayBuffer(), seconds: 60 });
    const l = answer && answer.layout;
    if (!l || !l.plate) throw new Error(say('No plate was found in the file'));
    l.id = '';
    if (!l.name || l.name === 'Layout') l.name = file.name.replace(/\.xlsx$/i, '');
    editLayoutDraft(l);
    lay.saved = '';
    toast(say('Read from {0}: check the names, then save the layout', file.name), 8000);
    renderEditor();
  } catch (e) { toast(say('The layout could not be read: {0}', e.message), 10000); }
}
/** The layout for the next experiment of an instrument (settings "nextExperiment"), with the experiment's name. */
async function useLayout(l) {
  let current;
  try { current = X.settingsOf(await api('/api/settings')); } catch (e) { toast(say('The settings could not be read: {0}', e.message), 8000); return; }
  const list = readers();
  if (!list.length) { toast(say('{0} was not found on this PC: only imported data can be analysed.', X.ME.instruments)); return; }
  const pickInst = h('select', { 'aria-label': en('Instrument'), translate: 'no' }, list.map(i => h('option', { value: i.key }, X.reader(i.key).name + (i.serial ? ' · ' + i.serial : ''))));
  const name = h('input', { type: 'text', maxlength: '120', cls: 'saa-wide-input', 'aria-label': en('Name of the next experiment'), placeholder: en('Name of the next experiment (optional)') });
  const fill = () => { const n = current.nextExperiment[pickInst.value]; name.value = n && n.name ? n.name : ''; };
  pickInst.addEventListener('change', fill);
  fill();
  dialog(en('Use for the next experiment'), [
    h('p', { translate: 'no' }, say('The next run that starts on the instrument gets the names and groups of “{0}”. Once used, the choice is cleared.', l.name || '')),
    h('label', { cls: 'saa-field' }, h('span', null, en('Instrument')), pickInst),
    h('label', { cls: 'saa-field' }, h('span', null, en('Experiment name')), name)],
  close => [button(en('Cancel'), close), button(en('Use this layout'), async () => {
    const next = Object.assign({}, current.nextExperiment, { [pickInst.value]: { name: name.value.trim(), layoutId: l.id } });
    try {
      const dirty = setDirty();
      settings = X.settingsOf(await api('/api/settings', { method: 'POST', body: { nextExperiment: next } }));
      if (set.draft && !dirty) { set.loaded = settings; set.draft = JSON.parse(JSON.stringify(settings)); }
      close();
      toast(say('Set for the next experiment of {0}', X.reader(pickInst.value).name));
    } catch (e) { toast(say('The settings could not be saved: {0}', e.message), 8000); }
    renderLayouts(); render();
  }, 'primary')]);
}

/* ===================== Settings ===================== */
const set = { loaded: null, draft: null, error: '', body: null, bar: null };
function buildSettingsView() {
  set.body = h('div', { cls: 'saa-settings' });
  set.bar = h('div', { cls: 'saa-savebar', hidden: true }, h('span', { translate: 'no' }, ''), h('span', { cls: 'spacer' }),
    button(en('Discard changes'), () => { set.draft = JSON.parse(JSON.stringify(set.loaded)); renderSettings(); }, 'sm ghost'),
    button(en('Save settings'), () => saveSettings(), 'sm primary'));
  settingsView = h('section', { cls: 'saa-view saa-list', 'data-saa': true, 'aria-label': en('Settings'), hidden: true }, set.body, set.bar);
}
async function openSettingsView() {
  set.error = '';
  if (!set.draft) set.body.replaceChildren(h('p', { cls: 'hint', translate: 'no' }, say('Loading…')));
  const [s] = await Promise.all([api('/api/settings').catch(e => { set.error = e.message; return null; }), loadHealth(), loadLayouts()]);
  if (s) { settings = X.settingsOf(s); useScreenNames(); if (!setDirty()) { set.loaded = settings; set.draft = JSON.parse(JSON.stringify(settings)); } }
  renderSettings();
  render();
}
const setDirty = () => !!set.draft && !!set.loaded && X.stableJson(set.draft) !== X.stableJson(set.loaded);
async function loadHealth() {
  try { health = await api('/api/health', { seconds: 20 }); } catch (e) { health = health || null; }
  return health;
}
/** A card of the Settings and About pages. title: a fixed label, or a node (data under translate="no"). */
function card(title, ...body) { return h('section', { cls: 'saa-card' }, h('h2', null, title), ...body); }
function renderSettings() {
  if (!set.body) return;
  const d = set.draft;
  if (!d) { set.body.replaceChildren(h('p', { cls: set.error ? 'saa-err' : 'hint', translate: 'no' }, set.error || say('Loading…'))); return; }
  const changed = () => { const on = setDirty(); show(set.bar, on); setText(set.bar.firstChild, on ? say('The settings have unsaved changes') : ''); };
  const check = (label, on, flip, note) => {
    const box = h('input', { type: 'checkbox', checked: !!on, onchange: () => { flip(box.checked); changed(); } });
    return h('label', { cls: 'chk saa-check' }, box, h('span', null, label), note || null);
  };
  const text = (value, placeholder, label, put, max) => h('input', { type: 'text', value: value || '', maxlength: String(max || 120), placeholder, 'aria-label': label,
    oninput: e => { put(e.target.value); changed(); } });
  // instruments: the name on screen, and what the next experiment is to be
  const insts = readers().map(i => i.key);
  for (const k of Object.keys(d.instruments).concat(Object.keys(d.nextExperiment))) if (!insts.includes(k)) insts.push(k);
  const layouts = layoutList || [];
  const instRows = insts.length ? insts.map(key => {
    const inst = readerOf(key), plain = X.reader(key).n ? (X.reader(key).n === 'P' ? say('PCR instrument')
      : /^P\d/.test(String(X.reader(key).n)) ? 'PCR ' + String(X.reader(key).n).slice(1) : say('Reader {0}', X.reader(key).n)) : key;
    const next = d.nextExperiment[key] || { name: '', layoutId: '' };
    const putNext = (k, v) => { const n = Object.assign({ name: '', layoutId: '' }, d.nextExperiment[key], { [k]: v }); if (n.name.trim() || n.layoutId) d.nextExperiment[key] = n; else delete d.nextExperiment[key]; };
    const layoutPick = h('select', { 'aria-label': en('Plate layout for the next experiment'), translate: 'no', onchange: e => { putNext('layoutId', e.target.value); changed(); } },
      h('option', { value: '' }, say('No layout')), layouts.map(l => h('option', { value: l.id, selected: l.id === next.layoutId }, l.name || l.id)));
    return h('div', { cls: 'saa-set-inst' },
      h('div', { cls: 'saa-set-inst-head', translate: 'no' }, h('span', { cls: 'saa-badge' }, badge(key)), h('b', null, plain), h('span', { cls: 'hint' }, inst ? subtitle(inst) : key)),
      h('label', { cls: 'saa-field' }, h('span', null, en('Name on screen')),
        text(d.instruments[key] ? d.instruments[key].displayName : '', plain, en('Name on screen'), v => { if (v.trim()) d.instruments[key] = { displayName: v }; else delete d.instruments[key]; }, 60)),
      h('label', { cls: 'saa-field' }, h('span', null, en('Next experiment')),
        h('span', { cls: 'saa-field-row' }, text(next.name, say('Name (optional)'), en('Name of the next experiment'), v => putNext('name', v), 120), layoutPick)));
  }) : [h('p', { cls: 'hint', translate: 'no' }, say('{0} was not found on this PC: only imported data can be analysed.', X.ME.instruments))];
  const zh = inChinese(), hl = health || {};
  const kindsLabel = { completed: en('An experiment is completed'), stopped: en('An experiment stopped early'), interrupted: en('An experiment was interrupted'),
    stuck: en('No new data for a long time'), 'save-failed': en('Data could not be saved') };
  const ed = edition();
  set.body.replaceChildren(
    card(en('Instruments'), h('p', { cls: 'hint' }, en('The name on screen is shown on the tiles, in the lists and in the notifications; file names keep the model and serial number. The next experiment of an instrument gets the name and the plate layout chosen here (once).')), ...instRows),
    card(en('Notifications'),
      h('p', { cls: 'hint' }, ed === 'installed' ? en('Windows notifications come from the program\'s icon in the taskbar, also when the window is closed. The window shows them too, without a sound.')
        : en('The window shows a notification when:')),
      h('div', { cls: 'saa-checks' }, X.EVENT_KINDS.map(k => check(kindsLabel[k], d.notify.kinds.includes(k), on => { d.notify.kinds = X.EVENT_KINDS.filter(x => (x === k ? on : d.notify.kinds.includes(x))); }))),
      check(en('Play a sound'), d.notify.sound, on => { d.notify.sound = on; })),
    card(en('Keep the PC awake'),
      check(en('Keep this PC awake while an experiment is measuring'), d.keepAwake, on => { d.keepAwake = on; }),
      h('p', { cls: 'hint', translate: 'no' }, state && state.keepAwake && state.keepAwake.active ? say('Active now: an experiment is measuring.') : say('Not needed now: no experiment is measuring.')),
      h('p', { cls: 'hint' }, en('Windows may otherwise go to sleep during a long run; the instrument software then stops reading.'))),
    card(en('Operator'),
      h('label', { cls: 'saa-field' }, h('span', null, en('Default operator')),
        text(d.operatorDefault, state && state.operator ? String(state.operator) : say('the Windows user'), en('Default operator'), v => { d.operatorDefault = v.trim(); }, 100)),
      h('p', { cls: 'hint' }, en('Recorded with every new experiment. Leave it empty to record the Windows user who is logged on. It can be changed for each experiment in the analysis.'))),
    card(en('Data folder'),
      h('div', { cls: 'saa-kv', translate: 'no' },
        h('span', null, say('Data folder')), h('code', { cls: 'saa-path' }, String(hl.dataFolder || '—')),
        h('span', null, say('Experiments')), h('code', { cls: 'saa-path' }, experimentsFolder() || '—'),
        h('span', null, say('Free space')), h('b', null, fin(hl.freeBytes) ? X.bytesText(hl.freeBytes) : '—')),
      h('div', { cls: 'saa-card-actions' }, button(en('Open data folder'), () => openFolder(''))),
      h('p', { cls: 'hint' }, en('Back up the whole data folder. Experiments holds your files; System is the program\'s own (records, logs, settings).'))),
    card(en('Checks of this PC'),
      health ? h('ul', { cls: 'saa-checklist', translate: 'no' }, X.checkLines(health, zh).map(c => h('li', { 'data-ok': c.ok ? '1' : '0' }, icon(c.ok ? 'check' : 'alert'), h('span', null, c.text))))
        : h('p', { cls: 'hint', translate: 'no' }, say('The checks could not be read.')),
      h('div', { cls: 'saa-card-actions' }, button(en('Check again'), async () => { await loadHealth(); renderSettings(); }, 'sm'))),
    card(en('Program versions'), versionsList()),
    card(en('Support'),
      h('p', { cls: 'hint' }, en('A file for whoever helps you: the program\'s logs, versions, settings and checks. It holds no experiment data unless you tick the box. Nothing is sent anywhere: you pass the file on yourself.')),
      h('label', { cls: 'chk saa-check' }, set.withData = h('input', { type: 'checkbox' }), h('span', null, en('Include the experiment files (larger; holds your data)'))),
      h('div', { cls: 'saa-card-actions' }, button(en('Save a support file…'), () => supportBundle(), 'sm'))));
  changed();
}
function versionsList() {
  const hl = health || {}, meta = name => (document.querySelector('meta[name="' + name + '"]') || {}).content || '';
  return h('div', { cls: 'saa-kv', translate: 'no' },
    h('span', null, say('Program')), h('b', null, [hl.program || X.ME.name, hl.version || ''].filter(Boolean).join(' ')),
    h('span', null, say('Edition')), h('span', null, X.editionText(edition()) || '—'),
    h('span', null, say('Analysis page')), h('span', null, [hl.pageVersion || meta('application-version'), meta('application-build')].filter(Boolean).join(' · ') || '—'));
}
async function saveSettings() {
  if (!set.draft) return;
  try {
    const saved = X.settingsOf(await api('/api/settings', { method: 'POST', body: set.draft, seconds: 30 }));
    settings = set.loaded = saved;
    set.draft = JSON.parse(JSON.stringify(saved));
    useScreenNames();
    toast(say('Settings saved'));
    for (const t of tiles.values()) { t.lastSig = null; t.groupSig = null; }
  } catch (e) { toast(say('The settings could not be saved: {0}', e.message), 8000); }
  renderSettings();
  render();
}
async function supportBundle() {
  try {
    const got = await api('/api/support-bundle?data=' + (set.withData && set.withData.checked ? '1' : '0'), { as: 'blob', seconds: 300 });
    saveBlob(got.blob, got.name || 'PlateKineticsLive-support.zip');
  } catch (e) { toast(say('The support file could not be made: {0}', e.message), 8000); }
}

/* ===================== About ===================== */
const about = { body: null };
function buildAbout() {
  about.body = h('div', { cls: 'saa-settings saa-about' });
  aboutView = h('section', { cls: 'saa-view saa-list', 'data-saa': true, 'aria-label': en('About'), hidden: true }, about.body);
}
async function renderAbout(fetch) {
  if (!about.body) return;
  // (online the program's health is for one who is signed in: the checks of the PC are shown here, as Settings shows them)
  if (fetch && (!online || editor())) await loadHealth();
  if (online && !editor()) health = null;
  const research = en('For research use only, not for diagnostic use.');
  about.body.replaceChildren(...[
    card(h('span', { translate: 'no' }, (health && health.program) || X.ME.name), versionsList(),
      host() && !online ? h('div', { cls: 'saa-card-actions' }, button(en('Check for updates'), () => { const b = host(); if (b) b.postMessage({ cmd: 'checkUpdate', manual: true }); }, 'sm')) : null,
      h('p', { cls: 'saa-notice', translate: 'no' }, h('b', null, X.chineseOf(research)), h('br'), h('span', null, research))),
    online && editor() ? card(en('Checks of this PC'),
      health ? h('ul', { cls: 'saa-checklist', translate: 'no' }, X.checkLines(health, inChinese()).map(c => h('li', { 'data-ok': c.ok ? '1' : '0' }, icon(c.ok ? 'check' : 'alert'), h('span', null, c.text))))
        : h('p', { cls: 'hint', translate: 'no' }, say('The checks could not be read.')),
      h('div', { cls: 'saa-card-actions' }, button(en('Check again'), () => renderAbout(true), 'sm'))) : null,
    card(en('Licence'),
      h('p', null, en('MIT License. Free to use, copy, change and pass on, also in other laboratories, provided the copyright and licence notice is kept.')),
      h('p', null, en('The program is provided as is, without warranty of any kind. The results of an analysis are the user\'s to check.'))),
    card(en('Instrument makers'),
      h('p', null, en('This program reads the files of the instrument software; it does not control the instruments. It is not made, endorsed or supported by BMG LABTECH or RocGene. Omega and MARS are trademarks of BMG LABTECH; Archimed is a trademark of RocGene.'))),
    card(en('Privacy'),
      h('p', null, en('The program works on this PC only. It sends nothing anywhere: no data, no usage figures, no updates are fetched. Its page is served on this PC and opens only in its own window.'))),
    card(en('Third-party software'),
      h('ul', { cls: 'saa-plain', translate: 'no' },
        h('li', null, 'SheetJS Community Edition 0.20.3 (Apache License 2.0)', ' - ', say('reads the workbooks in the analysis page')),
        h('li', null, 'Microsoft Edge WebView2', ' - ', say('the window of the installed and portable editions (Microsoft)')),
        h('li', null, 'Microsoft .NET Framework 4.8', ' - ', say('part of Windows')))),
    card(en('The analysis page on the web'),
      h('p', null, en('The same analysis, without the instruments, for files from anywhere:')),
      h('p', null, h('a', { href: 'https://tools.fow.mobi/kinetics/', target: '_blank', rel: 'noopener noreferrer', translate: 'no' }, 'https://tools.fow.mobi/kinetics/')))].filter(Boolean));
}

/* ---------- the program's window: a .pk file opened with a double click comes as a message { cmd: 'openRun', name, run, path, analysis } ---------- */
let running = false;               // start() has run: the feed is there
const waiting = [];                // messages that came before
function windowMessage(e) {
  let msg = e && e.data;
  if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch (err) { return; } }
  if (!msg || typeof msg !== 'object') return;
  if (msg.cmd === 'recycled' || msg.cmd === 'analysisSaved') {
    const id = msg.cmd + '|' + (msg.cmd === 'recycled' ? msg.file : msg.path), done = answers.get(id);
    if (done) { answers.delete(id); done(msg); }
    return;
  }
  if (msg.cmd === 'focus') { if (running) { poll(); syncSoon(); } return; }
  if (msg.cmd !== 'openRun') return;
  if (running) openPk(msg); else waiting.push(msg);
}

/** A new point for the dataset in view makes the page rebuild its results table, which would jump back to its top:
    put it back where the user was reading. (The page's own redraw runs in the animation frame it has asked for
    before this call or within it, so ours comes after it.) */
function redrawKeepingPlace(...parts) {
  const box = $('#resBody .table-wrap'), top = box ? box.scrollTop : 0, left = box ? box.scrollLeft : 0;
  app.inv(...parts);
  if (top || left) requestAnimationFrame(() => { const now = $('#resBody .table-wrap'); if (now) { now.scrollTop = top; now.scrollLeft = left; } });
}

/* ===================== start ===================== */
/** The page's own shortcuts act on the dataset of the analysis view; while that view is not showing they are kept
    from it. Ctrl+S saves the annotation of an experiment at once (the page's own Ctrl+S saves a project file of an
    imported table). */
function guardKeys(e) {
  if (mode !== 'shell') return;
  const ctrl = e.ctrlKey || e.metaKey, k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
  if (ctrl && !e.altKey && k === 's' && !document.querySelector('.modal-back')) {
    const x = view === 'analysis' ? expOf(app.curDS()) : null;
    if (x || view !== 'analysis') { e.preventDefault(); e.stopImmediatePropagation(); }
    if (e.shiftKey) return;              // Ctrl+Shift+S: the page's "Save as…", for imported tables in the analysis view
    if (x) saveExperiment();
    else if (view === 'layouts' && lay.draft) saveLayout();
    else if (view === 'settings' && setDirty()) saveSettings();
    return;
  }
  if (view === 'analysis' || document.querySelector('.modal-back')) return;
  const el = document.activeElement, typing = X.isEntry(el);
  // digits and G group the selection, ? opens the help (also the fullwidth one a Chinese input method types)
  const pages = (ctrl && (k === 'z' || k === 'y' || k === 'a')) || (!ctrl && !e.altKey && !typing && (/^[0-9g?\uff1f]$/.test(k)));
  if (pages && !(typing && own(el))) e.stopImmediatePropagation();
}
function start() {
  document.addEventListener('pointerdown', () => { ui.pointerDown = true; ui.lastInput = Date.now(); }, true);
  for (const type of ['pointerup', 'pointercancel']) document.addEventListener(type, () => { ui.pointerDown = false; lay.dragging = false; }, true);
  window.addEventListener('blur', () => { ui.pointerDown = false; });
  for (const type of ['keydown', 'input']) document.addEventListener(type, () => { ui.lastInput = Date.now(); }, true);
  window.addEventListener('keydown', guardKeys, true);
  // the tiles' colours are read from the page's theme when they are drawn
  const redraw = () => { for (const t of tiles.values()) { t.sig = t.plateSig = ''; t.groupSig = null; } render(); };
  new MutationObserver(redraw).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  if (window.matchMedia) matchMedia('(prefers-color-scheme: dark)').addEventListener('change', redraw);
  // the page sets the lang attribute of <html> whenever it switches its language (and at its start)
  new MutationObserver(relang).observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] });
  if (app) feed = X.createFeed({ app, auto: autoOn, busy, toast, changed: kick, redraw: redrawKeepingPlace, created,
    protocol: (runId, details, dsRunId) => { noteProtocol(runId, details); if (dsRunId !== runId) noteProtocol(dsRunId, details); },
    fetchRun: (key, id) => api('/api/run?key=' + enc(key) + '&id=' + enc(id)) });
  // the shell follows the user: the experiment shown is noted (for the folded list and the downloads' folder)
  setInterval(() => {
    if (mode !== 'shell' || view !== 'analysis') return;
    const ds = app.curDS();
    if (ds && X.isRunId(realRunId(ds.id))) {
      // every channel of the experiment in view counts as opened
      const run = X.runOfDataset(ds.id).runId;
      for (const d of app.S.ds) if (X.runOfDataset(d.id).runId === run) prefs.seen[d.id] = Date.now();
      // what is no longer on the page is forgotten
      for (const id of Object.keys(prefs.seen)) if (!app.S.ds.some(d => d.id === id)) delete prefs.seen[id];
      for (const id of Object.keys(prefs.sync)) if (!app.S.ds.some(d => d.id === id)) delete prefs.sync[id];
      savePrefsSoon();
    }
    tellCurrent();
  }, 5000);
  // the annotation of the experiment in view: every second, and shortly after the user did something; a dataset that
  // is being closed is looked at once more before it goes
  if (app) {
    setInterval(syncNow, 1000);
    for (const type of ['pointerup', 'keyup', 'change']) document.addEventListener(type, syncSoon, true);
    document.addEventListener('pointerdown', e => { if (e.target && e.target.closest && e.target.closest('#dsTabs [data-close]')) syncNow(); }, true);
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') syncSoon(); });
    // closing the page: what is changed goes now, with a request that outlives the page
    window.addEventListener('pagehide', () => { try { sync.flush(); } catch (e) { /* the page is going */ } });
    // online: signed in or out of editing (online.js)
    if (online) window.addEventListener('saaonline:me', () => { sync.signedIn(); stripSig = ''; syncNow(); });
  }
  // online: the views and the PC's status of one who is signed in come and go with the sign-in
  if (online) window.addEventListener('saaonline:me', signedInChanged);
  if (!online) {
    api('/api/settings').then(s => { settings = X.settingsOf(s); useScreenNames(); render(); }).catch(() => { /* an older program: defaults */ });
    loadHealth().then(() => render());
    api('/api/layouts').then(l => { layoutList = Array.isArray(l) ? l : []; render(); }).catch(() => { /* none */ });
  }
  relang();
  poll();
  running = true;
  for (const msg of waiting.splice(0)) openPk(msg);
  window.addEventListener('beforeunload', savePrefs);
}

// the program's window (WebView2) hands over the runs of the .pk files it is asked to open, and answers what the page
// asks of it; heard from the start, so that a message sent while the page is still loading is not lost
if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === 'function') window.chrome.webview.addEventListener('message', windowMessage);
window.SAAShell = { mount };
// a page the server did not recognise never calls mount(): show the instruments above it once it is there
document.addEventListener('DOMContentLoaded', () => { if (!mode) mount(null); });
})();
