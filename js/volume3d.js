// VolumeScope Web — 3D の表示（WebGL2 の GPU で描く）
//   ボリュームレンダリング・MIP: 画素ごとに視線を伸ばし、CT 値を伝達関数（色と 1 mm あたりの不透明度）で重ねる。
//   Windows 版の CPU のレイキャスティング（VolumeScope.Core の VolumeRenderer）と同じカメラ・同じ伝達関数・同じ陰影の式。
//   面: C# のマーチングキューブス法で作った三角形を描く。
//   左ドラッグ・1 本の指: 回す   右ドラッグ・2 本の指: 移動   ホイール・ピンチ: 拡大

const views = new Map();

const VS_QUAD = `#version 300 es
in vec2 a_pos; void main() { gl_Position = vec4(a_pos, 0.0, 1.0); }`;

const FS_RAY = `#version 300 es
precision highp float; precision highp sampler3D;
uniform sampler3D u_tex; uniform sampler2D u_lut;
uniform vec3 u_dims; uniform vec2 u_res;
uniform vec3 u_center, u_fwd, u_right, u_up; uniform float u_pixel, u_radius, u_stepMm;
uniform mat3 u_p2i; uniform vec3 u_origin;
uniform vec3 u_bmin, u_bmax; uniform int u_mode; uniform vec2 u_mip; uniform bool u_shade; uniform bool u_hasVolume;
out vec4 outColor;
vec3 bg(float y) { float t = 1.0 - y / u_res.y; return vec3(24.0 - 12.0 * t, 32.0 - 16.0 * t, 46.0 - 22.0 * t) / 255.0; }
float hu(vec3 q) { return texture(u_tex, (q + 0.5) / u_dims).r; }
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
void main() {
  vec3 back = bg(gl_FragCoord.y);
  if (!u_hasVolume || u_mode == 2) { outColor = vec4(back, 1.0); return; }
  float u = gl_FragCoord.x - u_res.x * 0.5, v = gl_FragCoord.y - u_res.y * 0.5;
  vec3 p0 = u_center - u_fwd * (u_radius * 1.05) + u_right * (u * u_pixel) + u_up * (v * u_pixel);
  vec3 o = u_p2i * (p0 - u_origin);
  vec3 d = u_p2i * (u_fwd * u_stepMm);
  vec3 inv = 1.0 / d;
  vec3 t1 = (u_bmin - o) * inv, t2 = (u_bmax - o) * inv;
  vec3 tmin = min(t1, t2), tmax = max(t1, t2);
  float tEnter = max(max(tmin.x, tmin.y), max(tmin.z, 0.0));
  float tExit = min(min(tmax.x, tmax.y), tmax.z);
  if (tExit < tEnter) { outColor = vec4(back, 1.0); return; }
  float jitter = hash(gl_FragCoord.xy);
  if (u_mode == 1) {
    float m = -4096.0;
    for (int i = 0; i < 4096; i++) {
      float t = tEnter + float(i); if (t > tExit) break;
      m = max(m, hu(o + d * t));
    }
    float g = clamp((m - u_mip.x) / max(u_mip.y - u_mip.x, 1.0), 0.0, 1.0);
    outColor = vec4(vec3(g), 1.0); return;
  }
  vec3 acc = vec3(0.0); float a = 0.0;
  mat3 grad = transpose(u_p2i);
  for (int i = 0; i < 4096; i++) {
    float t = floor(tEnter) + jitter + float(i);
    if (t > tExit || a > 0.98) break;
    if (t < tEnter) continue;
    vec3 q = o + d * t;
    float h = hu(q);
    int idx = int(clamp(h + 1024.0, 0.0, 4095.0));
    vec4 e = texelFetch(u_lut, ivec2(idx % 1024, idx / 1024), 0);
    if (e.a <= 0.0) continue;
    float alpha = 1.0 - exp(-e.a * u_stepMm);
    vec3 col = e.rgb;
    if (u_shade && alpha > 0.01) {
      vec3 gi = vec3(hu(q + vec3(1, 0, 0)) - hu(q - vec3(1, 0, 0)), hu(q + vec3(0, 1, 0)) - hu(q - vec3(0, 1, 0)), hu(q + vec3(0, 0, 1)) - hu(q - vec3(0, 0, 1)));
      vec3 n = -(grad * gi);
      float len = length(n);
      if (len > 1e-3) {
        n /= len;
        float diffuse = abs(dot(n, -u_fwd));
        float spec = pow(diffuse, 24.0) * 0.25;
        col = min(vec3(1.0), col * (0.25 + 0.75 * diffuse) + spec);
      }
    }
    float w = (1.0 - a) * alpha;
    acc += col * w; a += w;
  }
  outColor = vec4(acc + back * (1.0 - a), 1.0);
}`;

