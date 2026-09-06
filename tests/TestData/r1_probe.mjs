// R1 验证探针：经 MCP stdio 真实复现 agent 的 debug_launch 场景。
// 用法：node r1_probe.mjs <serverExe> <targetExe>
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const serverExe = process.argv[2];
const targetExe = process.argv[3];
if (!serverExe || !targetExe) { console.error('usage: node r1_probe.mjs <serverExe> <targetExe>'); process.exit(2); }

// 与 agent 相同的 MCP 调用方式：JSON-RPC over stdio
const child = spawn(serverExe, [], { cwd: process.cwd(), stdio: ['pipe', 'pipe', 'pipe'] });
const rl = createInterface({ input: child.stdout });
let nextId = 0;
const pending = new Map();
const events = [];

function call(method, params) {
  const id = String(++nextId);
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
}

rl.on('line', (line) => {
  const msg = JSON.parse(line);
  if (msg.id !== undefined && pending.has(msg.id)) {
    const p = pending.get(msg.id); pending.delete(msg.id);
    if (msg.error) p.reject(new Error(JSON.stringify(msg.error)));
    else p.resolve(msg.result);
  } else if (msg.method) {
    events.push(msg);
  }
});
child.stderr.on('data', (d) => { /* 日志走 stderr，与协议分离，忽略 */ });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function withTimeout(promise, ms, label) {
  return Promise.race([promise, sleep(ms).then(() => { throw new Error(label + ' 超时 ' + ms + 'ms'); })]);
}

try {
  // MCP 握手
  await withTimeout(call('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'r1-probe', version: '1' } }), 15000, 'initialize');
  notify('notifications/initialized', {});

  // 读工具清单确认工具存在
  const tools = await withTimeout(call('tools/list', {}), 15000, 'tools/list');
  const toolNames = tools.tools.map(t => t.name);
  console.log('工具数:', toolNames.length, '含 debug_launch:', toolNames.includes('debug_launch'));

  // debug_launch：不带 workingDirectory（复现 agent 场景——server CWD 即仓库根）
  const launch = await withTimeout(call('tools/call', { name: 'debug_launch', arguments: { commandLine: targetExe, timeoutSeconds: 30 } }), 40000, 'debug_launch');
  const launchText = launch.content?.map(c => c.text).join('\n') ?? JSON.stringify(launch);
  console.log('\n=== debug_launch 返回 ===\n' + launchText.slice(0, 800));

  // 轮询 debug_state 若干次观察 Attaching 状态的文本形态
  for (let i = 0; i < 3; i++) {
    await sleep(1500);
    const st = await withTimeout(call('tools/call', { name: 'debug_state', arguments: {} }), 15000, 'debug_state');
    const stText = st.content?.map(c => c.text).join('\n') ?? '';
    console.log(`\n=== debug_state #${i + 1} ===\n` + stText.slice(0, 500));
  }

  // 等目标真正进入运行（首次 continue 后），让 Web 启动日志流出
  await withTimeout(call('tools/call', { name: 'debug_continue', arguments: { timeoutSeconds: 10 } }), 20000, 'debug_continue');
  // 多等几秒让 Kestrel 起完
  await sleep(8000);

  // 抓目标输出：重点看 ContentRoot / wwwroot / Now listening
  const out = await withTimeout(call('tools/call', { name: 'debug_output', arguments: { lines: 100 } }), 15000, 'debug_output');
  const outText = out.content?.map(c => c.text).join('\n') ?? '';
  console.log('\n=== debug_output（目标进程输出）===\n' + outText.slice(0, 4000));

  // 抓关键证据行
  const evidence = outText.split('\n').filter(l => /ContentRoot|WebRoot|Now listening|Application started|ASPNETCORE_ENVIRONMENT|warn|err/i.test(l));
  console.log('\n=== 关键证据行 ===');
  for (const l of evidence.slice(0, 40)) console.log(l);

  // 尝试命中：若能解析到本机进程/url 则可进一步验证 404，此处只取日志证据
  const result = { launchOk: !launch.isError, launchText: launchText.slice(0, 200) };
  console.log('\n=== 探针结束 ===');
  console.log(JSON.stringify(result, null, 2));
} catch (e) {
  console.error('探针失败:', e.message);
  process.exitCode = 1;
} finally {
  try { await call('tools/call', { name: 'debug_disconnect', arguments: {} }); } catch { }
  child.kill();
}
