// VolumeScope Web — 断面（MPR）の表示。断面の画像は C# が作り、ここでは描くことと操作を伝えることだけをする。
//   1 本の指・左ボタン: 十字を動かす / 距離・円を置く（道具による）   ホイール: 断面を送る   Ctrl+ホイール・2 本の指: 拡大・移動
//   右ボタンでドラッグ: 濃淡（左右 = 幅、上下 = レベル）   ダブルクリック・ダブルタップ: 大きく表示

const views = new Map();

class MprCanvas {
  constructor(host, id, dotnet) {
    this.host = host; this.id = id; this.dotnet = dotnet;
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'mpr-canvas';
    this.canvas.tabIndex = 0;
    host.appendChild(this.canvas);
    this.ctx = this.canvas.getContext('2d');
    this.img = null; this.iw = 0; this.ih = 0; this.px = 1;
    this.scale = 1; this.ox = 0; this.oy = 0; this.userMoved = false;
    this.overlay = { color: '#fff', cross: null, hColor: '#fff', vColor: '#fff', labels: {}, measures: [] };
    this.tool = 'crosshair';
    this.pointers = new Map(); this.drag = null; this.lastTap = 0;
    new ResizeObserver(() => this.resize()).observe(host);
    this.bind(); this.resize();
  }

  // gray: 濃淡を 1 画素 1 バイトで（送る量を 4 分の 1 にする）
  async setSlice(w, h, gray) {
    const n = w * h, px = new Uint8ClampedArray(n * 4);
    for (let i = 0, j = 0; i < n; i++, j += 4) { const v = gray[i]; px[j] = v; px[j + 1] = v; px[j + 2] = v; px[j + 3] = 255; }
    const data = new ImageData(px, w, h);
    const seq = this.seq = (this.seq || 0) + 1;
    const bmp = await createImageBitmap(data);
    if (seq !== this.seq) { bmp.close(); return; } // 後から頼んだ断面のほうが先に描けていたら捨てる
    const changed = w !== this.iw || h !== this.ih;
    if (this.img && this.img.close) this.img.close();
    this.img = bmp; this.iw = w; this.ih = h;
    if (changed && !this.userMoved) this.fit(); else this.draw();
  }
  clear() { this.img = null; this.iw = this.ih = 0; this.draw(); }
  setOverlay(o) { this.overlay = o; this.draw(); }
  setTool(t) { this.tool = t; this.canvas.style.cursor = t === 'crosshair' ? 'crosshair' : 'cell'; }

  resize() {
    const r = this.host.getBoundingClientRect();
    this.dpr = window.devicePixelRatio || 1;
    this.cw = Math.max(1, r.width); this.ch = Math.max(1, r.height);
    this.canvas.width = Math.round(this.cw * this.dpr); this.canvas.height = Math.round(this.ch * this.dpr);
    this.canvas.style.width = this.cw + 'px'; this.canvas.style.height = this.ch + 'px';
    if (!this.userMoved) this.fit(); else this.draw();
  }
  fit() {
    if (!this.iw) { this.draw(); return; }
    const pad = 6;
    this.scale = Math.max(0.01, Math.min((this.cw - 2 * pad) / this.iw, (this.ch - 2 * pad) / this.ih));
    this.ox = (this.cw - this.iw * this.scale) / 2; this.oy = (this.ch - this.ih * this.scale) / 2;
    this.userMoved = false; this.draw();
  }
  toImg(x, y) { return { x: (x - this.ox) / this.scale, y: (y - this.oy) / this.scale }; }
  toScr(x, y) { return { x: x * this.scale + this.ox, y: y * this.scale + this.oy }; }
  draw() { if (!this.raf) this.raf = requestAnimationFrame(() => { this.raf = 0; this.render(); }); }

