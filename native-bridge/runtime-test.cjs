'use strict';
// Read-only by default. --exercise explicitly permits a one-watt test and
// restores the verified 15W baseline. Root activation runs this only at desktop.
const net = require('net');
const { PIPE_NAME } = require('./oxp3-power-bridge.cjs');
const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

function argumentsFrom(argv) {
  const value = { exercise: false, waitReadySeconds: 0 };
  for (let index = 0; index < argv.length; index++) {
    if (argv[index] === '--exercise') value.exercise = true;
    else if (argv[index] === '--wait-ready') {
      const seconds = Number(argv[++index]);
      if (!Number.isInteger(seconds) || seconds < 0 || seconds > 45) throw new Error('--wait-ready must be 0–45 seconds.');
      value.waitReadySeconds = seconds;
    } else throw new Error('Supported options: --wait-ready 0–45, --exercise.');
  }
  return value;
}

function request(command, watts) {
  return new Promise((resolve, reject) => {
    let buffer = '';
    let settled = false;
    const socket = net.createConnection(PIPE_NAME);
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(deadline);
      socket.destroy();
      if (error) reject(error); else resolve(value);
    };
    const deadline = setTimeout(() => finish(new Error('Native bridge connection/request timed out.')), 5000);
    socket.setEncoding('utf8');
    socket.on('connect', () => socket.write(JSON.stringify({ command,
      ...(watts === undefined ? {} : { watts }) }) + '\n'));
    socket.on('data', (chunk) => {
      buffer += chunk;
      if (Buffer.byteLength(buffer, 'utf8') > 4096) return finish(new Error('Native bridge response exceeded its limit.'));
      const end = buffer.indexOf('\n');
      if (end < 0) return;
      try {
        const response = JSON.parse(buffer.slice(0, end));
        if (response.ok !== true) return finish(new Error(response.error || 'Native bridge rejected the request.'));
        finish(null, response.result);
      } catch (error) { finish(error); }
    });
    socket.on('error', (error) => finish(new Error(`Native bridge unavailable: ${error.code || error.message}`)));
    socket.on('end', () => { if (!settled) finish(new Error('Native bridge closed before replying.')); });
  });
}

async function main() {
  const options = argumentsFrom(process.argv.slice(2));
  const deadline = Date.now() + options.waitReadySeconds * 1000;
  let state;
  for (;;) {
    try {
      state = await request('getState');
      if (state && state.nativeConnected === true) break;
    } catch (error) {
      if (Date.now() >= deadline) throw error;
    }
    if (Date.now() >= deadline) throw new Error('Native bridge is present but ONEXConsole is not ready.');
    await wait(500);
  }
  console.log(JSON.stringify({ phase: 'connected', state }));
  if (!options.exercise) return;
  if (state.tdp !== 15) throw new Error(`The bounded test requires the 15W baseline; current target is ${state.tdp}W. No change was made.`);
  const target = state.maxTdp >= 16 ? 16 : state.minTdp <= 14 ? 14 : null;
  if (target === null) throw new Error('There is no one-watt test target within current native limits.');
  let restoreRequired = false;
  try {
    // Mark restoration necessary BEFORE requesting, since a request that times
    // out may still have been accepted by the native handler.
    restoreRequired = true;
    const applied = await request('setWatts', target);
    if (applied.tdp !== target) throw new Error('Native test target was not confirmed.');
    console.log(JSON.stringify({ phase: 'test-target-configured', state: applied }));
    // Native first-loop and Intel PL4/PL2 work is asynchronous. Give it a
    // bounded settling interval without interpreting the target as a measurement.
    await wait(6500);
    const observed = await request('getState');
    if (observed.tdp !== target) throw new Error('ONEXConsole target changed during the test.');
  } finally {
    if (restoreRequired) {
      const restored = await request('setWatts', 15);
      if (restored.tdp !== 15) throw new Error('15W restoration was not confirmed.');
      await wait(6500);
      const final = await request('getState');
      if (final.tdp !== 15) throw new Error('Final ONEXConsole target is not 15W.');
      console.log(JSON.stringify({ phase: 'restored', state: final }));
    }
  }
  console.log('PASS: native configured target changed by one watt and returned to 15W. Actual CPU power was not measured.');
}

if (require.main === module) {
  main().then(() => process.exit(0), (error) => { console.error(error.message); process.exit(1); });
}
module.exports = { argumentsFrom, request };
