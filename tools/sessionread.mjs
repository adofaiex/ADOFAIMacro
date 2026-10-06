// 多帧 zstd 解码器：文件由多个独立 zstd 帧拼接（每帧 = 一行 JSONL）。
// zstdDecompressSync 只解第一帧，必须逐帧切分。
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';

const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

export function decodeFrames(buf) {
  const offs = [];
  let idx = buf.indexOf(MAGIC);
  while (idx !== -1) { offs.push(idx); idx = buf.indexOf(MAGIC, idx + 4); }
  // 追加一个哨兵
  const bounds = [...offs, buf.length];
  const parts = [];
  for (let i = 0; i < offs.length; i++) {
    const slice = buf.subarray(bounds[i], bounds[i + 1]);
    try { parts.push(zlib.zstdDecompressSync(slice).toString('utf8')); }
    catch { /* 帧边界误判，跳过 */ }
  }
  return parts.join('');
}

export function* walkSessions(root) {
  let ents = [];
  try { ents = fs.readdirSync(root, { withFileTypes: true }); } catch { return; }
  for (const e of ents) {
    const p = path.join(root, e.name);
    if (e.isDirectory()) yield* walkSessions(p);
    else if (e.name === 'session.jsonl.zstd') yield p;
  }
}

if (import.meta.url === `file:///${process.argv[1].replace(/\\/g, '/')}`) {
  const ROOT = 'C:/Users/22282/.dsh/sessions';
  const all = [...walkSessions(ROOT)];
  console.log(`共 ${all.length} 个 session.jsonl.zstd`);

  // 按项目分组
  const byProj = {};
  for (const f of all) {
    const proj = f.replace(/\\/g, '/').split('/sessions/')[1].split('/')[0];
    (byProj[proj] ??= []).push(f);
  }
  console.log('\n=== 按项目 ===');
  for (const [k, v] of Object.entries(byProj).sort((a, b) => b[1].length - a[1].length))
    console.log(`  ${String(v.length).padStart(3)}  ${k}`);

  const ado = byProj['--D-Projects-ADOFAIMacro--'] ?? [];
  console.log(`\n=== ADOFAIMacro 会话 (${ado.length}) ===`);
  for (const f of ado) {
    const id = path.basename(path.dirname(f));
    const st = fs.statSync(f);
    let txt = '';
    try { txt = decodeFrames(fs.readFileSync(f)); } catch { }
    const nLines = txt.split('\n').filter(l => l.trim()).length;
    console.log(`  ${id}  ${String(st.size).padStart(10)}B  ${st.mtime.toISOString().slice(0, 16)}  行=${nLines}`);
  }
}