  render() {
    const c = this.ctx, d = this.dpr;
    c.setTransform(d, 0, 0, d, 0, 0);
    c.fillStyle = '#000'; c.fillRect(0, 0, this.cw, this.ch);
    if (!this.img) return;
    c.imageSmoothingEnabled = this.scale < 3;
    c.drawImage(this.img, this.ox, this.oy, this.iw * this.scale, this.ih * this.scale);
    const o = this.overlay;
    // 十字（ほかの断面の位置。線の色はその断面の色）
    if (o.cross) {
      const s = this.toScr(o.cross[0], o.cross[1]);
      const gap = 10;
      c.lineWidth = 1;
      c.strokeStyle = o.vColor; c.globalAlpha = 0.9;
      c.beginPath(); c.moveTo(s.x, 0); c.lineTo(s.x, s.y - gap); c.moveTo(s.x, s.y + gap); c.lineTo(s.x, this.ch); c.stroke();
      c.strokeStyle = o.hColor;
      c.beginPath(); c.moveTo(0, s.y); c.lineTo(s.x - gap, s.y); c.moveTo(s.x + gap, s.y); c.lineTo(this.cw, s.y); c.stroke();
      c.globalAlpha = 1;
    }
    // 計測
    for (const m of o.measures || []) this.drawMeasure(m, m.active ? '#ffe08a' : '#ffd25a');
    if (this.drag && this.drag.kind === 'distance') this.drawMeasure({ type: 'd', a: [this.drag.a.x, this.drag.a.y], b: [this.drag.b.x, this.drag.b.y], text: this.drag.text || '' }, '#fff');
    if (this.drag && this.drag.kind === 'circle') this.drawMeasure({ type: 'c', c: [this.drag.a.x, this.drag.a.y], r: Math.hypot(this.drag.b.x - this.drag.a.x, this.drag.b.y - this.drag.a.y), text: '' }, '#fff');
    // 向きの文字
    c.font = '600 13px system-ui, sans-serif'; c.fillStyle = 'rgba(255,255,255,.75)';
    const L = o.labels || {};
    c.textAlign = 'center'; if (L.top) c.fillText(L.top, this.cw / 2, 18); if (L.bottom) c.fillText(L.bottom, this.cw / 2, this.ch - 8);
    c.textAlign = 'left'; if (L.left) c.fillText(L.left, 8, this.ch / 2 + 4);
    c.textAlign = 'right'; if (L.right) c.fillText(L.right, this.cw - 8, this.ch / 2 + 4);
    c.textAlign = 'left';
  }

  drawMeasure(m, color) {
    const c = this.ctx;
    c.strokeStyle = color; c.fillStyle = color; c.lineWidth = 1.6;
    let tx, ty;
    if (m.type === 'd') {
      const a = this.toScr(m.a[0], m.a[1]), b = this.toScr(m.b[0], m.b[1]);
      c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke();
      for (const p of [a, b]) { c.beginPath(); c.arc(p.x, p.y, 3, 0, 7); c.fill(); }
      tx = (a.x + b.x) / 2 + 8; ty = (a.y + b.y) / 2 - 8;
    } else {
      const s = this.toScr(m.c[0], m.c[1]);
      c.beginPath(); c.arc(s.x, s.y, m.r * this.scale, 0, 7); c.stroke();
      tx = s.x + m.r * this.scale + 6; ty = s.y;
    }
    if (m.text) {
      c.font = '12px "Bahnschrift","Segoe UI",system-ui,sans-serif';
      const lines = m.text.split('\n'); let w = 0; for (const l of lines) w = Math.max(w, c.measureText(l).width);
      c.fillStyle = 'rgba(10,12,16,.75)'; c.fillRect(tx - 4, ty - 13, w + 8, lines.length * 15 + 5);
      c.fillStyle = color; lines.forEach((l, i) => c.fillText(l, tx, ty + i * 15));
    }
  }

