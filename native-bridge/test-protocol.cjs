'use strict';
const assert = require('assert/strict');
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const { EventEmitter } = require('events');
const bridge = require('./oxp3-power-bridge.cjs');

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function main() {

  const events = new EventEmitter();
  const settingsValue = { tdp: 15 };
  const log = [];
  let active = 0;
  let overlaps = 0;
  const handler = async (event, watts) => {
    assert.deepEqual(event, {});
    active++;
    if (active > 1) overlaps++;
    log.push(['handler', watts]);
    await delay(5);
    settingsValue.tdp = watts;
    active--;
  };
  events.on('tdpChanged', handler);
  const config = {
    minTdp: 4,
    maxTdp: 35,
    onBatteryPower: true,
    QuickSettingWindow: { setCurrentSetting: (value) => log.push(['ui', value.tdp]) },
  };
  const controller = bridge.createController({ config, settings: { load: async () => settingsValue }, ipcMain: events });
  const first = await bridge.handleRequest({ command: 'getState' }, controller);
  assert.equal(first.ok, true);
  assert.equal(first.result.tdp, 15);
  assert.equal(first.result.nativeConnected, true);
  assert.equal(first.result.minTdp, 4);
  assert.equal(first.result.maxTdp, 35);
  assert.equal(first.result.stateKind, 'configured-target');
  await assert.rejects(bridge.handleRequest([], controller), /Invalid request/);
  await assert.rejects(bridge.handleRequest({ command: 'unknown' }, controller), /Unsupported/);
  for (const watts of [3, 36, 4.1, '17', null, NaN]) await assert.rejects(controller.setWatts(watts));
  assert.deepEqual(log, []);
  config.maxTdp = 12;
  await assert.rejects(controller.setWatts(13), /between 4 and 12/);
  config.maxTdp = 40;
  assert.equal((await controller.getState()).maxTdp, 40);
  assert.equal(settingsValue.tdp, 15);
  assert.deepEqual(log, []);
  assert.equal((await controller.setWatts(40)).tdp, 40);
  await assert.rejects(controller.setWatts(41), /between 4 and 40/);
  log.length = 0;
  config.maxTdp = 35;
  await assert.rejects(controller.setWatts(40), /between 4 and 35/);
  const results = await Promise.all([controller.setWatts(17), controller.setWatts(20)]);
  assert.deepEqual(results.map((value) => value.tdp), [17, 20]);
  assert.equal(overlaps, 0);
  assert.deepEqual(log, [['handler', 17], ['ui', 17], ['handler', 20], ['ui', 20]]);
  events.on('tdpChanged', () => {});
  await assert.rejects(controller.setWatts(21), /not ready/);
  assert.equal((await controller.getState()).nativeConnected, false);
  events.removeAllListeners('tdpChanged');
  events.on('tdpChanged', handler);
  let nativeReady = false;
  let readsDuringStartup = 0;
  const startupController = bridge.createController({ config,
    settings: { load: async () => { readsDuringStartup++; return settingsValue; } },
    ipcMain: events, isNativeReady: () => nativeReady });
  assert.equal((await startupController.getState()).nativeConnected, false);
  assert.equal(readsDuringStartup, 0);
  await assert.rejects(startupController.setWatts(21), /still starting or resuming/);
  assert.equal(readsDuringStartup, 0);
  nativeReady = true;
  assert.equal((await startupController.getState()).nativeConnected, true);

  class FakeSocket extends EventEmitter {
    constructor() { super(); this.destroyed = false; this.reply = null; }
    setEncoding() {}
    end(text) { this.reply = text; this.destroyed = true; this.emit('close'); }
    destroy() { this.destroyed = true; this.emit('close'); }
  }
  const socket = new FakeSocket();
  bridge.attachConnection(socket, { controller });
  socket.emit('data', JSON.stringify({ command: 'getState' }) + '\n');
  await delay(10);
  assert.equal(JSON.parse(socket.reply).result.tdp, 20);
  const invalidSocket = new FakeSocket();
  bridge.attachConnection(invalidSocket, { controller });
  invalidSocket.emit('data', 'x'.repeat(bridge.MAX_BYTES + 1));
  assert.equal(invalidSocket.destroyed, true);
  assert.equal(invalidSocket.reply, null);
  const badJson = new FakeSocket();
  bridge.attachConnection(badJson, { controller });
  badJson.emit('data', '{bad}\n');
  await delay(10);
  assert.equal(JSON.parse(badJson.reply).ok, false);

  console.log('PASS: keyless requests, bounds, readiness, serialized native handler/UI updates, packet bounds, request isolation and changed-limit rejection. No native handler or hardware was invoked.');
}

main().catch((error) => { console.error(error.stack || error); process.exitCode = 1; });