const VS_MESH = `#version 300 es
in vec3 a_pos; in vec3 a_nrm;
uniform vec3 u_center, u_fwd, u_right, u_up; uniform float u_pixel, u_radius; uniform vec2 u_res;
out vec3 v_n;
void main() {
  vec3 r = a_pos - u_center;
  gl_Position = vec4(dot(r, u_right) / (u_pixel * u_res.x * 0.5), dot(r, u_up) / (u_pixel * u_res.y * 0.5), dot(r, u_fwd) / (u_radius * 2.2), 1.0);
  v_n = a_nrm;
}`;

const FS_MESH = `#version 300 es
precision highp float;
in vec3 v_n; uniform vec3 u_fwd; uniform vec3 u_color; out vec4 outColor;
void main() {
  vec3 n = normalize(v_n);
  float diffuse = abs(dot(n, -u_fwd));
  float spec = pow(diffuse, 32.0) * 0.3;
  outColor = vec4(min(vec3(1.0), u_color * (0.22 + 0.78 * diffuse) + spec), 1.0);
}`;

function compile(gl, vs, fs) {
  const p = gl.createProgram();
  for (const [type, src] of [[gl.VERTEX_SHADER, vs], [gl.FRAGMENT_SHADER, fs]]) {
    const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
    gl.attachShader(p, s);
  }
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
  return p;
}

function basis(az, el) {
  const a = az * Math.PI / 180, e = Math.max(-89.9, Math.min(89.9, el)) * Math.PI / 180;
  const f = norm([Math.sin(a) * Math.cos(e), Math.cos(a) * Math.cos(e), -Math.sin(e)]);
  const r = norm(cross(f, [0, 0, 1]));
  const u = norm(cross(r, f));
  return { f, r, u };
}
const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const norm = v => { const l = Math.hypot(v[0], v[1], v[2]) || 1; return [v[0] / l, v[1] / l, v[2] / l]; };

class Volume3D {
  constructor(host, id, dotnet) {
    this.host = host; this.id = id; this.dotnet = dotnet;
    this.canvas = document.createElement('canvas');
    this.canvas.className = 'vol-canvas';
    host.appendChild(this.canvas);
    this.gl = this.canvas.getContext('webgl2', { antialias: true, preserveDrawingBuffer: true });
    this.ok = !!this.gl;
    this.cam = { az: 25, el: 12, zoom: 1.25, panX: 0, panY: 0 };
    this.params = { mode: 0, shading: true, mip: [-200, 1000], color: [0.95, 0.92, 0.85], crop: [0, 1, 0, 1, 0, 1] };
    this.vol = null; this.mesh = null; this.pointers = new Map(); this.drag = null; this.quality = 1;
    if (this.ok) this.init();
    new ResizeObserver(() => this.resize()).observe(host);
    this.bind(); this.resize();
  }

  init() {
    const gl = this.gl;
    this.ray = compile(gl, VS_QUAD, FS_RAY);
    this.meshProg = compile(gl, VS_MESH, FS_MESH);
    this.quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    this.lut = gl.createTexture();
    gl.getExtension('EXT_color_buffer_float');
    // 3D と 2D のテクスチャを別の番号に（同じ番号だと描けない）
    gl.useProgram(this.ray);
    gl.uniform1i(gl.getUniformLocation(this.ray, 'u_tex'), 0);
    gl.uniform1i(gl.getUniformLocation(this.ray, 'u_lut'), 1);
  }

