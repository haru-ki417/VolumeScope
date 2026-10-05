// VolumeScope Web — 選んだ・落としたファイルを C# に 1 つずつ渡す。ファイルはブラウザーの中だけで扱い、どこにも送らない
let picked = [];
let dotnet = null;

// 画像でないことがはっきりしているファイルは読まない（DICOM は拡張子がないことも多いので、ないものは読む）
const skip = /\.(jpe?g|png|gif|bmp|webp|txt|xml|html?|pdf|exe|dll|ini|json|csv|log|md|inf|bat|cmd|js|css|ico|db)$/i;

function accept(list) {
  picked = [...list].filter(f => !skip.test(f.name) && f.size > 0);
  if (dotnet) dotnet.invokeMethodAsync('OnFilesPicked', picked.length, picked.reduce((s, f) => s + f.size, 0));
}

export function init(ref, dropId) {
  dotnet = ref;
  for (const id of ['pick-folder', 'pick-files']) {
    const input = document.getElementById(id);
    if (input && !input.dataset.bound) {
      input.dataset.bound = '1';
      input.addEventListener('change', () => { accept(input.files); input.value = ''; });
    }
  }
  const zone = document.getElementById(dropId);
  if (zone && !zone.dataset.bound) {
    zone.dataset.bound = '1';
    zone.addEventListener('dragover', e => { e.preventDefault(); zone.classList.add('dropping'); });
    zone.addEventListener('dragleave', e => { if (e.target === zone || !zone.contains(e.relatedTarget)) zone.classList.remove('dropping'); });
    zone.addEventListener('drop', async e => {
      e.preventDefault(); zone.classList.remove('dropping');
      const items = [...(e.dataTransfer.items || [])].map(i => i.webkitGetAsEntry ? i.webkitGetAsEntry() : null);
      if (items.some(x => x)) accept(await walk(items.filter(x => x)));
      else accept(e.dataTransfer.files);
    });
  }
}

// 落としたフォルダーの中を、下のフォルダーまでたどる
async function walk(entries) {
  const out = [];
  const visit = async entry => {
    if (entry.isFile) { out.push(await new Promise((res, rej) => entry.file(res, rej))); return; }
    if (!entry.isDirectory) return;
    const reader = entry.createReader();
    for (;;) {
      const batch = await new Promise((res, rej) => reader.readEntries(res, rej));
      if (batch.length === 0) break;
      for (const e of batch) await visit(e);
    }
  };
  for (const e of entries) await visit(e);
  return out;
}

export function name(i) { return picked[i]?.webkitRelativePath || picked[i]?.name || ''; }
export async function read(i) { const f = picked[i]; return f ? new Uint8Array(await f.arrayBuffer()) : null; }
export function release() { picked = []; }
