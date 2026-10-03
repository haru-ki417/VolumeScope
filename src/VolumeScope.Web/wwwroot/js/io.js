// VolumeScope Web — 保存・キー操作など（ファイルはブラウザーの中だけで扱い、どこにも送らない）

export function download(name, mime, bytes) {
  const blob = new Blob([bytes], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = name; a.rel = 'noopener';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

// 表示中の画面（キャンバス）を、見えている並びのまま 1 枚の PNG にする
export async function saveScreens(selector, name) {
  const hosts = [...document.querySelectorAll(selector)].filter(h => h.offsetParent !== null);
  if (hosts.length === 0) return false;
  const rects = hosts.map(h => h.getBoundingClientRect());
  const x0 = Math.min(...rects.map(r => r.left)), y0 = Math.min(...rects.map(r => r.top));
  const x1 = Math.max(...rects.map(r => r.right)), y1 = Math.max(...rects.map(r => r.bottom));
  const s = Math.min(2, window.devicePixelRatio || 1);
  const out = document.createElement('canvas');
  out.width = Math.round((x1 - x0) * s); out.height = Math.round((y1 - y0) * s);
  const ctx = out.getContext('2d');
  ctx.fillStyle = '#2E3846'; ctx.fillRect(0, 0, out.width, out.height);
  hosts.forEach((h, i) => {
    const r = rects[i], cv = h.querySelector('canvas');
    if (cv) ctx.drawImage(cv, (r.left - x0) * s, (r.top - y0) * s, r.width * s, r.height * s);
    // 画面の名前（左上）
    const label = h.dataset.label;
    if (label) {
      ctx.font = `600 ${13 * s}px system-ui, sans-serif`; ctx.fillStyle = h.dataset.color || '#fff';
      ctx.fillText(label, (r.left - x0 + 10) * s, (r.top - y0 + 20) * s);
    }
  });
  const blob = await new Promise(r => out.toBlob(r, 'image/png'));
  download(name, 'image/png', new Uint8Array(await blob.arrayBuffer()));
  return true;
}

let keyHandler = null;
export function listenKeys(dotnet) {
  if (keyHandler) document.removeEventListener('keydown', keyHandler);
  keyHandler = e => {
    const t = e.target;
    const typing = t && (t.tagName === 'INPUT' && t.type !== 'range' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    if (typing || e.altKey) return;
    if (t && t.getAttribute && t.getAttribute('role') === 'slider' && e.key.startsWith('Arrow')) return;
    let key = null;
    if (e.ctrlKey || e.metaKey) {
      if (e.key.toLowerCase() === 'o') key = 'open';
    } else {
      key = { c: 'crosshair', d: 'distance', r: 'circle', '1': 'Axial', '2': 'Coronal', '3': 'Sagittal', '4': '3D', Escape: 'restore', PageUp: 'prev', PageDown: 'next', ArrowUp: 'prev', ArrowDown: 'next' }[e.key] || null;
      if ((key === 'prev' || key === 'next') && t && t.type === 'range') key = null;
    }
    if (key) { e.preventDefault(); dotnet.invokeMethodAsync('OnKey', key); }
  };
  document.addEventListener('keydown', keyHandler);
}

export function clickElement(id) { document.getElementById(id)?.click(); }
export function isNarrow() { return window.matchMedia('(max-width: 1099px)').matches; }
export function folderPickerSupported() { return 'webkitdirectory' in document.createElement('input') && !/Android|iPhone|iPad/i.test(navigator.userAgent); }