  // 画像（CT 値を半精度の小数で）と、番地 ⇔ 患者座標の変換
  setVolume(w, h, d, halfBytes, geo) {
    if (!this.ok) return;
    const gl = this.gl;
    if (this.tex) gl.deleteTexture(this.tex);
    this.tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_3D, this.tex);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    const data = new Uint16Array(halfBytes.buffer.slice(halfBytes.byteOffset, halfBytes.byteOffset + w * h * d * 2));
    gl.texImage3D(gl.TEXTURE_3D, 0, gl.R16F, w, h, d, 0, gl.RED, gl.HALF_FLOAT, data);
    for (const p of [gl.TEXTURE_MIN_FILTER, gl.TEXTURE_MAG_FILTER]) gl.texParameteri(gl.TEXTURE_3D, p, gl.LINEAR);
    for (const p of [gl.TEXTURE_WRAP_S, gl.TEXTURE_WRAP_T, gl.TEXTURE_WRAP_R]) gl.texParameteri(gl.TEXTURE_3D, p, gl.CLAMP_TO_EDGE);
    this.vol = { w, h, d, ...geo };
    this.draw();
  }
  clearVolume() { this.vol = null; this.mesh = null; this.draw(); }

  // 伝達関数の表（-1024〜3071 HU、1 HU ごとに R, G, B, 1 mm あたりの不透明度）
  setTransfer(bytes) {
    if (!this.ok) return;
    const gl = this.gl;
    const f = new Float32Array(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + 4096 * 16));
    gl.bindTexture(gl.TEXTURE_2D, this.lut);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, 1024, 4, 0, gl.RGBA, gl.FLOAT, f);
    for (const p of [gl.TEXTURE_MIN_FILTER, gl.TEXTURE_MAG_FILTER]) gl.texParameteri(gl.TEXTURE_2D, p, gl.NEAREST);
    this.draw();
  }

  setMesh(posBytes, nrmBytes, idxBytes) {
    if (!this.ok) return;
    const gl = this.gl;
    if (this.mesh) { gl.deleteBuffer(this.mesh.p); gl.deleteBuffer(this.mesh.n); gl.deleteBuffer(this.mesh.i); }
    if (!posBytes || posBytes.length === 0) { this.mesh = null; this.draw(); return; }
    const mk = (target, bytes) => { const b = gl.createBuffer(); gl.bindBuffer(target, b); gl.bufferData(target, bytes, gl.STATIC_DRAW); return b; };
    this.mesh = { p: mk(gl.ARRAY_BUFFER, posBytes), n: mk(gl.ARRAY_BUFFER, nrmBytes), i: mk(gl.ELEMENT_ARRAY_BUFFER, idxBytes), count: idxBytes.length / 4 };
    this.draw();
  }

  setParams(p) { Object.assign(this.params, p); this.draw(); }
  setCamera(c) { Object.assign(this.cam, c); this.draw(); }

  resize() {
    const r = this.host.getBoundingClientRect();
    this.dpr = Math.min(window.devicePixelRatio || 1, 1.5);
    this.cw = Math.max(1, r.width); this.ch = Math.max(1, r.height);
    this.canvas.width = Math.round(this.cw * this.dpr); this.canvas.height = Math.round(this.ch * this.dpr);
    this.canvas.style.width = this.cw + 'px'; this.canvas.style.height = this.ch + 'px';
    this.draw();
  }

  draw() { if (!this.raf) this.raf = requestAnimationFrame(() => { this.raf = 0; this.render(); }); }

  render() {
    if (!this.ok) return;
    const gl = this.gl, W = this.canvas.width, H = this.canvas.height;
    gl.viewport(0, 0, W, H);
    const v = this.vol;
    const { f, r, u } = basis(this.cam.az, this.cam.el);
    const radius = v ? v.radius : 100;
    const pixel = 2 * radius / (Math.min(W, H) * Math.max(this.cam.zoom, 0.05));
    const c0 = v ? v.center : [0, 0, 0];
    const center = [0, 1, 2].map(i => c0[i] + r[i] * this.cam.panX + u[i] * this.cam.panY);
    const mode = this.params.mode; // 0: ボリューム 1: MIP 2: 面

    gl.disable(gl.DEPTH_TEST);
    gl.useProgram(this.ray);
    const U = n => gl.getUniformLocation(this.ray, n);
    gl.uniform2f(U('u_res'), W, H);
    gl.uniform3fv(U('u_center'), center); gl.uniform3fv(U('u_fwd'), f); gl.uniform3fv(U('u_right'), r); gl.uniform3fv(U('u_up'), u);
    gl.uniform1f(U('u_pixel'), pixel); gl.uniform1f(U('u_radius'), radius);
    gl.uniform1i(U('u_mode'), mode); gl.uniform1i(U('u_hasVolume'), v ? 1 : 0);
    if (v) {
      const step = v.minSpacing * (this.interacting ? 1.0 : 0.5);
      gl.uniform1f(U('u_stepMm'), step);
      gl.uniform3f(U('u_dims'), v.w, v.h, v.d);
      gl.uniformMatrix3fv(U('u_p2i'), true, v.p2i); // 行ごとに並べた行列
      gl.uniform3fv(U('u_origin'), v.origin);
      const c = this.params.crop, e = 1e-3;
      gl.uniform3f(U('u_bmin'), Math.max(c[0] * v.w - 0.5, 0), Math.max(c[2] * v.h - 0.5, 0), Math.max(c[4] * v.d - 0.5, 0));
      gl.uniform3f(U('u_bmax'), Math.min(c[1] * v.w - 0.5, v.w - 1 - e), Math.min(c[3] * v.h - 0.5, v.h - 1 - e), Math.min(c[5] * v.d - 0.5, v.d - 1 - e));
      gl.uniform2f(U('u_mip'), this.params.mip[0], this.params.mip[1]);
      gl.uniform1i(U('u_shade'), this.params.shading ? 1 : 0);
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_3D, this.tex); gl.uniform1i(U('u_tex'), 0);
      gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, this.lut); gl.uniform1i(U('u_lut'), 1);
    }
    const loc = gl.getAttribLocation(this.ray, 'a_pos');
    gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.disableVertexAttribArray(loc);

    if (mode === 2 && this.mesh) {
      gl.enable(gl.DEPTH_TEST); gl.clear(gl.DEPTH_BUFFER_BIT);
      gl.useProgram(this.meshProg);
      const M = n => gl.getUniformLocation(this.meshProg, n);
      gl.uniform3fv(M('u_center'), center); gl.uniform3fv(M('u_fwd'), f); gl.uniform3fv(M('u_right'), r); gl.uniform3fv(M('u_up'), u);
      gl.uniform1f(M('u_pixel'), pixel); gl.uniform1f(M('u_radius'), radius); gl.uniform2f(M('u_res'), W, H);
      gl.uniform3fv(M('u_color'), this.params.color);
      const pl = gl.getAttribLocation(this.meshProg, 'a_pos'), nl = gl.getAttribLocation(this.meshProg, 'a_nrm');
      gl.bindBuffer(gl.ARRAY_BUFFER, this.mesh.p); gl.enableVertexAttribArray(pl); gl.vertexAttribPointer(pl, 3, gl.FLOAT, false, 0, 0);
      gl.bindBuffer(gl.ARRAY_BUFFER, this.mesh.n); gl.enableVertexAttribArray(nl); gl.vertexAttribPointer(nl, 3, gl.FLOAT, false, 0, 0);
      gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.mesh.i);
      gl.drawElements(gl.TRIANGLES, this.mesh.count, gl.UNSIGNED_INT, 0);
      gl.disableVertexAttribArray(pl); gl.disableVertexAttribArray(nl);
    }
  }

  bind() {
    const cv = this.canvas;
    cv.addEventListener('wheel', e => { e.preventDefault(); this.cam.zoom = Math.min(20, Math.max(0.2, this.cam.zoom * Math.pow(1.0018, -e.deltaY))); this.touch(); }, { passive: false });
    cv.addEventListener('pointerdown', e => {
      cv.setPointerCapture(e.pointerId);
      const p = this.pos(e); this.pointers.set(e.pointerId, p);
      if (this.pointers.size === 2) {
        const [a, b] = [...this.pointers.values()];
        this.drag = { kind: 'pinch', dist: Math.hypot(a.x - b.x, a.y - b.y), mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } };
      } else if (this.pointers.size === 1) {
        this.drag = { kind: e.button === 2 || e.button === 1 || e.shiftKey ? 'pan' : 'orbit', last: p };
      }
      this.interacting = true;
    });
    cv.addEventListener('pointermove', e => {
      const p = this.pos(e); if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, p);
      const d = this.drag; if (!d) return;
      const pxmm = 2 * (this.vol ? this.vol.radius : 100) / (Math.min(this.cw, this.ch) * this.cam.zoom);
      if (d.kind === 'pinch' && this.pointers.size >= 2) {
        const [a, b] = [...this.pointers.values()];
        const dist = Math.hypot(a.x - b.x, a.y - b.y), mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
        this.cam.zoom = Math.min(20, Math.max(0.2, this.cam.zoom * dist / Math.max(d.dist, 1)));
        this.cam.panX -= (mid.x - d.mid.x) * pxmm; this.cam.panY += (mid.y - d.mid.y) * pxmm;
        d.dist = dist; d.mid = mid; this.touch(); return;
      }
      const dx = p.x - d.last.x, dy = p.y - d.last.y; d.last = p;
      if (d.kind === 'orbit') { this.cam.az = (this.cam.az + dx * 0.45) % 360; this.cam.el = Math.max(-89, Math.min(89, this.cam.el + dy * 0.45)); }
      else if (d.kind === 'pan') { this.cam.panX -= dx * pxmm; this.cam.panY += dy * pxmm; }
      this.touch();
    });
    const end = e => {
      this.pointers.delete(e.pointerId);
      if (this.pointers.size === 0) { this.drag = null; this.interacting = false; this.draw(); this.report(); }
    };
    cv.addEventListener('pointerup', end); cv.addEventListener('pointercancel', end);
    cv.addEventListener('dblclick', () => this.dotnet.invokeMethodAsync('OnDouble', this.id));
    cv.addEventListener('contextmenu', e => e.preventDefault());
  }
  pos(e) { const r = this.canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
  touch() {
    this.draw();
    clearTimeout(this.idle);
    this.idle = setTimeout(() => { if (!this.drag) { this.interacting = false; this.draw(); this.report(); } }, 200);
  }
  report() { this.dotnet.invokeMethodAsync('OnCamera', this.cam.az, this.cam.el, this.cam.zoom, this.cam.panX, this.cam.panY); }
}

