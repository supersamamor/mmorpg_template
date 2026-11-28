namespace Game.Tools.Editor;

/// <summary>
/// The editor's one page (T-801). Its forms are built at run time from the kind's JSON Schema: a property's type, enum, pattern, description and the required list decide the
/// field. A string whose pattern starts with a namespace (<c>^item\.</c>) is a reference: it offers the ids with that prefix from the content and the theme's asset keys. The
/// document's own <c>id</c> is shown but not editable. Nothing here names a content field (hard rule 2).
/// </summary>
internal static class EditorPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Content editor</title>
<style>
:root { --bg:#f6f5f2; --panel:#fff; --ink:#1d1d1b; --muted:#6b6a66; --line:#dcdad4; --accent:#2f5d8a; --bad:#a3271f; --good:#2c6b3a; --warn:#8a5a00; }
@media (prefers-color-scheme: dark) { :root { --bg:#191918; --panel:#232322; --ink:#ecebe8; --muted:#a3a29d; --line:#3a3936; --accent:#7fb0e0; --bad:#ff8a80; --good:#8fd19e; --warn:#e6b85c; } }
* { box-sizing: border-box; }
body { margin:0; font:14px/1.4 system-ui, sans-serif; background:var(--bg); color:var(--ink); }
header { padding:10px 16px; border-bottom:1px solid var(--line); display:flex; gap:12px; align-items:baseline; flex-wrap:wrap; }
header h1 { font-size:16px; margin:0; }
header .note { color:var(--muted); font-size:12px; }
main { display:grid; grid-template-columns: 220px 1fr; min-height: calc(100vh - 45px); }
nav { border-right:1px solid var(--line); padding:8px; overflow:auto; }
nav button { display:block; width:100%; text-align:left; border:0; background:none; color:var(--ink); padding:4px 6px; border-radius:4px; cursor:pointer; font:inherit; }
nav button.on { background:var(--accent); color:var(--panel); }
nav .kinds { display:flex; flex-wrap:wrap; gap:4px; margin-bottom:8px; }
nav .kinds button { display:inline-block; width:auto; border:1px solid var(--line); }
nav small { color:var(--muted); }
nav button.on small { color:inherit; opacity:.85; }
section { padding:12px 16px; overflow:auto; }
fieldset { border:1px solid var(--line); border-radius:6px; margin:6px 0; padding:6px 10px; background:var(--panel); }
legend { color:var(--muted); font-size:12px; }
.row { display:grid; grid-template-columns: 170px 1fr; gap:8px; align-items:start; margin:4px 0; }
.row > label { padding-top:4px; font-weight:600; word-break:break-word; }
.row > label .req { color:var(--bad); }
.hint { color:var(--muted); font-size:12px; margin-top:2px; }
input, select, textarea { font:inherit; color:var(--ink); background:var(--bg); border:1px solid var(--line); border-radius:4px; padding:3px 6px; width:100%; }
input[readonly] { color:var(--muted); }
input[type=checkbox] { width:auto; }
textarea { min-height:3.2em; }
.item { border-left:3px solid var(--line); padding-left:8px; margin:4px 0; }
.tools { display:flex; gap:4px; margin:2px 0; }
.tools button, .add { font:inherit; font-size:12px; border:1px solid var(--line); background:var(--panel); color:var(--ink); border-radius:4px; padding:1px 8px; cursor:pointer; }
.bar { position:sticky; top:0; background:var(--bg); padding:6px 0; display:flex; gap:10px; align-items:center; z-index:1; flex-wrap:wrap; }
.bar button.save { font:inherit; background:var(--accent); color:var(--panel); border:0; border-radius:4px; padding:5px 14px; cursor:pointer; }
#result { white-space:pre-wrap; }
.good { color:var(--good); } .bad { color:var(--bad); } .warn { color:var(--warn); }
ul.issues { margin:4px 0; padding-left:18px; }
@media (max-width: 700px) { main { grid-template-columns: 1fr; } nav { border-right:0; border-bottom:1px solid var(--line); max-height:40vh; } .row { grid-template-columns: 1fr; } }
</style>
</head>
<body>
<header><h1>Content editor</h1><span class="note">Local only. Every save checks the whole content set first and writes nothing if it has an error.</span></header>
<main>
<nav><div class="kinds" id="kinds"></div><div id="docs"></div></nav>
<section><div class="bar"><strong id="title">Pick a kind and a document.</strong><button class="save" id="save" hidden>Save</button><span id="result"></span></div><div id="form"></div></section>
</main>
<script>
"use strict";
const $ = (id) => document.getElementById(id);
const show = (...nodes) => $("result").replaceChildren(...nodes.filter(n => n != null));
const el = (tag, props, ...kids) => { const e = document.createElement(tag); Object.assign(e, props || {}); for (const k of kids) if (k != null) e.append(k); return e; };
const call = async (url, options) => { const r = await fetch(url, options); const text = await r.text(); let body = null; try { body = JSON.parse(text); } catch { body = text; } return { ok: r.ok, status: r.status, body }; };
let ids = {}, kind = null, schema = null, current = null, root = null;

function typesOf(s) { const t = s.type; return Array.isArray(t) ? t : t ? [t] : []; }
function baseType(s) { return typesOf(s).find(t => t !== "null") || (s.enum ? "string" : s.properties ? "object" : null); }
function prefixOf(s) { const m = /^\^([a-z_]+)\\\./.exec(s.pattern || ""); return m ? m[1] : null; }
let listCount = 0;
function datalist(prefix) {
  const id = "ids-" + prefix;
  if (!document.getElementById(id)) { const l = el("datalist", { id }); for (const v of ids[prefix] || []) l.append(el("option", { value: v })); document.body.append(l); }
  return id;
}

// A field for schema s holding value v. Returns { node, get } where get() is the value, or undefined to leave the key out.
function field(s, v, optional, isId) {
  const type = baseType(s);
  const nullable = typesOf(s).includes("null") || optional;
  if (s.enum) {
    const sel = el("select");
    if (nullable) sel.append(el("option", { value: "", textContent: "(none)" }));
    for (const o of s.enum) sel.append(el("option", { value: o, textContent: o }));
    sel.value = v == null ? "" : String(v);
    return { node: sel, get: () => sel.value === "" ? undefined : sel.value };
  }
  if (type === "string") {
    const long = typeof v === "string" && (v.length > 50 || v.includes("\n"));
    const input = long ? el("textarea", { value: v ?? "" }) : el("input", { type: "text", value: v ?? "" });
    if (isId) input.readOnly = true;
    const p = prefixOf(s);
    if (p && !isId) { input.setAttribute("list", datalist(p)); input.placeholder = p + ".…"; }
    return { node: input, get: () => input.value === "" && nullable ? undefined : input.value };
  }
  if (type === "integer" || type === "number") {
    const input = el("input", { type: "number", step: type === "integer" ? "1" : "any", value: v ?? "" });
    return { node: input, get: () => input.value === "" ? (nullable ? undefined : 0) : Number(input.value) };
  }
  if (type === "boolean") {
    if (!nullable) { const box = el("input", { type: "checkbox", checked: !!v }); return { node: box, get: () => box.checked }; }
    const sel = el("select"); for (const [val, t] of [["", "(not set)"], ["true", "true"], ["false", "false"]]) sel.append(el("option", { value: val, textContent: t }));
    sel.value = v == null ? "" : String(v);
    return { node: sel, get: () => sel.value === "" ? undefined : sel.value === "true" };
  }
  if (type === "array" && s.items) return list(s, v, nullable);
  if (type === "object" && s.properties) return object(s, v, nullable);
  if (type === "object" && s.additionalProperties && typeof s.additionalProperties === "object") return map(s, v, nullable);
  // Anything else (a shape the forms do not draw): JSON text.
  const area = el("textarea", { value: v === undefined ? "" : JSON.stringify(v, null, 1) });
  return { node: el("div", null, area, el("div", { className: "hint", textContent: "JSON" })), get: () => { if (area.value.trim() === "") return undefined; return JSON.parse(area.value); } };
}

function row(name, s, required, f) {
  const label = el("label", { textContent: name }); if (required) label.append(el("span", { className: "req", textContent: " *" }));
  const right = el("div", null, f.node); if (s.description) right.append(el("div", { className: "hint", textContent: s.description }));
  return el("div", { className: "row" }, label, right);
}

function object(s, v, optional, top) {
  const box = el("fieldset"); const fields = [];
  const required = new Set(s.required || []);
  let present = v != null || !optional;
  const inner = el("div");
  if (optional && !top) {
    const toggle = el("input", { type: "checkbox", checked: present });
    const legend = el("legend", null, toggle, " include");
    toggle.onchange = () => { present = toggle.checked; inner.hidden = !present; };
    box.append(legend); inner.hidden = !present;
  }
  for (const [name, ps] of Object.entries(s.properties)) {
    const f = field(ps, v ? v[name] : undefined, !required.has(name), top && name === "id");
    fields.push([name, f]); inner.append(row(name, ps, required.has(name), f));
  }
  box.append(inner);
  return { node: box, get: () => { if (!present) return undefined; const o = {}; for (const [n, f] of fields) { const x = f.get(); if (x !== undefined) o[n] = x; } return o; } };
}

function list(s, v, optional) {
  const box = el("div"); const items = [];
  const add = (value) => {
    const f = field(s.items, value, false, false);
    const wrap = el("div", { className: "item" });
    const entry = { f, wrap };
    const up = el("button", { type: "button", textContent: "↑", title: "move up" }), down = el("button", { type: "button", textContent: "↓", title: "move down" }), del = el("button", { type: "button", textContent: "remove" });
    up.onclick = () => { const i = items.indexOf(entry); if (i > 0) { items.splice(i, 1); items.splice(i - 1, 0, entry); redraw(); } };
    down.onclick = () => { const i = items.indexOf(entry); if (i < items.length - 1) { items.splice(i, 1); items.splice(i + 1, 0, entry); redraw(); } };
    del.onclick = () => { items.splice(items.indexOf(entry), 1); redraw(); };
    wrap.append(el("div", { className: "tools" }, up, down, del), f.node);
    items.push(entry);
  };
  const body = el("div");
  const redraw = () => { body.replaceChildren(...items.map(i => i.wrap)); };
  for (const x of Array.isArray(v) ? v : []) add(x);
  redraw();
  const more = el("button", { type: "button", className: "add", textContent: "+ add" });
  more.onclick = () => { add(undefined); redraw(); };
  box.append(body, more);
  return { node: box, get: () => { const out = items.map(i => i.f.get()).filter(x => x !== undefined); return out.length === 0 && optional && v == null ? undefined : out; } };
}

function map(s, v, optional) {
  const box = el("div"); const rows = [];
  const add = (key, value) => {
    const k = el("input", { type: "text", value: key ?? "", placeholder: "key" }); const f = field(s.additionalProperties, value, false, false);
    const del = el("button", { type: "button", textContent: "remove" }); const r = el("div", { className: "row" }, k, el("div", null, f.node, el("div", { className: "tools" }, del)));
    const entry = { k, f, r }; del.onclick = () => { rows.splice(rows.indexOf(entry), 1); r.remove(); }; rows.push(entry); body.append(r);
  };
  const body = el("div");
  for (const [key, value] of Object.entries(v || {})) add(key, value);
  const more = el("button", { type: "button", className: "add", textContent: "+ add" }); more.onclick = () => add("", undefined);
  box.append(body, more);
  return { node: box, get: () => { const o = {}; for (const e of rows) if (e.k.value !== "") o[e.k.value] = e.f.get(); return Object.keys(o).length === 0 && optional && v == null ? undefined : o; } };
}

async function showKinds() {
  $("title").textContent = "Loading…";
  const idsLoading = call("/api/ids");
  const kinds = (await call("/api/kinds")).body;
  ids = (await idsLoading).body;
  $("title").textContent = "Pick a kind and a document.";
  $("kinds").replaceChildren(...kinds.map(k => { const b = el("button", { type: "button", textContent: k.kind, title: k.title }); b.onclick = () => openKind(k.kind, b); return b; }));
}

async function openKind(k, button) {
  for (const b of $("kinds").children) b.classList.toggle("on", b === button);
  kind = k; schema = (await call("/api/schema/" + k)).body;
  const docs = (await call("/api/kinds/" + k)).body;
  $("docs").replaceChildren(...docs.map(d => { const b = el("button", { type: "button" }, d.name || d.id, el("br"), el("small", { textContent: d.id })); b.onclick = () => openDoc(d.path, b); return b; }));
}

async function openDoc(path, button) {
  for (const b of $("docs").children) b.classList.toggle("on", b === button);
  const r = await call("/api/doc?path=" + encodeURIComponent(path));
  if (!r.ok) { $("result").textContent = r.body.problem; return; }
  current = r.body; $("title").textContent = path; $("result").textContent = ""; $("save").hidden = false;
  root = object(schema, current.document, false, true);
  $("form").replaceChildren(root.node);
}

function issueList(issues, severity) {
  const list = issues.filter(i => i.severity === severity);
  if (list.length === 0) return null;
  return el("ul", { className: "issues" }, ...list.map(i => el("li", { textContent: `${i.file}:${i.line}:${i.column} ${i.code}: ${i.message}${i.path ? " [" + i.path + "]" : ""}` })));
}

$("save").onclick = async () => {
  let document;
  try { document = root.get(); } catch (e) { show(el("span", { className: "bad", textContent: "A JSON field does not parse: " + e.message })); return; }
  $("result").textContent = "Checking the whole content set…";
  const r = await call("/api/doc", { method: "POST", headers: { "Content-Type": "application/json", "X-Editor": "1" }, body: JSON.stringify({ path: current.path, version: current.version, document }) });
  const b = r.body;
  if (r.ok) {
    current.version = b.version;
    const on = $("docs").querySelector("button.on");
    if (on && typeof document.name === "string") on.firstChild.textContent = document.name;   // the list shows the new name
    const how = b.mode === "inplace" ? "only values changed: comments, order and layout kept" : "the file was written again: key order kept, comments lost";
    show(el("span", { className: "good", textContent: `Saved (${how}).` }), issueList(b.issues, "warning"));
    ids = (await call("/api/ids")).body;
    for (const l of window.document.querySelectorAll("datalist")) l.remove();   // the next form lists the ids as they are now
  } else if (b && b.problem) {
    show(el("span", { className: "bad", textContent: "Not saved: " + b.problem }));
  } else {
    show(el("span", { className: "bad", textContent: "Not saved: the content set would have errors. Nothing was written." }), issueList(b.issues, "error"));
  }
};

showKinds();
</script>
</body>
</html>
""";
}