  bind() {
    const cv = this.canvas;
    cv.addEventListener('wheel', e => {
      e.preventDefault();
      const r = cv.getBoundingClientRect();
      if (e.ctrlKey || e.metaKey) { this.zoomAt(e.clientX - r.left, e.clientY - r.top, Math.pow(1.0018, -e.deltaY)); return; }
      this.wheelAcc = (this.wheelAcc || 0) + e.deltaY;
      const steps = Math.trunc(this.wheelAcc / 60);
      if (steps !== 0) { this.wheelAcc -= steps * 60; this.dotnet.invokeMethodAsync('OnScroll', this.id, steps); }
    }, { passive: false });
    cv.addEventListener('pointerdown', e => this.down(e));
    cv.addEventListener('pointermove', e => this.move(e));
    cv.addEventListener('pointerup', e => this.up(e));
    cv.addEventListener('pointercancel', e => this.up(e, true));
    cv.addEventListener('dblclick', () => this.dotnet.invokeMethodAsync('OnDouble', this.id));
    cv.addEventListener('contextmenu', e => e.preventDefault());
  }
  pos(e) { const r = this.canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
  zoomAt(x, y, f) {
    const next = Math.min(40, Math.max(0.05, this.scale * f)); f = next / this.scale;
    this.ox = x - (x - this.ox) * f; this.oy = y - (y - this.oy) * f; this.scale = next; this.userMoved = true; this.draw();
  }

  down(e) {
    if (!this.img) return;
    this.canvas.focus(); this.canvas.setPointerCapture(e.pointerId);
    const p = this.pos(e); this.pointers.set(e.pointerId, p);
    if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      this.drag = { kind: 'pinch', dist: Math.hypot(a.x - b.x, a.y - b.y), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
      return;
    }
    if (this.pointers.size > 2) return;
    if (e.pointerType === 'touch') {
      const now = performance.now();
      if (now - this.lastTap < 300) { this.dotnet.invokeMethodAsync('OnDouble', this.id); this.lastTap = 0; return; }
      this.lastTap = now;
    }
    if (e.button === 2) { this.drag = { kind: 'window', start: p }; return; }
    if (e.button === 1 || (e.button === 0 && e.shiftKey)) { this.drag = { kind: 'pan', start: p, ox: this.ox, oy: this.oy }; return; }
    const ip = this.toImg(p.x, p.y);
    if (this.tool === 'distance' || this.tool === 'circle') { this.drag = { kind: this.tool, a: ip, b: ip }; this.draw(); return; }
    this.drag = { kind: 'cross' };
    this.sendPoint(ip, false);
  }
  move(e) {
    const p = this.pos(e);
    if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, p);
    const d = this.drag;
    if (d && d.kind === 'pinch' && this.pointers.size >= 2) {
      const [a, b] = [...this.pointers.values()];
      const dist = Math.hypot(a.x - b.x, a.y - b.y), mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      this.ox += mid.x - d.mid.x; this.oy += mid.y - d.mid.y; this.userMoved = true;
      this.zoomAt(mid.x, mid.y, dist / Math.max(d.dist, 1)); d.dist = dist; d.mid = mid; return;
    }
    if (!d) return;
    if (d.kind === 'pan') { this.ox = d.ox + p.x - d.start.x; this.oy = d.oy + p.y - d.start.y; this.userMoved = true; this.draw(); return; }
    if (d.kind === 'window') {
      const dx = p.x - d.start.x, dy = p.y - d.start.y; d.start = p;
      this.dotnet.invokeMethodAsync('OnWindow', dx, dy); return;
    }
    const ip = this.toImg(p.x, p.y);
    if (d.kind === 'cross') { this.sendPoint(ip, false); return; }
    if (d.kind === 'distance' || d.kind === 'circle') { d.b = ip; this.draw(); }
  }
  up(e, cancel) {
    this.pointers.delete(e.pointerId);
    const d = this.drag;
    if (!d) return;
    if (d.kind === 'pinch') { if (this.pointers.size === 0) this.drag = null; return; }
    this.drag = null;
    if (cancel) { this.draw(); return; }
    if (d.kind === 'distance' && Math.hypot(d.b.x - d.a.x, d.b.y - d.a.y) * this.scale > 4)
      this.dotnet.invokeMethodAsync('OnDistance', this.id, d.a.x, d.a.y, d.b.x, d.b.y);
    else if (d.kind === 'circle' && Math.hypot(d.b.x - d.a.x, d.b.y - d.a.y) * this.scale > 4)
      this.dotnet.invokeMethodAsync('OnCircle', this.id, d.a.x, d.a.y, Math.hypot(d.b.x - d.a.x, d.b.y - d.a.y));
    else if (d.kind === 'cross') this.sendPoint(this.toImg(this.pos(e).x, this.pos(e).y), true);
    this.draw();
  }
  // 十字の移動は、描き終わるまで次を送らない（重い断面の作り直しが積み上がらないように）
  sendPoint(ip, final) {
    this.pendingPoint = { x: Math.min(this.iw, Math.max(0, ip.x)), y: Math.min(this.ih, Math.max(0, ip.y)), final };
    if (this.pointBusy) return;
    this.pointBusy = true;
    requestAnimationFrame(async () => {
      const q = this.pendingPoint; this.pendingPoint = null;
      try { await this.dotnet.invokeMethodAsync('OnPoint', this.id, q.x, q.y); } catch { }
      this.pointBusy = false;
      if (this.pendingPoint) this.sendPoint(this.pendingPoint, this.pendingPoint.final);
    });
  }
}

export function create(id, dotnet) { views.set(id, new MprCanvas(document.getElementById(id), id, dotnet)); }
export function setSlice(id, w, h, rgba) { return views.get(id)?.setSlice(w, h, rgba); }
export function setOverlay(id, o) { views.get(id)?.setOverlay(o); }
export function setTool(id, t) { views.get(id)?.setTool(t); }
export function clear(id) { views.get(id)?.clear(); }
export function fit(id) { views.get(id)?.fit(); }
export function snapshot(id) { return views.get(id)?.canvas.toDataURL('image/png'); }
export function dispose(id) { views.delete(id); }
