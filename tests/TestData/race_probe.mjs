// 竞态探针：debug_launch → continue → 立即 set token 断点（模拟 DebugMcpToolsTests 时序），
// 统计「已设(立即绑定) vs 已登记(pending)」频率，验证是否为模块加载竞态。
// 用法：node race_probe.mjs <serverDllOrExe> <targetExe> [rounds]
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const serverPath = process.argv[2];
const targetExe = process.argv[3];
const rounds = parseInt(process.argv[4] || '5', 10);
if (!serverPath || !targetExe) { console.error('usage: node race_probe.mjs <serverDllOrExe> <targetExe> [rounds]'); process.exit(2); }

const isDll = serverPath.endsWith('.dll');
const child = spawn(isDll ? 'dotnet' : serverPath, isDll ? [serverPath] : [], { stdio: ['pipe', 'pipe', 'pipe'] });
const rl = createInterface({ input: child.stdout });
let nextId = 0;
const pending = new Map();

function call(method, params) {
  const id = String(++nextId);
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
function notify(method, params) { child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n'); }
rl.on('line', (line) => {
  const msg = JSON.parse(line);
  if (msg.id !== undefined && pending.has(msg.id)) {
    const p = pending.get(msg.id); pending.delete(msg.id);
    if (msg.error) p.reject(new Error(JSON.stringify(msg.error)));
    else p.resolve(msg.result);
  }
});
child.stderr.on('data', () => {});
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function withTimeout(promise, ms, label) {
  return Promise.race([promise, sleep(ms).then(() => { throw new Error(label + ' 超时'); })]);
}
async function tool(name, args) {
  const r = await withTimeout(call('tools/call', { name, arguments: args }), 30000, name);
  return (r.content?.map(c => c.text).join('\n') ?? JSON.stringify(r)).trim();
}

let bound = 0, pendingCount = 0;
try {
  await withTimeout(call('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'race-probe', version: '1' } }), 15000, 'initialize');
  notify('notifications/initialized', {});
  const tools = await withTimeout(call('tools/list', {}), 15000, 'tools/list');
  console.log('工具数:', tools.tools.length);

  for (let r = 1; r <= rounds; r++) {
    console.log(`\n=== 第 ${r} 轮 ===`);
    // 目标 3 迭代 + 20s delay（给足窗口），timeout 放宽
    const launch = await tool('debug_launch', { commandLine: `${targetExe} 3 20`, timeoutSeconds: 25 });
    console.log('launch:', launch.slice(0, 120));

    // 读 Work token（经 search_string 不如直接算：0x06000003 稳定，但这里用固定 Work token）
    // continue 先行（与测试一致）
    const cont = await tool('debug_continue', { timeoutSeconds: 10 });
    console.log('continue:', cont.slice(0, 80));

    // 立即 set（不 sleep），与测试时序一致
    const set = await tool('debug_breakpoint_set', {
      moduleName: 'DebugTarget.dll', methodToken: '0x06000003', ilOffset: 0,
    });
    const isBound = set.includes('断点已设');
    console.log(`set 立即: ${isBound ? '已设(bound)' : '已登记(pending)'} :: ${set.slice(0, 90)}`);
    isBound ? bound++ : pendingCount++;

    // 清理：disconnect（目标自己会跑完 delay 退出，无需杀）
    await sleep(500);
    try { await tool('debug_disconnect', {}); } catch {}
    await sleep(500);
  }
  console.log(`\n=== 统计 ===  bound=${bound} pending=${pendingCount} (rounds=${rounds})`);
} catch (e) {
  console.error('探针失败:', e.message);
  process.exitCode = 1;
} finally {
  child.kill();
}