export function create(id, dotnet) { const v = new Volume3D(document.getElementById(id), id, dotnet); views.set(id, v); return v.ok; }
export function setVolume(id, w, h, d, bytes, geo) { views.get(id)?.setVolume(w, h, d, bytes, geo); }
export function clearVolume(id) { views.get(id)?.clearVolume(); }
export function setTransfer(id, bytes) { views.get(id)?.setTransfer(bytes); }
export function setMesh(id, p, n, i) { views.get(id)?.setMesh(p, n, i); }
export function setParams(id, p) { views.get(id)?.setParams(p); }
export function setCamera(id, c) { views.get(id)?.setCamera(c); }
export function snapshot(id) { return views.get(id)?.canvas.toDataURL('image/png'); }
export function dispose(id) { views.delete(id); }
// GPU の 3D テクスチャの一辺の上限と、スマホ・タブレットかどうか（送る画像の大きさを決める）
export function limits(id) {
  const v = views.get(id);
  const max3d = v && v.ok ? v.gl.getParameter(v.gl.MAX_3D_TEXTURE_SIZE) : 0;
  const mobile = window.matchMedia('(pointer: coarse)').matches || /Android|iPhone|iPad|Mobile/i.test(navigator.userAgent);
  return [max3d, mobile ? 1 : 0];
}
