function l(h, t, e) {
  const s = document.createElement(h);
  return t && (s.className = t), e && e.appendChild(s), s;
}
function G(h) {
  if (!h) return null;
  if (typeof h != "string") return h;
  const t = h.trim();
  if (t.startsWith("<svg")) {
    const i = new DOMParser().parseFromString(t, "image/svg+xml").documentElement;
    if (i && i.nodeName.toLowerCase() === "svg")
      return document.importNode(i, !0);
  }
  const e = document.createElement("span");
  return e.innerHTML = t, e.childNodes.length === 1 ? e.firstChild : e;
}
function E(h, t) {
  h.textContent = "";
  const e = G(t);
  e && h.appendChild(e);
}
function p(h, t, e, s) {
  return h.addEventListener(t, e, s), () => h.removeEventListener(t, e, s);
}
function b(h, t, e) {
  return h < t ? t : h > e ? e : h;
}
const C = 8;
class W {
  constructor(t) {
    this.themeHost = t, this.root = null, this.disposers = [], this.ctx = null, this.onDocPointerDown = (e) => {
      this.root?.contains(e.target) || this.close();
    }, this.onKeyDown = (e) => {
      e.key === "Escape" && (e.stopPropagation(), this.close());
    };
  }
  get isOpen() {
    return this.root !== null;
  }
  open(t, e, s, i) {
    if (this.close(), t.length === 0) return;
    this.ctx = { ...i, close: () => this.close() };
    const o = l("div", "sp-menu");
    o.setAttribute("role", "menu"), o.dataset.theme = this.themeHost().dataset.theme ?? "dark", this.renderItems(o, t), document.body.appendChild(o), this.root = o, this.place(o, e, s), this.disposers.push(
      p(document, "pointerdown", this.onDocPointerDown, !0),
      p(document, "keydown", this.onKeyDown, !0),
      p(window, "resize", () => this.close()),
      p(window, "scroll", () => this.close(), !0),
      p(o, "contextmenu", (r) => r.preventDefault())
    );
  }
  close() {
    for (const t of this.disposers) t();
    this.disposers = [], this.root?.remove(), this.root = null, this.ctx = null;
  }
  renderItems(t, e) {
    for (const s of e) {
      if (s.divider) {
        l("div", "sp-menu__divider", t);
        continue;
      }
      const i = l("button", "sp-menu__item", t);
      i.type = "button", i.setAttribute("role", "menuitem"), s.disabled && (i.disabled = !0), s.danger && i.classList.add("is-danger");
      const o = l("span", "sp-menu__icon", i);
      E(o, s.icon);
      const r = l("span", "sp-menu__label", i);
      r.textContent = s.label, i.addEventListener("click", () => {
        if (s.disabled) return;
        const n = this.ctx;
        this.close(), s.onClick?.(n);
      });
    }
  }
  /** Position at (x, y), flipping back over the anchor when it overflows. */
  place(t, e, s) {
    t.style.visibility = "hidden", t.style.left = "0px", t.style.top = "0px";
    const { width: i, height: o } = t.getBoundingClientRect(), r = window.innerWidth - C, n = window.innerHeight - C;
    let a = e;
    a + i > r && (a = e - i), a = Math.max(C, Math.min(a, r - i));
    let c = s;
    c + o > n && (c = Math.max(C, n - o)), t.style.left = `${Math.round(a)}px`, t.style.top = `${Math.round(c)}px`, t.style.visibility = "";
  }
}
const P = [
  "January",
  "February",
  "March",
  "April",
  "May",
  "June",
  "July",
  "August",
  "September",
  "October",
  "November",
  "December"
], Y = {
  photos: "photos",
  photo: "photo",
  selected: "selected",
  selectAll: "Select all",
  deselectAll: "Deselect all",
  clearSelection: "Clear selection",
  favorite: "Add to favorites",
  unfavorite: "Remove from favorites",
  empty: "No photos",
  loading: "Loading…",
  loadFailed: "Failed to load",
  close: "Close",
  prev: "Previous",
  next: "Next",
  zoomIn: "Zoom in",
  zoomOut: "Zoom out",
  rotateLeft: "Rotate left",
  rotateRight: "Rotate right",
  actualSize: "Actual size",
  fitToWindow: "Fit to window",
  dateHeader: ({ year: h, month: t, day: e }) => `${P[t - 1]} ${e}, ${h}`,
  months: P,
  monthsShort: [
    "Jan",
    "Feb",
    "Mar",
    "Apr",
    "May",
    "Jun",
    "Jul",
    "Aug",
    "Sep",
    "Oct",
    "Nov",
    "Dec"
  ]
}, B = [
  "1月",
  "2月",
  "3月",
  "4月",
  "5月",
  "6月",
  "7月",
  "8月",
  "9月",
  "10月",
  "11月",
  "12月"
], j = {
  photos: "张照片",
  photo: "张照片",
  selected: "已选择",
  selectAll: "全选",
  deselectAll: "取消全选",
  clearSelection: "清空选择",
  favorite: "加入收藏",
  unfavorite: "取消收藏",
  empty: "暂无照片",
  loading: "加载中…",
  loadFailed: "加载失败",
  close: "关闭",
  prev: "上一张",
  next: "下一张",
  zoomIn: "放大",
  zoomOut: "缩小",
  rotateLeft: "向左旋转",
  rotateRight: "向右旋转",
  actualSize: "原始尺寸",
  fitToWindow: "适应窗口",
  dateHeader: ({ year: h, month: t, day: e }) => `${h}年${t}月${e}日`,
  months: B,
  monthsShort: B
}, J = {
  en: Y,
  "zh-CN": j
};
function A(h = "en", t) {
  return { ...J[h] ?? Y, ...t ?? {} };
}
const L = {
  gap: 4,
  targetRowHeight: 220,
  headerHeight: 44,
  groupSpacing: 20,
  lastRowMaxRatio: 1.5
};
function O(h, t, e, s) {
  const i = h - t * Math.max(0, s - 1);
  return i > 0 && e > 0 ? i / e : 0;
}
function $(h, t) {
  const { width: e, gap: s, targetRowHeight: i, headerHeight: o, groupSpacing: r } = t, n = [], a = [], c = [];
  if (e <= 0)
    return { totalHeight: 0, tiles: n, rows: a, headers: c, width: e };
  let d = 0;
  for (const u of h) {
    if (u.photos.length === 0) continue;
    const x = n.length;
    o > 0 && (c.push({
      key: u.key,
      y: d,
      height: o,
      group: u,
      from: x,
      to: x + u.photos.length
    }), d += o);
    let S = 0;
    for (; S < u.photos.length; ) {
      let M = 0, f = S, T = 0;
      for (; f < u.photos.length; ) {
        const w = M + u.photos[f].ratio, v = O(e, s, w, f - S + 1);
        if (v < i && f > S) {
          const H = O(e, s, M, f - S);
          Math.abs(v - i) < Math.abs(H - i) ? (M = w, f++, T = v) : T = H;
          break;
        }
        M = w, f++, T = v;
      }
      const R = !(f >= u.photos.length) || T <= i * t.lastRowMaxRatio, k = Math.round(R ? T : i), V = n.length;
      let F = 0;
      for (let w = S; w < f; w++) {
        const v = u.photos[w], H = w === f - 1;
        let z = Math.round(v.ratio * k);
        R && H && (z = Math.max(1, e - F)), n.push({
          key: v.key,
          x: F,
          y: d,
          width: z,
          height: k,
          photo: v
        }), F += z + s;
      }
      a.push({ y: d, height: k, from: V, to: n.length }), d += k + s, S = f;
    }
    d = d - s + r;
  }
  return { totalHeight: Math.max(0, d - r), tiles: n, rows: a, headers: c, width: e };
}
const g = (h, t = "") => `<svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"${t}>${h}</svg>`, m = {
  /** Filled when active, outlined otherwise — the tile favorite control. */
  heart: g(
    '<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8l1.1 1L12 21l7.7-7.6 1.1-1a5.5 5.5 0 0 0 0-7.8z"/>'
  ),
  heartFilled: g(
    '<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8l1.1 1L12 21l7.7-7.6 1.1-1a5.5 5.5 0 0 0 0-7.8z" fill="currentColor"/>'
  ),
  check: g('<path d="M20 6 9 17l-5-5"/>'),
  close: g('<path d="M18 6 6 18M6 6l12 12"/>'),
  chevronLeft: g('<path d="m15 18-6-6 6-6"/>'),
  chevronRight: g('<path d="m9 18 6-6-6-6"/>'),
  zoomIn: g(
    '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5M11 8v6M8 11h6"/>'
  ),
  zoomOut: g('<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5M8 11h6"/>'),
  rotateLeft: g('<path d="M3 5v6h6"/><path d="M3.5 11a9 9 0 1 1 2 7"/>'),
  rotateRight: g('<path d="M21 5v6h-6"/><path d="M20.5 11a9 9 0 1 0-2 7"/>'),
  actualSize: g(
    '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M8 9v6M8 12h4M16 9v6"/>'
  ),
  fit: g('<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>'),
  image: g(
    '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8.5" cy="8.5" r="1.5"/><path d="m21 15-5-5L5 21"/>'
  ),
  broken: g(
    '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 14h4l2-3 3 4 2-2 3 3h4"/>'
  )
}, _ = {
  tileFrom: 0,
  tileTo: 0,
  headerFrom: 0,
  headerTo: 0
};
function D(h, t) {
  let e = 0, s = h.length;
  for (; e < s; ) {
    const i = e + s >> 1;
    h[i].y + h[i].height <= t ? e = i + 1 : s = i;
  }
  return e;
}
function I(h, t, e) {
  let s = e, i = h.length;
  for (; s < i; ) {
    const o = s + i >> 1;
    h[o].y < t ? s = o + 1 : i = o;
  }
  return s;
}
function q(h, t, e, s) {
  const { rows: i, headers: o, tiles: r } = h;
  if (i.length === 0) return _;
  const n = t - s, a = t + e + s, c = D(i, n), d = I(i, a, c), y = o.length ? D(o, n) : 0, u = o.length ? I(o, a, y) : 0;
  return {
    tileFrom: c < i.length ? i[c].from : r.length,
    tileTo: d > c ? i[d - 1].to : c < i.length ? i[c].from : r.length,
    headerFrom: y,
    headerTo: u
  };
}
function Z(h, t) {
  return h.tileFrom === t.tileFrom && h.tileTo === t.tileTo && h.headerFrom === t.headerFrom && h.headerTo === t.headerTo;
}
function Q(h, t) {
  const e = D(h.rows, t), s = h.rows[e];
  if (!s) return null;
  const i = h.tiles[s.from];
  return i ? { key: i.key, offset: i.y - t } : null;
}
const tt = 40, et = 12, st = "dirty";
class it {
  constructor(t, e) {
    this.content = t, this.ctx = e, this.range = _, this.layout = null, this.mountedTiles = /* @__PURE__ */ new Map(), this.freeTiles = [], this.mountedHeaders = /* @__PURE__ */ new Map(), this.freeHeaders = [], this.pressTimer = 0, this.pressOrigin = null, this.swallowClick = !1, this.handlePointerDown = (s) => {
      if (this.cancelLongPress(), s.pointerType === "mouse" || s.target?.closest("[data-role]")) return;
      const i = this.tileFromEvent(s);
      if (!i) return;
      this.pressOrigin = { x: s.clientX, y: s.clientY };
      const { clientX: o, clientY: r } = s;
      this.pressTimer = window.setTimeout(() => {
        this.pressTimer = 0, this.pressOrigin = null, this.swallowClick = !0, this.ctx.onContextMenu(
          new MouseEvent("contextmenu", { clientX: o, clientY: r, bubbles: !1 }),
          i
        );
      }, 500);
    }, this.handlePointerMove = (s) => {
      if (!this.pressOrigin) return;
      const i = s.clientX - this.pressOrigin.x, o = s.clientY - this.pressOrigin.y;
      Math.hypot(i, o) > 10 && this.cancelLongPress();
    }, this.cancelLongPress = () => {
      this.pressTimer && clearTimeout(this.pressTimer), this.pressTimer = 0, this.pressOrigin = null;
    }, this.handleClick = (s) => {
      if (this.swallowClick) {
        this.swallowClick = !1, s.preventDefault(), s.stopPropagation();
        return;
      }
      const i = s.target?.closest("[data-role]")?.dataset.role;
      if (i === "group-select") {
        const r = this.groupFromEvent(s);
        r && (s.preventDefault(), this.ctx.onGroupToggle(r));
        return;
      }
      const o = this.tileFromEvent(s);
      if (o) {
        if (i === "select") {
          s.preventDefault(), s.stopPropagation(), this.ctx.onToggleSelect(o);
          return;
        }
        if (i === "favorite") {
          s.preventDefault(), s.stopPropagation(), this.ctx.onToggleFavorite(o);
          return;
        }
        if (i === "badge") {
          const r = s.target?.closest("[data-badge]")?.dataset.badge;
          r && (s.preventDefault(), s.stopPropagation(), this.badgeHandlerFor(o, r)?.(o.raw, s));
          return;
        }
        this.ctx.onTileClick(o, s);
      }
    }, this.handleContextMenu = (s) => {
      s.target?.closest(".sp-day") || this.ctx.onContextMenu(s, this.tileFromEvent(s));
    }, this.content.addEventListener("click", this.handleClick), this.content.addEventListener("contextmenu", this.handleContextMenu), this.content.addEventListener("pointerdown", this.handlePointerDown), this.content.addEventListener("pointermove", this.handlePointerMove), this.content.addEventListener("pointerup", this.cancelLongPress), this.content.addEventListener("pointercancel", this.cancelLongPress);
  }
  destroy() {
    this.cancelLongPress(), this.content.removeEventListener("click", this.handleClick), this.content.removeEventListener("contextmenu", this.handleContextMenu), this.content.removeEventListener("pointerdown", this.handlePointerDown), this.content.removeEventListener("pointermove", this.handlePointerMove), this.content.removeEventListener("pointerup", this.cancelLongPress), this.content.removeEventListener("pointercancel", this.cancelLongPress), this.content.textContent = "", this.mountedTiles.clear(), this.mountedHeaders.clear(), this.freeTiles = [], this.freeHeaders = [], this.layout = null, this.range = _;
  }
  setLayout(t) {
    this.layout = t, this.content.style.height = `${t.totalHeight}px`, this.range = _, this.recycleAll();
  }
  /** Render the window around `scrollTop`. Cheap to call on every scroll frame. */
  update(t, e, s) {
    const i = this.layout;
    if (!i) return;
    const o = q(i, t, e, s);
    Z(o, this.range) || (this.range = o, this.syncTiles(i, o), this.syncHeaders(i, o));
  }
  /** Re-read selection/favorite state for one photo, or all mounted ones. */
  refresh(t) {
    if (t) {
      const e = this.mountedTiles.get(t);
      e?.photo && this.paintState(e, e.photo), this.refreshHeadersFor(t);
      return;
    }
    for (const e of this.mountedTiles.values())
      e.photo && this.paintState(e, e.photo);
    for (const e of this.mountedHeaders.values())
      e.group && (e.label.textContent = this.ctx.messages().dateHeader(e.group), this.paintHeaderState(e, e.group));
  }
  /* --------------------------------------------------------------- tiles */
  syncTiles(t, e) {
    const s = /* @__PURE__ */ new Map();
    for (let i = e.tileFrom; i < e.tileTo; i++) {
      const o = t.tiles[i];
      o && s.set(o.key, o);
    }
    for (const [i, o] of this.mountedTiles)
      s.has(i) || this.releaseTile(i, o);
    for (const [i, o] of s) {
      const r = this.mountedTiles.get(i);
      if (r) {
        this.placeTile(r, o);
        continue;
      }
      const n = this.freeTiles.pop() ?? this.createTile();
      this.bindTile(n, o), this.mountedTiles.set(i, n), this.content.appendChild(n.root);
    }
  }
  createTile() {
    const t = l("figure", "sp-tile"), e = l("img", "sp-tile__img", t);
    e.decoding = "async", e.loading = "lazy", e.draggable = !1, e.addEventListener("load", () => t.classList.add("is-loaded")), e.addEventListener("error", () => t.classList.add("is-error")), l("span", "sp-tile__scrim", t);
    const s = l("button", "sp-tile__check", t);
    s.type = "button", s.dataset.role = "select", s.innerHTML = m.check;
    const i = l("button", "sp-tile__fav", t);
    return i.type = "button", i.dataset.role = "favorite", { root: t, img: e, check: s, fav: i, badgeHosts: {}, badgeKey: "", photo: null, src: "" };
  }
  /** Render consumer badges into their corners, reusing hosts across recycles. */
  paintBadges(t, e) {
    const s = this.ctx.badges?.(e.raw) || [], i = s.map((o) => `${o.corner}:${o.id}`).join("|");
    if (i !== t.badgeKey) {
      t.badgeKey = i;
      for (const o of Object.values(t.badgeHosts))
        o && (o.textContent = "");
      for (const o of s) {
        let r = t.badgeHosts[o.corner];
        r || (r = l("span", `sp-tile__slot sp-tile__slot--${o.corner}`, t.root), t.badgeHosts[o.corner] = r);
        const n = o.onClick ? "button" : "span", a = document.createElement(n);
        a.className = `sp-tile__badge${o.className ? ` ${o.className}` : ""}`, o.title && (a.title = o.title), n === "button" && (a.type = "button", a.dataset.role = "badge", a.dataset.badge = o.id), E(a, o.content), r.appendChild(a);
      }
    }
  }
  badgeHandlerFor(t, e) {
    return (this.ctx.badges?.(t.raw) || []).find((i) => i.id === e)?.onClick;
  }
  bindTile(t, e) {
    const s = e.photo;
    t.photo = s, t.root.dataset.key = s.key, t.root.classList.remove("is-error");
    const i = this.ctx.thumbUrl ? this.ctx.thumbUrl(s.raw, { width: e.width, height: e.height }) : s.thumbUrl;
    t.src !== i ? (t.root.classList.remove("is-loaded"), t.src = i, t.img.src = i, t.img.alt = s.alt) : t.img.complete && t.root.classList.add("is-loaded"), this.placeTile(t, e), this.paintState(t, s), this.paintBadges(t, s);
  }
  placeTile(t, e) {
    const s = t.root.style;
    s.transform = `translate3d(${e.x}px, ${e.y}px, 0)`, s.width = `${e.width}px`, s.height = `${e.height}px`;
  }
  paintState(t, e) {
    const { store: s } = this.ctx, i = this.ctx.messages(), o = this.ctx.selectable();
    if (t.check.hidden = !o, o) {
      const n = s.isSelected(e.key);
      t.root.classList.toggle("is-selected", n), t.check.setAttribute("aria-pressed", String(n)), t.check.title = n ? i.deselectAll : i.selectAll;
    } else
      t.root.classList.remove("is-selected");
    const r = this.ctx.favorite();
    if (t.fav.hidden = !r, r) {
      const n = s.isFavorite(e.key);
      t.fav.classList.toggle("is-active", n), t.fav.innerHTML = n ? m.heartFilled : m.heart, t.fav.setAttribute("aria-pressed", String(n)), t.fav.title = n ? i.unfavorite : i.favorite;
    }
  }
  releaseTile(t, e) {
    if (this.mountedTiles.delete(t), e.photo = null, e.badgeKey = st, e.root.remove(), this.freeTiles.length >= tt) {
      const s = this.freeTiles.shift();
      s && (s.img.removeAttribute("src"), s.src = "");
    }
    this.freeTiles.push(e);
  }
  /* -------------------------------------------------------------- headers */
  syncHeaders(t, e) {
    const s = /* @__PURE__ */ new Map();
    for (let i = e.headerFrom; i < e.headerTo; i++) {
      const o = t.headers[i];
      o && s.set(o.key, o);
    }
    for (const [i, o] of this.mountedHeaders)
      s.has(i) || (this.mountedHeaders.delete(i), o.group = null, o.root.remove(), this.pushHeader(o));
    for (const [i, o] of s) {
      let r = this.mountedHeaders.get(i);
      r || (r = this.freeHeaders.pop() ?? this.createHeader(), this.mountedHeaders.set(i, r), this.content.appendChild(r.root)), this.bindHeader(r, o);
    }
  }
  createHeader() {
    const t = l("div", "sp-day"), e = l("button", "sp-day__check", t);
    e.type = "button", e.dataset.role = "group-select", e.innerHTML = m.check;
    const s = l("span", "sp-day__label", t);
    return { root: t, check: e, label: s, group: null };
  }
  bindHeader(t, e) {
    t.group = e.group, t.root.dataset.key = e.key, t.root.style.transform = `translate3d(0, ${e.y}px, 0)`, t.root.style.height = `${e.height}px`, t.label.textContent = this.ctx.messages().dateHeader(e.group), this.paintHeaderState(t, e.group);
  }
  paintHeaderState(t, e) {
    const s = this.ctx.selectable();
    if (t.check.hidden = !s, !s) return;
    const i = this.ctx.store.groupSelectionState(e);
    t.root.classList.toggle("is-all", i === "all"), t.root.classList.toggle("is-some", i === "some"), t.check.setAttribute("aria-pressed", String(i === "all")), t.check.title = i === "all" ? this.ctx.messages().deselectAll : this.ctx.messages().selectAll;
  }
  refreshHeadersFor(t) {
    if (this.ctx.store.photoByKey(t))
      for (const s of this.mountedHeaders.values())
        s.group && s.group.photos.some((i) => i.key === t) && this.paintHeaderState(s, s.group);
  }
  /** Return a header node to the pool, keeping the pool bounded. */
  pushHeader(t) {
    this.freeHeaders.length >= et && this.freeHeaders.shift(), this.freeHeaders.push(t);
  }
  recycleAll() {
    for (const [t, e] of this.mountedTiles) this.releaseTile(t, e);
    for (const [t, e] of this.mountedHeaders)
      this.mountedHeaders.delete(t), e.group = null, e.root.remove(), this.pushHeader(e);
  }
  /* --------------------------------------------------------------- events */
  tileFromEvent(t) {
    const s = t.target?.closest(".sp-tile")?.dataset.key;
    return s ? this.ctx.store.photoByKey(s) ?? null : null;
  }
  groupFromEvent(t) {
    const s = t.target?.closest(".sp-day")?.dataset.key;
    return s ? this.mountedHeaders.get(s)?.group ?? null : null;
  }
}
const N = (h) => h < 10 ? `0${h}` : String(h);
function ot(h) {
  return `${h.year}-${N(h.month)}-${N(h.day)}`;
}
function rt(h) {
  const t = [];
  let e = null;
  for (const s of h) {
    const i = ot(s);
    (!e || e.key !== i) && (e = {
      key: i,
      year: s.year,
      month: s.month,
      day: s.day,
      photos: []
    }, t.push(e)), e.photos.push(s);
  }
  return t;
}
function nt(h) {
  if (h instanceof Date) return h.getTime();
  if (typeof h == "number") return h;
  if (typeof h == "string") {
    const t = /^(\d{4})-(\d{2})-(\d{2})$/.exec(h);
    if (t)
      return new Date(
        Number(t[1]),
        Number(t[2]) - 1,
        Number(t[3])
      ).getTime();
    const e = Date.parse(h);
    if (!Number.isNaN(e)) return e;
  }
  return NaN;
}
function ht(h, t = "desc") {
  const e = [], s = [], i = /* @__PURE__ */ new Set();
  for (const n of h) {
    if (!n || n.id === void 0 || n.id === null) {
      s.push({ item: n, reason: "missing id" });
      continue;
    }
    const a = String(n.id);
    if (i.has(a)) {
      s.push({ item: n, reason: `duplicate id "${a}"` });
      continue;
    }
    const c = Number(n.width), d = Number(n.height);
    if (!(c > 0) || !(d > 0)) {
      s.push({ item: n, reason: "width/height must be positive numbers" });
      continue;
    }
    const y = nt(n.takenAt);
    if (Number.isNaN(y)) {
      s.push({ item: n, reason: "takenAt could not be parsed" });
      continue;
    }
    if (!n.thumbUrl && !n.url) {
      s.push({ item: n, reason: "thumbUrl or url is required" });
      continue;
    }
    const u = new Date(y);
    i.add(a), e.push({
      raw: n,
      id: n.id,
      key: a,
      width: c,
      height: d,
      ratio: c / d,
      time: y,
      year: u.getFullYear(),
      month: u.getMonth() + 1,
      day: u.getDate(),
      thumbUrl: n.thumbUrl || n.url,
      url: n.url || n.thumbUrl,
      alt: n.alt ?? a,
      index: 0
    });
  }
  const o = t === "asc" ? 1 : -1;
  e.sort((n, a) => n.time !== a.time ? (n.time - a.time) * o : n.key < a.key ? -1 : n.key > a.key ? 1 : 0);
  const r = /* @__PURE__ */ new Map();
  return e.forEach((n, a) => {
    n.index = a, r.set(n.key, a);
  }), { photos: e, byKey: r, rejected: s };
}
class at {
  constructor() {
    this.photos = [], this.groups = [], this.byKey = /* @__PURE__ */ new Map(), this.selection = /* @__PURE__ */ new Set(), this.favorites = /* @__PURE__ */ new Set(), this.rejected = [];
  }
  setData(t, e) {
    const s = ht(t, e);
    this.photos = s.photos, this.byKey = s.byKey, this.rejected = s.rejected, this.groups = rt(s.photos), this.favorites.clear();
    for (const i of s.photos)
      i.raw.favorite && this.favorites.add(i.key);
    for (const i of [...this.selection])
      this.byKey.has(i) || this.selection.delete(i);
  }
  photoAt(t) {
    return this.photos[t];
  }
  photoByKey(t) {
    const e = this.byKey.get(t);
    return e === void 0 ? void 0 : this.photos[e];
  }
  /* ------------------------------------------------------------ selection */
  isSelected(t) {
    return this.selection.has(t);
  }
  toggleSelected(t, e) {
    const s = e ?? !this.selection.has(t);
    return s ? this.selection.add(t) : this.selection.delete(t), s;
  }
  /** `all` / `none` / `some` — drives the tri-state circle on a day header. */
  groupSelectionState(t) {
    let e = 0;
    for (const s of t.photos)
      this.selection.has(s.key) && e++;
    return e === 0 ? "none" : e === t.photos.length ? "all" : "some";
  }
  setGroupSelected(t, e) {
    for (const s of t.photos)
      e ? this.selection.add(s.key) : this.selection.delete(s.key);
  }
  clearSelection() {
    this.selection.clear();
  }
  /** Selected photos in timeline order. */
  selectedPhotos() {
    return this.selection.size === 0 ? [] : this.photos.filter((t) => this.selection.has(t.key));
  }
  /* ------------------------------------------------------------ favorites */
  isFavorite(t) {
    return this.favorites.has(t);
  }
  setFavorite(t, e) {
    e ? this.favorites.add(t) : this.favorites.delete(t);
  }
}
const lt = 22, ct = 90;
function dt(h, t, e = lt) {
  const s = h.length;
  if (s === 0) return [];
  if (t <= 0) return h.map((r) => ({ ...r, y: 0 }));
  const i = s > 1 ? Math.min(e, t / (s - 1)) : e, o = h.map((r) => ({ ...r, y: r.at * t }));
  for (let r = 1; r < s; r++)
    o[r].y - o[r - 1].y < i && (o[r].y = o[r - 1].y + i);
  if (o[s - 1].y > t) {
    o[s - 1].y = t;
    for (let r = s - 2; r >= 0; r--)
      o[r + 1].y - o[r].y < i && (o[r].y = o[r + 1].y - i);
  }
  if (o[0].y < 0) {
    o[0].y = 0;
    for (let r = 1; r < s; r++)
      o[r].y - o[r - 1].y < i && (o[r].y = o[r - 1].y + i);
  }
  return o;
}
class ut {
  constructor(t, e, s) {
    this.messages = e, this.onSeek = s, this.ticks = [], this.layout = null, this.disposers = [], this.dragging = !1, this.renderedAt = -1, this.observer = null, this.remeasureHandle = 0, this.remeasureTries = 0, this.onPointerDown = (i) => {
      i.preventDefault(), this.dragging = !0, this.root.setPointerCapture(i.pointerId), this.root.classList.add("is-dragging");
      const o = this.fractionAt(i.clientY);
      this.showBubble(i.clientY, o), this.onSeek(o);
    }, this.onPointerMove = (i) => {
      const o = this.fractionAt(i.clientY);
      this.showBubble(i.clientY, o), this.dragging && this.onSeek(o);
    }, this.onPointerUp = (i) => {
      this.dragging && (this.dragging = !1, this.root.hasPointerCapture(i.pointerId) && this.root.releasePointerCapture(i.pointerId), this.root.classList.remove("is-dragging"));
    }, this.onPointerLeave = () => {
      this.dragging || (this.bubble.hidden = !0);
    }, this.root = l("div", "sp-timeline", t), l("div", "sp-timeline__line", this.root), this.rail = l("div", "sp-timeline__rail", this.root), this.thumb = l("div", "sp-timeline__thumb", this.root), this.bubble = l("div", "sp-timeline__bubble", this.root), this.bubble.hidden = !0, typeof ResizeObserver < "u" && (this.observer = new ResizeObserver(() => {
      (this.rail.clientHeight || this.root.clientHeight) !== this.renderedAt && this.render();
    }), this.observer.observe(this.root)), this.disposers.push(
      p(this.root, "pointerdown", this.onPointerDown),
      p(this.root, "pointermove", this.onPointerMove),
      p(this.root, "pointerup", this.onPointerUp),
      p(this.root, "pointercancel", this.onPointerUp),
      p(this.root, "pointerleave", this.onPointerLeave)
    );
  }
  destroy() {
    for (const t of this.disposers) t();
    this.disposers = [], this.observer?.disconnect(), this.observer = null, this.remeasureHandle && cancelAnimationFrame(this.remeasureHandle), this.remeasureHandle = 0, this.root.remove();
  }
  setVisible(t) {
    this.root.hidden = !t;
  }
  /** Rebuild the year marks from a fresh layout. */
  setLayout(t) {
    this.layout = t, this.ticks = [];
    const e = t.totalHeight;
    if (e > 0 && t.headers.length > 0) {
      let s = null;
      for (const i of t.headers) {
        const { year: o } = i.group;
        o !== s && (s = o, this.ticks.push({ at: b(i.y / e, 0, 1), label: String(o) }));
      }
    }
    this.render();
  }
  /** Reflect the current scroll position on the rail. */
  setProgress(t) {
    this.thumb.style.top = `${b(t, 0, 1) * 100}%`;
  }
  render() {
    if (this.rail.textContent = "", this.ticks.length === 0) return;
    const t = this.rail.clientHeight || this.root.clientHeight;
    this.renderedAt = t;
    const e = (s, i) => {
      const o = l("span", "sp-timeline__tick", this.rail);
      o.style.top = i, o.textContent = s;
    };
    if (t <= 0) {
      for (const s of this.ticks) e(s.label, `${s.at * 100}%`);
      this.scheduleRemeasure();
      return;
    }
    this.remeasureTries = 0;
    for (const s of dt(this.ticks, t))
      e(s.label, `${Math.round(s.y)}px`);
  }
  /**
   * Re-render once the rail can actually be measured.
   *
   * The ResizeObserver normally catches this, but it is one asynchronous
   * notification and correctness should not hinge on it arriving — it does not
   * fire for elements with no box, and a missed one used to leave the labels
   * stuck in their unmeasured layout. Polling a bounded number of frames costs
   * nothing and does not depend on anything being delivered.
   */
  scheduleRemeasure() {
    this.remeasureHandle || this.remeasureTries >= ct || (this.remeasureHandle = requestAnimationFrame(() => {
      this.remeasureHandle = 0, this.remeasureTries++, (this.rail.clientHeight || this.root.clientHeight) !== this.renderedAt ? this.render() : this.scheduleRemeasure();
    }));
  }
  /** Re-run label thinning after the rail's pixel height changes. */
  relayout() {
    this.render();
  }
  fractionAt(t) {
    const e = this.root.getBoundingClientRect();
    return e.height <= 0 ? 0 : b((t - e.top) / e.height, 0, 1);
  }
  /** Full date of the group nearest the given rail position. */
  labelAt(t) {
    const e = this.layout;
    if (!e || e.headers.length === 0) return "";
    const s = t * e.totalHeight;
    let i = 0, o = e.headers.length - 1;
    for (; i < o; ) {
      const r = i + o + 1 >> 1;
      e.headers[r].y <= s ? i = r : o = r - 1;
    }
    return this.messages().dateHeader(e.headers[i].group);
  }
  showBubble(t, e) {
    const s = this.root.getBoundingClientRect();
    this.bubble.hidden = !1, this.bubble.textContent = this.labelAt(e), this.bubble.style.top = `${b(t - s.top, 12, s.height - 12)}px`;
  }
}
class pt {
  constructor(t, e) {
    this.ctx = e, this.renderedKey = "", this.root = l("div", "sp-toolbar", t), this.count = l("span", "sp-toolbar__count", this.root), this.actionsHost = l("div", "sp-toolbar__actions", this.root), this.clearBtn = l("button", "sp-toolbar__btn", this.root), this.clearBtn.type = "button", this.clearBtn.innerHTML = m.close, l("span", "", this.clearBtn), this.clearBtn.addEventListener("click", e.onClear);
  }
  setVisible(t) {
    this.root.hidden = !t;
  }
  update(t, e) {
    const s = this.ctx.messages(), i = e > 0;
    this.root.classList.toggle("is-selecting", i), this.count.textContent = i ? `${e} ${s.selected}` : `${t} ${t === 1 ? s.photo : s.photos}`, this.clearBtn.hidden = !i, this.clearBtn.title = s.clearSelection;
    const o = this.clearBtn.querySelector("span");
    o && (o.textContent = s.clearSelection), this.renderActions(i);
  }
  renderActions(t) {
    if (!t) {
      this.renderedKey !== "" && (this.actionsHost.textContent = "", this.renderedKey = "");
      return;
    }
    const e = this.ctx.selected(), s = this.ctx.actions(e), i = s.map((o) => `${o.id}:${o.label}:${o.disabled ? 1 : 0}`).join("|");
    if (i !== this.renderedKey) {
      this.renderedKey = i, this.actionsHost.textContent = "";
      for (const o of s) {
        const r = l("button", "sp-toolbar__btn", this.actionsHost);
        if (r.type = "button", r.title = o.label, o.danger && r.classList.add("is-danger"), o.disabled && (r.disabled = !0), o.icon) {
          const n = l("span", "sp-toolbar__icon", r);
          E(n, o.icon);
        }
        l("span", "", r).textContent = o.label, r.addEventListener("click", () => {
          o.disabled || o.onClick({
            selected: this.ctx.selected(),
            clearSelection: this.ctx.onClear
          });
        });
      }
    }
  }
  destroy() {
    this.root.remove();
  }
}
class gt {
  constructor() {
    this.map = /* @__PURE__ */ new Map();
  }
  on(t, e) {
    let s = this.map.get(t);
    return s || this.map.set(t, s = /* @__PURE__ */ new Set()), s.add(e), () => this.off(t, e);
  }
  off(t, e) {
    if (!e) {
      this.map.delete(t);
      return;
    }
    this.map.get(t)?.delete(e);
  }
  emit(t, ...e) {
    const s = this.map.get(t);
    if (s)
      for (const i of [...s])
        try {
          i(...e);
        } catch (o) {
          t !== "error" && this.emit("error", o instanceof Error ? o : new Error(String(o)));
        }
  }
  clear() {
    this.map.clear();
  }
}
function mt(h, t) {
  let e = 0, s = 0, i = null;
  const o = (n) => {
    e = Date.now(), i = null, h(...n);
  }, r = (...n) => {
    const a = Date.now() - e;
    if (i = n, a >= t) {
      s && (clearTimeout(s), s = 0), o(n);
      return;
    }
    s || (s = window.setTimeout(() => {
      s = 0, i && o(i);
    }, t - a));
  };
  return r.cancel = () => {
    s && clearTimeout(s), s = 0, i = null;
  }, r;
}
function X(h) {
  let t = 0, e = null;
  const s = (...i) => {
    e = i, !t && (t = requestAnimationFrame(() => {
      t = 0;
      const o = e;
      e = null, h(...o);
    }));
  };
  return s.cancel = () => {
    t && cancelAnimationFrame(t), t = 0, e = null;
  }, s;
}
const ft = 0.35, yt = 90, bt = 0.5;
class wt {
  constructor(t, e, s, i, o) {
    this.stage = t, this.onChange = e, this.onDoubleTap = s, this.zoomStep = i, this.onPanEnd = o, this.transform = { scale: 1, x: 0, y: 0, rotation: 0 }, this.limits = { minScale: 0.1, maxScale: 8 }, this.content = { width: 1, height: 1 }, this.disposers = [], this.pointers = /* @__PURE__ */ new Map(), this.panFrom = null, this.pinchFrom = null, this.rubberBand = !1, this.onPointerDown = (r) => {
      if (!(r.button !== void 0 && r.button !== 0)) {
        try {
          this.stage.setPointerCapture(r.pointerId);
        } catch {
        }
        if (this.pointers.set(r.pointerId, { x: r.clientX, y: r.clientY }), this.pointers.size === 2) {
          this.panFrom = null, this.pinchFrom = { dist: this.pointerDistance(), scale: this.transform.scale };
          return;
        }
        this.rubberBand = r.pointerType === "touch", this.panFrom = {
          x: r.clientX,
          y: r.clientY,
          tx: this.transform.x,
          ty: this.transform.y
        }, this.stage.classList.add("is-panning");
      }
    }, this.onPointerMove = (r) => {
      if (this.pointers.has(r.pointerId)) {
        if (this.pointers.set(r.pointerId, { x: r.clientX, y: r.clientY }), this.pinchFrom && this.pointers.size >= 2) {
          const n = this.pointerDistance();
          if (this.pinchFrom.dist > 0) {
            const a = this.pointerMidpoint();
            this.zoomAt(
              this.pinchFrom.scale * n / this.pinchFrom.dist,
              a.x,
              a.y
            );
          }
          return;
        }
        this.panFrom && (r.preventDefault(), this.set({
          x: this.panFrom.tx + (r.clientX - this.panFrom.x),
          y: this.panFrom.ty + (r.clientY - this.panFrom.y)
        }));
      }
    }, this.onPointerUp = (r) => {
      const n = this.panFrom;
      this.pointers.delete(r.pointerId);
      try {
        this.stage.hasPointerCapture?.(r.pointerId) && this.stage.releasePointerCapture(r.pointerId);
      } catch {
      }
      if (this.pointers.size < 2 && (this.pinchFrom = null), this.pointers.size === 0) {
        this.panFrom = null, this.stage.classList.remove("is-panning");
        const a = this.rubberBand;
        this.rubberBand = !1, n && this.onPanEnd?.(r.clientX - n.x, r.clientY - n.y, a);
      }
    }, this.onWheel = (r) => {
      r.preventDefault();
      const n = r.deltaMode === 1 ? 16 : r.deltaMode === 2 ? 100 : 1, a = r.deltaY * n, c = Math.pow(this.zoomStep, -a / 100);
      this.zoomAt(this.transform.scale * c, r.clientX, r.clientY);
    }, this.disposers.push(
      p(t, "pointerdown", this.onPointerDown),
      p(t, "pointermove", this.onPointerMove),
      p(t, "pointerup", this.onPointerUp),
      p(t, "pointercancel", this.onPointerUp),
      p(t, "wheel", this.onWheel, { passive: !1 }),
      p(t, "dblclick", (r) => {
        r.preventDefault(), this.onDoubleTap(r.clientX, r.clientY);
      })
    );
  }
  destroy() {
    for (const t of this.disposers) t();
    this.disposers = [], this.pointers.clear();
  }
  get value() {
    return { ...this.transform };
  }
  setLimits(t) {
    this.limits = t;
  }
  setContentSize(t, e) {
    this.content = { width: Math.max(1, t), height: Math.max(1, e) };
  }
  set(t, e = !0) {
    const s = { ...this.transform, ...t };
    s.scale = b(s.scale, this.limits.minScale, this.limits.maxScale), this.transform = e ? this.clamp(s) : s, this.onChange(this.value);
  }
  /** Zoom to `scale`, keeping the given client point pinned. */
  zoomAt(t, e, s) {
    const i = b(t, this.limits.minScale, this.limits.maxScale), o = this.transform.scale;
    if (i === o) return;
    const r = this.stage.getBoundingClientRect(), n = e - (r.left + r.width / 2), a = s - (r.top + r.height / 2), c = i / o;
    this.set({
      scale: i,
      x: n * (1 - c) + this.transform.x * c,
      y: a * (1 - c) + this.transform.y * c
    });
  }
  zoomBy(t, e, s) {
    const i = this.stage.getBoundingClientRect();
    this.zoomAt(
      this.transform.scale * t,
      e ?? i.left + i.width / 2,
      s ?? i.top + i.height / 2
    );
  }
  /** Rotated bounding box of the content at the current scale. */
  rotatedSize() {
    const t = this.transform.rotation * Math.PI / 180, e = Math.abs(Math.cos(t)), s = Math.abs(Math.sin(t)), { width: i, height: o } = this.content;
    return {
      width: i * e + o * s,
      height: i * s + o * e
    };
  }
  /**
   * Bound the image's travel.
   *
   * An axis with overflow — the image is larger than the stage — clamps to the
   * image edges, so panning a zoomed photo never reveals empty space.
   *
   * An axis with none needs a choice. Clamping to 0 makes a drag look broken.
   * Pointer drags therefore move freely and stay where they are dropped
   * (`rubberBand` off), while touch drags rubber-band, because there the
   * gesture doubles as swipe-to-navigate and has to spring back when it does
   * not commit.
   */
  clamp(t) {
    const e = this.stage.getBoundingClientRect(), s = this.rotatedSize(), i = Math.max(0, (s.width * t.scale - e.width) / 2), o = Math.max(0, (s.height * t.scale - e.height) / 2), r = (n, a, c) => {
      if (a > 0) return b(n, -a, a);
      if (this.rubberBand)
        return Math.sign(n) * Math.min(Math.abs(n) * ft, yt);
      const d = c * bt;
      return b(n, -d, d);
    };
    return {
      ...t,
      x: r(t.x, i, e.width),
      y: r(t.y, o, e.height)
    };
  }
  /** Re-apply clamping, e.g. after the stage resized. */
  reclamp() {
    this.transform = this.clamp(this.transform), this.onChange(this.value);
  }
  pointerDistance() {
    const [t, e] = [...this.pointers.values()];
    return !t || !e ? 0 : Math.hypot(t.x - e.x, t.y - e.y);
  }
  pointerMidpoint() {
    const [t, e] = [...this.pointers.values()];
    return !t || !e ? { x: 0, y: 0 } : { x: (t.x + e.x) / 2, y: (t.y + e.y) / 2 };
  }
}
const vt = [
  "rotateLeft",
  "rotateRight",
  "divider",
  "zoomOut",
  "zoomLevel",
  "zoomIn",
  "divider",
  "actualSize",
  "fit"
];
class xt {
  constructor(t, e, s, i) {
    this.messages = e, this.controls = s, this.entries = [], this.root = l("div", "sp-viewer__toolbar", t), this.root.setAttribute("role", "toolbar");
    for (const o of i) this.add(o);
  }
  destroy() {
    this.root.remove(), this.entries = [];
  }
  update(t) {
    for (const e of this.entries)
      e.refresh?.(t), e.action?.disabled !== void 0 && (e.node.disabled = e.action.disabled);
  }
  add(t) {
    if (typeof t == "string") {
      this.addBuiltin(t);
      return;
    }
    const e = this.button(t.title ?? t.id);
    E(e, t.icon), e.addEventListener(
      "click",
      () => t.onClick?.({ state: this.controls.getState(), controls: this.controls })
    ), this.entries.push({ id: t.id, node: e, action: t });
  }
  addBuiltin(t) {
    const e = this.messages();
    if (t === "divider") {
      const r = l("span", "sp-viewer__divider", this.root);
      this.entries.push({ id: t, node: r });
      return;
    }
    if (t === "zoomLevel") {
      const r = l("span", "sp-viewer__zoom", this.root);
      r.textContent = "100%", this.entries.push({
        id: t,
        node: r,
        refresh: (n) => {
          r.textContent = `${Math.round(n.scale * 100)}%`;
        }
      });
      return;
    }
    const i = {
      rotateLeft: {
        icon: m.rotateLeft,
        title: e.rotateLeft,
        run: () => this.controls.rotate(-90)
      },
      rotateRight: {
        icon: m.rotateRight,
        title: e.rotateRight,
        run: () => this.controls.rotate(90)
      },
      zoomOut: {
        icon: m.zoomOut,
        title: e.zoomOut,
        run: () => this.controls.zoomOut()
      },
      zoomIn: {
        icon: m.zoomIn,
        title: e.zoomIn,
        run: () => this.controls.zoomIn()
      },
      actualSize: {
        icon: m.actualSize,
        title: e.actualSize,
        run: () => this.controls.actualSize()
      },
      fit: {
        icon: m.fit,
        title: e.fitToWindow,
        run: () => this.controls.fit()
      }
    }[t];
    if (!i) return;
    const o = this.button(i.title);
    o.innerHTML = i.icon, o.addEventListener("click", i.run), this.entries.push({ id: t, node: o });
  }
  button(t) {
    const e = l("button", "sp-viewer__btn", this.root);
    return e.type = "button", e.title = t, e.setAttribute("aria-label", t), e;
  }
}
const St = 60, Lt = 4, K = {
  initialFit: "contain",
  maxScale: 8,
  zoomStep: 1.25,
  preload: 1,
  arrows: !0,
  // Off by default: the stage is a drag surface, and a drag that ends outside
  // the image delivers its click to the stage — closing the viewer mid-gesture.
  closeOnBackdrop: !1
};
class Tt {
  constructor(t, e = {}) {
    this.host = t, this.root = null, this.dragged = !1, this.prevBtn = null, this.nextBtn = null, this.toolbar = null, this.gesture = null, this.index = -1, this.natural = { width: 0, height: 0 }, this.disposers = [], this.preloaded = /* @__PURE__ */ new Set(), this.lastFocus = null, this.onImageLoad = () => {
      this.root?.classList.remove("is-loading");
      const s = this.img.naturalWidth, i = this.img.naturalHeight;
      s > 0 && i > 0 && (s !== this.natural.width || i !== this.natural.height) && (this.natural = { width: s, height: i }, this.gesture?.setContentSize(s, i), this.fit());
    }, this.onImageError = () => {
      this.root?.classList.remove("is-loading"), this.root?.classList.add("is-error"), this.status.removeAttribute("aria-label"), this.statusText.textContent = this.host.messages().loadFailed;
    }, this.applyTransform = (s) => {
      this.img.style.transform = `translate3d(${s.x}px, ${s.y}px, 0) scale(${s.scale}) rotate(${s.rotation}deg)`, this.img.style.width = `${this.natural.width}px`, this.img.style.height = `${this.natural.height}px`, this.toolbar?.update(this.state());
    }, this.onPanEnd = (s, i, o) => {
      if (this.dragged = Math.hypot(s, i) > Lt, !(!o || (this.gesture?.value.scale ?? 1) > this.fitScale() * 1.05)) {
        if (Math.abs(s) >= St && Math.abs(s) > Math.abs(i)) {
          s < 0 ? this.next() : this.prev();
          return;
        }
        this.settle();
      }
    }, this.toggleZoomAt = (s, i) => {
      const o = this.fitScale(), r = this.gesture?.value.scale ?? 1, n = Math.abs(r - o) < 0.01 ? 1 : o;
      this.gesture?.zoomAt(n, s, i);
    }, this.fit = () => {
      const s = this.fitScale();
      this.gesture?.setLimits({
        // Never let the user zoom below "fits the window".
        minScale: Math.min(s, 1) * 0.5,
        maxScale: this.opts.maxScale
      }), this.gesture?.set({ scale: s, x: 0, y: 0 });
    }, this.onResize = X(() => {
      this.root && this.fit();
    }), this.controls = {
      zoomIn: (s) => this.gesture?.zoomBy(s ?? this.opts.zoomStep),
      zoomOut: (s) => this.gesture?.zoomBy(1 / (s ?? this.opts.zoomStep)),
      zoomTo: (s) => this.gesture?.set({ scale: s }),
      rotate: (s) => {
        const i = ((this.gesture?.value.rotation ?? 0) + s) % 360;
        this.gesture?.set({ rotation: i, x: 0, y: 0 }, !1);
        const o = this.fitScale(i);
        this.gesture?.setLimits({
          minScale: Math.min(o, 1) * 0.5,
          maxScale: this.opts.maxScale
        }), this.gesture?.set({ scale: o });
      },
      reset: () => this.fit(),
      fit: () => this.fit(),
      actualSize: () => this.gesture?.set({ scale: 1, x: 0, y: 0 }),
      next: () => this.next(),
      prev: () => this.prev(),
      close: () => this.close(),
      getState: () => this.state()
    }, this.onKeyDown = (s) => {
      switch (s.key) {
        case "Escape":
          s.preventDefault(), this.close();
          break;
        case "ArrowLeft":
          s.preventDefault(), this.prev();
          break;
        case "ArrowRight":
          s.preventDefault(), this.next();
          break;
        case "+":
        case "=":
          s.preventDefault(), this.controls.zoomIn();
          break;
        case "-":
        case "_":
          s.preventDefault(), this.controls.zoomOut();
          break;
        case "0":
          s.preventDefault(), this.fit();
          break;
        case "1":
          s.preventDefault(), this.controls.actualSize();
          break;
      }
    }, this.opts = { ...K, ...e };
  }
  get isOpen() {
    return this.root !== null;
  }
  get currentIndex() {
    return this.index;
  }
  setOptions(t) {
    this.opts = { ...K, ...t };
  }
  /* ----------------------------------------------------------- lifecycle */
  open(t) {
    const e = this.host.photos();
    t < 0 || t >= e.length || (this.root || this.mount(), this.show(t));
  }
  close() {
    if (this.root) {
      for (const t of this.disposers) t();
      this.disposers = [], this.gesture?.destroy(), this.gesture = null, this.toolbar?.destroy(), this.toolbar = null, this.root.remove(), this.root = null, this.index = -1, this.preloaded.clear(), document.documentElement.classList.remove("sp-lock-scroll"), this.lastFocus instanceof HTMLElement && this.lastFocus.focus({ preventScroll: !0 }), this.lastFocus = null, this.host.onClose();
    }
  }
  next() {
    this.show(this.index + 1);
  }
  prev() {
    this.show(this.index - 1);
  }
  mount() {
    this.lastFocus = document.activeElement;
    const t = this.host.messages(), e = l("div", "sp-viewer");
    e.dataset.theme = this.host.theme(), e.tabIndex = -1, e.setAttribute("role", "dialog"), e.setAttribute("aria-modal", "true");
    const s = l("div", "sp-viewer__backdrop", e);
    this.stage = l("div", "sp-viewer__stage", e), this.img = l("img", "sp-viewer__img", this.stage), this.img.draggable = !1, this.img.alt = "", this.img.addEventListener("load", this.onImageLoad), this.img.addEventListener("error", this.onImageError), this.status = l("div", "sp-viewer__status", this.stage), this.status.setAttribute("role", "status"), this.dots = l("div", "sp-viewer__dots", this.status);
    for (let o = 0; o < 3; o++) l("span", "", this.dots);
    this.statusText = l("span", "sp-viewer__status-text", this.status);
    const i = l("button", "sp-viewer__close", e);
    i.type = "button", i.title = t.close, i.setAttribute("aria-label", t.close), i.innerHTML = m.close, i.addEventListener("click", () => this.close()), this.counter = l("div", "sp-viewer__counter", e), this.opts.arrows && (this.prevBtn = l("button", "sp-viewer__nav sp-viewer__nav--prev", e), this.prevBtn.type = "button", this.prevBtn.title = t.prev, this.prevBtn.setAttribute("aria-label", t.prev), this.prevBtn.innerHTML = m.chevronLeft, this.prevBtn.addEventListener("click", () => this.prev()), this.nextBtn = l("button", "sp-viewer__nav sp-viewer__nav--next", e), this.nextBtn.type = "button", this.nextBtn.title = t.next, this.nextBtn.setAttribute("aria-label", t.next), this.nextBtn.innerHTML = m.chevronRight, this.nextBtn.addEventListener("click", () => this.next())), document.body.appendChild(e), this.root = e, document.documentElement.classList.add("sp-lock-scroll"), this.gesture = new wt(
      this.stage,
      this.applyTransform,
      this.toggleZoomAt,
      this.opts.zoomStep,
      this.onPanEnd
    ), this.toolbar = new xt(
      e,
      this.host.messages,
      this.controls,
      this.opts.actions ?? vt
    ), this.disposers.push(
      p(e, "keydown", this.onKeyDown),
      p(window, "resize", this.onResize),
      p(s, "click", () => {
        this.opts.closeOnBackdrop && !this.dragged && this.close();
      }),
      p(this.stage, "click", (o) => {
        o.target !== this.stage || this.dragged || this.opts.closeOnBackdrop && this.close();
      })
    ), e.focus({ preventScroll: !0 });
  }
  /* -------------------------------------------------------------- display */
  show(t) {
    const e = this.host.photos();
    if (t < 0 || t >= e.length) return;
    const s = e[t];
    this.index = t, this.natural = { width: s.width, height: s.height }, this.root?.classList.remove("is-error"), this.root?.classList.add("is-loading"), this.status.setAttribute("aria-label", this.host.messages().loading), this.statusText.textContent = "", this.img.alt = s.alt, this.img.src = s.url, this.gesture?.setContentSize(s.width, s.height), this.gesture?.set({ rotation: 0, x: 0, y: 0 }, !1), this.fit(), this.img.complete && this.img.naturalWidth > 0 && this.onImageLoad(), this.counter.textContent = `${t + 1} / ${e.length}`, this.prevBtn && (this.prevBtn.disabled = t === 0), this.nextBtn && (this.nextBtn.disabled = t === e.length - 1), this.host.onOpen(s.raw, t), this.preload(t);
  }
  preload(t) {
    const e = this.host.photos();
    for (let s = 1; s <= this.opts.preload; s++)
      for (const i of [t - s, t + s]) {
        const o = e[i];
        if (!o || this.preloaded.has(o.key)) continue;
        this.preloaded.add(o.key);
        const r = new Image();
        r.decoding = "async", r.src = o.url;
      }
  }
  /* ------------------------------------------------------------ transform */
  /** Scale at which the image exactly fits the stage without cropping. */
  fitScale(t = this.gesture?.value.rotation ?? 0) {
    const e = this.stage.getBoundingClientRect(), s = Math.abs(t % 180) === 90, i = s ? this.natural.height : this.natural.width, o = s ? this.natural.width : this.natural.height;
    if (!i || !o || !e.width || !e.height) return 1;
    const r = Math.min(e.width / i, e.height / o);
    return this.opts.initialFit === "no-upscale" ? Math.min(1, r) : r;
  }
  /** Animate any rubber-band offset back to centre. */
  settle() {
    const t = this.gesture?.value;
    !t || t.x === 0 && t.y === 0 || (this.img.classList.add("is-settling"), this.gesture?.set({ x: 0, y: 0 }), window.setTimeout(() => this.img.classList.remove("is-settling"), 220));
  }
  state() {
    const t = this.host.photos(), e = t[this.index], s = this.gesture?.value ?? { scale: 1, x: 0, y: 0, rotation: 0 };
    return {
      item: e?.raw,
      index: this.index,
      total: t.length,
      scale: s.scale,
      rotation: s.rotation,
      x: s.x,
      y: s.y
    };
  }
}
const U = {
  order: "desc",
  gap: L.gap,
  targetRowHeight: L.targetRowHeight,
  headerHeight: L.headerHeight,
  groupSpacing: L.groupSpacing,
  overscan: 2,
  selectable: !0,
  favorite: !0,
  header: !0,
  timeline: !0,
  locale: "en",
  theme: "dark"
}, Mt = {
  totalHeight: 0,
  tiles: [],
  rows: [],
  headers: [],
  width: 0
};
class Ht {
  constructor(t, e = {}) {
    this.store = new at(), this.emitter = new gt(), this.toolbar = null, this.timeline = null, this.layout = Mt, this.resizeObserver = null, this.disposers = [], this.destroyed = !1, this.pendingLayout = !1, this.onScroll = X(() => {
      this.destroyed || this.paint();
    }), this.onResize = mt(() => {
      this.destroyed || (this.relayout(), this.timeline?.relayout());
    }, 100), this.onContainerResize = () => {
      if (!this.destroyed) {
        if (this.pendingLayout) {
          this.relayout(!0), this.timeline?.relayout();
          return;
        }
        this.onResize();
      }
    };
    const s = typeof t == "string" ? document.querySelector(t) : t;
    if (!s) throw new Error(`[sweet-album] container not found: ${t}`);
    this.options = { ...e }, this.messages = A(this.opt("locale"), e.messages), this.root = l("div", "sweet-album"), this.root.dataset.theme = this.opt("theme"), s.appendChild(this.root), this.menu = new W(() => this.root), this.viewer = new Tt(
      {
        photos: () => this.store.photos,
        messages: () => this.messages,
        theme: () => this.root.dataset.theme ?? "dark",
        onOpen: (i, o) => this.emit("viewerOpen", i, o),
        onClose: () => this.emit("viewerClose")
      },
      this.viewerOptions()
    ), this.buildDom(), e.data ? this.setData(e.data) : this.applyData([]);
  }
  /* ----------------------------------------------------------------- dom */
  buildDom() {
    this.opt("header") && (this.toolbar = new pt(this.root, {
      messages: () => this.messages,
      selected: () => this.store.selectedPhotos().map((t) => t.raw),
      actions: (t) => {
        const e = this.options.selectionActions;
        return e ? typeof e == "function" ? e(t) : e : [];
      },
      onClear: () => this.clearSelection()
    })), this.body = l("div", "sp-body", this.root), this.scroller = l("div", "sp-scroller", this.body), this.scroller.tabIndex = 0, this.content = l("div", "sp-content", this.scroller), this.empty = l("div", "sp-empty", this.scroller), this.empty.hidden = !0, this.renderer = new it(this.content, {
      store: this.store,
      messages: () => this.messages,
      selectable: () => this.opt("selectable"),
      favorite: () => this.opt("favorite"),
      thumbUrl: this.options.thumbUrl,
      badges: this.options.badges ? (t) => this.options.badges(t) : void 0,
      onTileClick: (t, e) => this.handleTileClick(t, e),
      onToggleSelect: (t) => this.toggleSelection(t.key),
      onToggleFavorite: (t) => void this.toggleFavorite(t.key),
      onGroupToggle: (t) => this.toggleGroup(t),
      onContextMenu: (t, e) => this.handleContextMenu(t, e)
    }), this.opt("timeline") && (this.timeline = new ut(
      this.body,
      () => this.messages,
      (t) => this.seek(t)
    )), this.scroller.addEventListener("scroll", this.onScroll, { passive: !0 }), this.disposers.push(
      () => this.scroller.removeEventListener("scroll", this.onScroll)
    ), typeof ResizeObserver < "u" ? (this.resizeObserver = new ResizeObserver(this.onContainerResize), this.resizeObserver.observe(this.scroller)) : (window.addEventListener("resize", this.onContainerResize), this.disposers.push(
      () => window.removeEventListener("resize", this.onContainerResize)
    ));
  }
  /* ---------------------------------------------------------------- data */
  /** Replace the timeline. Accepts an array or a (possibly async) provider. */
  async setData(t) {
    try {
      const e = typeof t == "function" ? await t() : t;
      if (this.destroyed) return;
      this.applyData(e ?? []);
    } catch (e) {
      this.fail(e);
    }
  }
  applyData(t) {
    this.store.setData(t, this.opt("order"));
    for (const { item: e, reason: s } of this.store.rejected)
      this.fail(new Error(`[sweet-album] skipped photo ${String(e?.id)}: ${s}`));
    this.empty.hidden = this.store.photos.length > 0, this.empty.textContent = this.messages.empty, this.relayout(!0), this.syncToolbar(), this.emit("ready", { count: this.store.photos.length });
  }
  /* -------------------------------------------------------------- layout */
  /** Recompute geometry. Preserves the photo at the top of the viewport. */
  relayout(t = !1) {
    const e = this.content.clientWidth;
    if (e <= 0) {
      this.pendingLayout = !0;
      return;
    }
    if (this.pendingLayout = !1, !t && e === this.layout.width) return;
    const s = t ? null : Q(this.layout, this.scroller.scrollTop);
    this.layout = $(this.store.groups, {
      width: e,
      gap: this.opt("gap"),
      targetRowHeight: this.rowHeightFor(e),
      headerHeight: this.opt("headerHeight"),
      groupSpacing: this.opt("groupSpacing"),
      lastRowMaxRatio: L.lastRowMaxRatio
    }), this.renderer.setLayout(this.layout);
    const i = this.content.clientWidth;
    if (i > 0 && i !== e && (this.layout = $(this.store.groups, {
      width: i,
      gap: this.opt("gap"),
      targetRowHeight: this.rowHeightFor(i),
      headerHeight: this.opt("headerHeight"),
      groupSpacing: this.opt("groupSpacing"),
      lastRowMaxRatio: L.lastRowMaxRatio
    }), this.renderer.setLayout(this.layout)), this.timeline?.setLayout(this.layout), this.timeline?.setVisible(
      this.opt("timeline") && this.layout.totalHeight > this.scroller.clientHeight
    ), s) {
      const o = this.store.photoByKey(s.key), r = o ? this.tileFor(o) : null;
      r && (this.scroller.scrollTop = Math.max(0, r.y - s.offset));
    }
    this.paint();
  }
  /**
   * Row height to lay out at.
   *
   * When the caller has not pinned `targetRowHeight`, scale it to the container
   * so a phone gets roughly three photos per row instead of one and a half.
   * An explicit option always wins.
   */
  rowHeightFor(t) {
    const e = this.options.targetRowHeight;
    return e !== void 0 ? e : Math.round(b(t / 3, 120, U.targetRowHeight));
  }
  tileFor(t) {
    const e = this.layout.tiles[t.index];
    return e?.key === t.key ? e : this.layout.tiles.find((s) => s.key === t.key) ?? null;
  }
  paint() {
    const t = this.opt("overscan") * this.rowHeightFor(this.layout.width);
    this.renderer.update(this.scroller.scrollTop, this.scroller.clientHeight, t), this.layout.totalHeight > 0 && this.timeline?.setProgress(this.scroller.scrollTop / this.layout.totalHeight);
  }
  /* ----------------------------------------------------------- selection */
  handleTileClick(t, e) {
    this.emit("itemClick", t.raw, t.index, e), !e.defaultPrevented && this.options.viewer !== !1 && this.viewer.open(t.index);
  }
  toggleSelection(t) {
    this.store.toggleSelected(t), this.renderer.refresh(t), this.syncToolbar(), this.emitSelection();
  }
  toggleGroup(t) {
    const e = this.store.groupSelectionState(t) !== "all";
    this.store.setGroupSelected(t, e), this.renderer.refresh(), this.syncToolbar(), this.emitSelection();
  }
  emitSelection() {
    const t = this.store.selectedPhotos();
    this.emit(
      "selectionChange",
      t.map((e) => e.id),
      t.map((e) => e.raw)
    );
  }
  syncToolbar() {
    this.toolbar?.setVisible(this.opt("header")), this.toolbar?.update(this.store.photos.length, this.store.selection.size), this.root.classList.toggle("is-selecting", this.store.selection.size > 0);
  }
  /* ----------------------------------------------------------- favorites */
  async toggleFavorite(t) {
    const e = this.store.photoByKey(t);
    if (!e) return;
    const s = this.store.isFavorite(t), i = !s;
    this.store.setFavorite(t, i), this.renderer.refresh(t), this.emit("favoriteToggle", e.raw, i);
    const o = this.options.onFavoriteToggle?.(e.raw, i);
    if (!(!o || typeof o.then != "function"))
      try {
        await o;
      } catch (r) {
        this.store.setFavorite(t, s), this.renderer.refresh(t), this.fail(r);
      }
  }
  /* --------------------------------------------------------- context menu */
  handleContextMenu(t, e) {
    const s = this.options.contextMenu;
    if (!s) return;
    const i = e?.raw ?? null, o = this.store.selectedPhotos().map((n) => n.raw), r = s({ item: i, selected: o, close: () => this.menu.close() });
    r === !1 || !r?.length || (t.preventDefault(), this.menu.open(r, t.clientX, t.clientY, { item: i, selected: o }));
  }
  /* ------------------------------------------------------------ public API */
  /** All photos currently in the timeline, in display order. */
  getPhotos() {
    return this.store.photos.map((t) => t.raw);
  }
  getSelection() {
    return this.store.selectedPhotos().map((t) => t.raw);
  }
  getSelectedIds() {
    return this.store.selectedPhotos().map((t) => t.id);
  }
  setSelection(t) {
    this.store.clearSelection();
    for (const e of t) {
      const s = String(e);
      this.store.byKey.has(s) && this.store.selection.add(s);
    }
    this.renderer.refresh(), this.syncToolbar(), this.emitSelection();
  }
  selectAll() {
    this.setSelection(this.store.photos.map((t) => t.id));
  }
  clearSelection() {
    this.store.selection.size !== 0 && (this.store.clearSelection(), this.renderer.refresh(), this.syncToolbar(), this.emitSelection());
  }
  getFavorites() {
    return this.store.photos.filter((t) => this.store.isFavorite(t.key)).map((t) => t.id);
  }
  /** Set favorite state without firing `onFavoriteToggle`. */
  setFavorite(t, e) {
    const s = String(t);
    this.store.byKey.has(s) && (this.store.setFavorite(s, e), this.renderer.refresh(s));
  }
  /** Open the viewer at a photo id or timeline index. */
  open(t) {
    if (this.options.viewer === !1) return;
    const s = this.store.byKey.get(String(t)) ?? (typeof t == "number" ? t : -1);
    this.viewer.open(s);
  }
  closeViewer() {
    this.viewer.close();
  }
  scrollToIndex(t, e = "auto") {
    const s = this.store.photos[t], i = s ? this.tileFor(s) : null;
    i && this.scroller.scrollTo({ top: Math.max(0, i.y - 8), behavior: e });
  }
  /**
   * Jump to the day group nearest the given date.
   *
   * "Nearest" rather than "on or after" so a date outside the timeline still
   * lands somewhere useful — asking for the year before your oldest photo
   * scrolls to the bottom instead of doing nothing.
   */
  scrollToDate(t, e = 1, s = 1, i = "auto") {
    const o = this.layout.headers;
    if (o.length === 0) return;
    const r = new Date(t, e - 1, s).getTime(), n = (u) => {
      const x = o[u].group;
      return new Date(x.year, x.month - 1, x.day).getTime();
    }, a = n(o.length - 1) >= n(0);
    let c = 0, d = o.length - 1;
    for (; c < d; ) {
      const u = c + d >> 1;
      (a ? n(u) < r : n(u) > r) ? c = u + 1 : d = u;
    }
    let y = c;
    c > 0 && Math.abs(n(c - 1) - r) < Math.abs(n(c) - r) && (y = c - 1), this.scroller.scrollTo({ top: o[y].y, behavior: i });
  }
  seek(t) {
    const e = Math.max(0, this.scroller.scrollHeight - this.scroller.clientHeight);
    this.scroller.scrollTop = b(t * this.layout.totalHeight, 0, e);
  }
  setLocale(t) {
    this.options.locale = t, this.messages = A(t, this.options.messages), this.empty.textContent = this.messages.empty, this.renderer.refresh(), this.timeline?.setLayout(this.layout), this.syncToolbar();
  }
  setTheme(t) {
    this.options.theme = t, this.root.dataset.theme = t;
  }
  /**
   * Merge in new options. Geometry-affecting keys trigger a relayout, so this
   * is what the framework wrappers call on every prop change.
   */
  setOptions(t) {
    const s = [
      "gap",
      "targetRowHeight",
      "headerHeight",
      "groupSpacing",
      "order"
    ].some(
      (o) => o in t && t[o] !== this.options[o]
    ), i = "order" in t && t.order !== this.options.order;
    if (this.options = { ...this.options, ...t }, (t.locale || t.messages) && (this.messages = A(this.opt("locale"), this.options.messages), this.empty.textContent = this.messages.empty, this.timeline?.setLayout(this.layout)), t.theme && (this.root.dataset.theme = t.theme), t.viewer !== void 0 && this.viewer.setOptions(this.viewerOptions()), i) {
      this.applyData(this.store.photos.map((o) => o.raw));
      return;
    }
    s && this.relayout(!0), this.renderer.refresh(), this.syncToolbar(), this.timeline?.setVisible(
      this.opt("timeline") && this.layout.totalHeight > this.scroller.clientHeight
    );
  }
  /** Force a geometry recalculation, e.g. after the container was un-hidden. */
  refresh() {
    this.relayout(!0);
  }
  on(t, e) {
    return this.emitter.on(t, e);
  }
  off(t, e) {
    this.emitter.off(t, e);
  }
  destroy() {
    if (!this.destroyed) {
      this.destroyed = !0, this.onScroll.cancel(), this.onResize.cancel(), this.resizeObserver?.disconnect();
      for (const t of this.disposers) t();
      this.disposers = [], this.viewer.close(), this.menu.close(), this.timeline?.destroy(), this.toolbar?.destroy(), this.renderer.destroy(), this.emitter.clear(), this.root.remove();
    }
  }
  /* -------------------------------------------------------------- helpers */
  opt(t) {
    const e = this.options[t];
    return e === void 0 ? U[t] : e;
  }
  viewerOptions() {
    return this.options.viewer === !1 ? {} : this.options.viewer ?? {};
  }
  emit(t, ...e) {
    this.emitter.emit(t, ...e), {
      itemClick: this.options.onItemClick,
      selectionChange: this.options.onSelectionChange,
      viewerOpen: this.options.onViewerOpen,
      viewerClose: this.options.onViewerClose,
      error: this.options.onError
    }[t]?.(...e);
  }
  fail(t) {
    const e = t instanceof Error ? t : new Error(String(t));
    this.emit("error", e), this.options.onError || console.warn(e.message);
  }
}
export {
  Ht as SweetAlbum,
  Ht as default,
  Y as en,
  rt as groupByDay,
  m as icons,
  $ as layoutJustified,
  J as locales,
  ht as normalize,
  nt as parseTime,
  A as resolveMessages,
  j as zhCN
};
//# sourceMappingURL=index.js.map
