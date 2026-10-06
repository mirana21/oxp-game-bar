'use strict';
const assert = require('assert/strict');
const { EventEmitter } = require('events');
const { PassThrough } = require('stream');
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const { CdpPipe } = require('./cdp-pipe.cjs');
const { createLiveController, createLimitTracker, observeNativeLimits, readQuickState } = require('./live-native-bridge.cjs');
const { bootstrapExpression, recoverBridge } = require('./launch-native.cjs');
async function main() {
  let clock = 0;
  let target = 15;
  const calls = [];
  let active = 0;
  const ipcMain = new EventEmitter();
  ipcMain.on('tdpChanged', async (event, watts) => {
    assert.deepEqual(event, {});
    assert.equal(active++, 0);
    calls.push(['native', watts]);
    await new Promise((resolve) => setTimeout(resolve, 2));
    target = watts;
    active--;
  });
  let ui = { tdp: 15, minTdp: 4, maxTdp: 35 };
  const controller = createLiveController({ ipcMain, now: () => clock,
    readTarget: async () => target, readLimits: async () => ui,
    refreshUi: async (watts) => { calls.push(['ui', watts]); ui.tdp = watts; },
    onBatteryPower: () => true });
  assert.equal((await controller.getState()).nativeConnected, false);
  await assert.rejects(controller.setWatts(16), /unavailable/);
  assert.deepEqual(calls, []);
  clock = 8000;
  assert.equal((await controller.getState()).tdp, 15);
  assert.equal((await controller.getState()).nativeConnected, true);
  for (const watts of [3, 36, 15.1, '15', NaN]) await assert.rejects(controller.setWatts(watts));
  const changed = await Promise.all([controller.setWatts(16), controller.setWatts(15)]);
  assert.deepEqual(changed.map((value) => value.tdp), [16, 15]);
  assert.deepEqual(calls, [['native', 16], ['ui', 16], ['native', 15], ['ui', 15]]);
  controller.suspend();
  await assert.rejects(controller.setWatts(16), /unavailable/);
  controller.resume();
  clock = 19999;
  assert.equal((await controller.getState()).nativeConnected, false);
  clock = 20000;
  assert.equal((await controller.getState()).nativeConnected, true);
  const writesBeforeLimitChange = calls.length;
  ui.maxTdp = 40;
  assert.equal((await controller.getState()).maxTdp, 40);
  assert.equal(target, 15);
  assert.equal(calls.length, writesBeforeLimitChange);
  assert.equal((await controller.setWatts(40)).tdp, 40);
  await assert.rejects(controller.setWatts(41), /between 4 and 40/);
  ui.maxTdp = 35;
  await assert.rejects(controller.setWatts(40), /between 4 and 35/);
  ui.maxTdp = 12;
  await assert.rejects(controller.setWatts(13), /between 4 and 12/);
  ui = null;
  await assert.rejects(controller.setWatts(10), /unavailable/);
  controller.stop();
  assert.equal((await controller.getState()).nativeConnected, false);

  const home = { component: { data: { tdp: 15, minTdp: 4, maxTdp: 35, supportFunc: { cpuPower: true } } } };
  const root = { _vnode: { component: { subTree: { suspense: { activeBranch: { children: [home] } } } } } };
  global.document = { getElementById: () => root };
  assert.deepEqual(readQuickState(), { tdp: 15, minTdp: 4, maxTdp: 35 });
  root._vnode.component.subTree.children = [root._vnode];
  root._vnode.component.subTree.suspense.activeBranch.children = [];
  assert.equal(readQuickState(), null);
  delete global.document;

  const tracked = createLimitTracker();
  let sends = 0;
  const contents = { send(channel, data) { assert.equal(this, contents); sends++; return 'original-result'; } };
  const originalSend = contents.send;
  const detach = observeNativeLimits(contents, tracked);
  assert.equal(tracked.snapshot(), null);
  assert.equal(contents.send('initSettingEvent', {minTdp: 4, maxTdp: 35, supportFunc: {cpuPower: true}}), 'original-result');
  assert.equal(tracked.initialized, true);
  assert.deepEqual(tracked.snapshot(), {minTdp: 4, maxTdp: 35});
  let hiddenClock = 0, hiddenTarget = 15, hiddenWrites = 0;
  const hiddenIpc = new EventEmitter();
  hiddenIpc.on('tdpChanged', async (_, watts) => { hiddenTarget = watts; hiddenWrites++; });
  const hidden = createLiveController({ipcMain: hiddenIpc, now: () => hiddenClock,
    startupDelayMs: 0, readLimits: async () => tracked.snapshot(), readTarget: async () => hiddenTarget,
    refreshUi: async watts => contents.send('setCurrentSetting', {tdp: watts}), onBatteryPower: () => false});
  assert.equal((await hidden.getState()).nativeConnected, true);
  hidden.suspend();
  await assert.rejects(hidden.setWatts(16), /unavailable/);
  hidden.resume(); hiddenClock = 11999;
  assert.equal((await hidden.getState()).nativeConnected, false);
  hiddenClock = 12000;
  // No renderer, visible window, executeJavaScript or synthetic native Show is
  // available in this test. Recovery uses main-process events and DB state.
  assert.equal((await hidden.getState()).nativeConnected, true);
  assert.equal(hiddenTarget, 15); assert.equal(hiddenWrites, 0);
  await hidden.setWatts(16); assert.equal(hiddenTarget, 16);
  contents.send('setCurrentSetting', {maxTdp: 40});
  assert.equal((await hidden.getState()).maxTdp, 40);
  assert.equal(hiddenTarget, 16); assert.equal(hiddenWrites, 1);
  await hidden.setWatts(40);
  contents.send('setCurrentSetting', {maxTdp: 35});
  await assert.rejects(hidden.setWatts(40), /between 4 and 35/);
  contents.send('setCurrentSetting', {maxTdp: NaN});
  assert.equal((await hidden.getState()).nativeConnected, false);
  contents.send('setCurrentSetting', {maxTdp: 35});
  assert.equal((await hidden.getState()).nativeConnected, true);
  contents.send('initSettingEvent', {minTdp: 4, maxTdp: 35, supportFunc: {cpuPower: false}});
  assert.equal((await hidden.getState()).nativeConnected, false);
  assert.ok(sends > 0); detach(); assert.equal(contents.send, originalSend);
  let attempts = 0; const waits = [], recoveryLogs = [];
  const recovered = await recoverBridge({ connect: async () => { if (++attempts <= 3) throw new Error('temporarily starting'); return {nativeConnected:true}; },
    isAlive: () => true, delay: async ms => waits.push(ms), log: (...args) => recoveryLogs.push(args)});
  assert.equal(recovered.nativeConnected, true);
  assert.deepEqual(waits, [3000, 6000, 12000]); assert.equal(recoveryLogs.length, 1);
  let alive = true;
  assert.equal(await recoverBridge({connect: async () => {alive = false; throw new Error('exited');}, isAlive: () => alive}), null);
  console.log('PASS: hidden startup/resume without renderer access, cooldown gates, event-driven 35/40 W bounds, unchanged current watts, original message forwarding/restoration, and persistent startup retries.');

  const input = new PassThrough();
  const output = new PassThrough();
  const packets = [];
  input.on('data', (bytes) => packets.push(JSON.parse(bytes.toString().slice(0, -1))));
  const transport = new CdpPipe(input, output, { timeoutMs: 30 });
  const result = transport.request('Target.getTargets');
  assert.equal(packets[0].method, 'Target.getTargets');
  output.write('{"id":1,"result":{"target');
  output.write('Infos":[]}}\0');
  assert.deepEqual(await result, { targetInfos: [] });
  await assert.rejects(transport.request('Runtime.evaluate', {}, 'test-session'), /timed out/);
  assert.equal(input.destroyed, false);
  assert.equal(output.destroyed, false);
  const unavailable = new Promise((resolve) => transport.once('unavailable', resolve));
  output.write('{bad}\0');
  assert.match((await unavailable).message, /Invalid/);
  assert.equal(input.destroyed, false);
  assert.equal(output.destroyed, false);
  input.destroy(); output.destroy();

  new vm.Script(bootstrapExpression('C:/profile/native-bridge/live-native-bridge.cjs'));
  for (const file of ['launch-native.cjs', 'cdp-pipe.cjs', 'live-native-bridge.cjs']) {
    new vm.Script(fs.readFileSync(path.join(__dirname, file), 'utf8'), { filename: file });
  }
  console.log('PASS: private CDP framing/timeouts keep handles open; native startup/resume/stop gates, serialization, live limits, production VNode reads, and source parse. No native app or hardware used.');
}
main().catch((error) => { console.error(error.stack || error); process.exitCode = 1; });
