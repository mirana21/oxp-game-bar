'use strict';
// Loaded into the original ONEXConsole main process through its enabled remote
// module. Original executable/ASAR bytes and all integrity fuses stay intact.
const path = require('path');
const net = require('net');
const { attachConnection, PIPE_NAME } = require('./oxp3-power-bridge.cjs');
const EXPECTED_VERSION = '0.10.3-fix2';
const MAX_PENDING = 8;

function readQuickState() {
  const root = document.getElementById('app');
  const stack = root && root._vnode ? [[root._vnode, 0]] : [];
  const seen = new Set();
  let visited = 0;
  while (stack.length && visited++ < 500) {
    const [value, depth] = stack.pop();
    if (!value || typeof value !== 'object' || seen.has(value) || depth > 20) continue;
    seen.add(value);
    const component = value.component;
    const data = component && component.data;
    if (data && Number.isInteger(data.tdp) && Number.isFinite(data.minTdp) &&
        Number.isFinite(data.maxTdp) && data.supportFunc && data.supportFunc.cpuPower === true) {
      return { tdp: data.tdp, minTdp: data.minTdp, maxTdp: data.maxTdp };
    }
    for (const child of [component && component.subTree,
      value.suspense && value.suspense.activeBranch]) {
      if (child) stack.push([child, depth + 1]);
    }
    if (Array.isArray(value.children)) for (const child of value.children) stack.push([child, depth + 1]);
  }
  return null;
}
const READ_QUICK_STATE_EXPRESSION = `(${readQuickState.toString()})()`;

// Native initialization and limit changes are sent from the main process even
// while the renderer is hidden or asleep. Observe those messages without
// changing their arguments, ordering, return value or native implementation.
function createLimitTracker() {
  let minTdp = null, maxTdp = null, supported = false, revision = 0, initialized = false;
  function observe(channel, data) {
    if (!['initSettingEvent', 'setCurrentSetting'].includes(channel) || !data || typeof data !== 'object') return;
    if (!['minTdp', 'maxTdp', 'supportFunc'].some(key => Object.hasOwn(data, key))) return;
    if (channel === 'initSettingEvent') initialized = true;
    revision++;
    if (Object.hasOwn(data, 'minTdp')) minTdp = data.minTdp;
    if (Object.hasOwn(data, 'maxTdp')) maxTdp = data.maxTdp;
    if (Object.hasOwn(data, 'supportFunc')) supported = data.supportFunc?.cpuPower === true;
  }
  function snapshot() {
    return supported && Number.isFinite(minTdp) && Number.isFinite(maxTdp) &&
      Math.ceil(minTdp) >= 1 && Math.floor(maxTdp) >= Math.ceil(minTdp)
      ? { minTdp, maxTdp } : null;
  }
  return { observe, snapshot, get revision() { return revision; }, get initialized() { return initialized; } };
}
function observeNativeLimits(webContents, tracker) {
  const original = webContents.send;
  function observedSend(...args) {
    const result = Reflect.apply(original, this, args);
    tracker.observe(args[0], args[1]);
    return result;
  }
  webContents.send = observedSend;
  return () => { if (webContents.send === observedSend) webContents.send = original; };
}

function createLiveController({ ipcMain, readTarget, readLimits, refreshUi, onBatteryPower,
  now = () => Date.now(), startupDelayMs = 8000, resumeDelayMs = 12000 }) {
  let availableAt = now() + startupDelayMs;
  let suspended = false;
  let stopping = false;
  let pending = 0;
  let queue = Promise.resolve();
  function handler() {
    if (stopping || suspended || now() < availableAt) throw new Error('ONEXConsole is starting, resuming, or stopping.');
    const handlers = ipcMain.listeners('tdpChanged');
    if (handlers.length !== 1 || typeof handlers[0] !== 'function') throw new Error('Native power controls are unavailable.');
    return handlers[0];
  }
  async function getState() {
    let nativeConnected = true;
    try { handler(); } catch { nativeConnected = false; }
    let ui = null;
    let tdp = null;
    if (nativeConnected) {
      try {
        [ui, tdp] = await Promise.all([readLimits(), readTarget()]);
        if (!ui || !Number.isFinite(ui.minTdp) || !Number.isFinite(ui.maxTdp) || !Number.isInteger(tdp)) nativeConnected = false;
      } catch { nativeConnected = false; }
    }
    const minTdp = ui ? Math.max(4, Math.ceil(ui.minTdp)) : null;
    const maxTdp = ui ? Math.floor(ui.maxTdp) : null;
    if (!Number.isInteger(minTdp) || !Number.isInteger(maxTdp) || minTdp > maxTdp) nativeConnected = false;
    // Recheck the lifecycle after asynchronous reads, including suspend/quit.
    try { handler(); } catch { nativeConnected = false; }
    return { nativeConnected, tdp: Number.isInteger(tdp) ? tdp : null,
      minTdp, maxTdp, onBatteryPower: Boolean(onBatteryPower()), stateKind: 'configured-target' };
  }
  function setWatts(watts) {
    if (!Number.isInteger(watts)) return Promise.reject(new Error('Watts must be an integer.'));
    if (pending >= MAX_PENDING) return Promise.reject(new Error('The power update queue is full.'));
    pending++;
    const update = queue.then(async () => {
      const state = await getState();
      if (!state.nativeConnected) throw new Error('Native power controls are unavailable or still starting.');
      if (watts < state.minTdp || watts > state.maxTdp) throw new Error(`Watts must be between ${state.minTdp} and ${state.maxTdp}.`);
      const native = handler();
      await native({}, watts);
      await refreshUi(watts);
      const result = await getState();
      if (result.tdp !== watts) throw new Error('Native configured target was not confirmed.');
      return result;
    });
    queue = update.catch(() => {});
    return update.finally(() => { pending--; });
  }
  return { getState, setWatts,
    suspend() { suspended = true; },
    resume() { suspended = false; availableAt = now() + resumeDelayMs; },
    stop() { stopping = true; } };
}

async function startLive(quickWindowId) {
  if (global.__oxp3LivePowerBridge) return global.__oxp3LivePowerBridge.status();
  const { app, BrowserWindow, ipcMain, powerMonitor } = require('electron');
  if (app.getVersion() !== EXPECTED_VERSION) throw new Error('Unreviewed ONEXConsole version.');
  if (!Number.isInteger(quickWindowId)) throw new Error('Invalid native quick-settings window.');
  const quickWindow = BrowserWindow.fromId(quickWindowId);
  if (!quickWindow || !/#\/quicksetting(?:[/?]|$)/.test(quickWindow.webContents.getURL())) throw new Error('Expected native quick-settings renderer.');
  const limits = createLimitTracker();
  const stopObserving = observeNativeLimits(quickWindow.webContents, limits);
  const sqlite = require(path.join(process.resourcesPath, 'app.asar', 'node_modules', 'sqlite3'));
  const databasePath = path.join(app.getPath('userData'), 'db', 'core.db');
  const db = await new Promise((resolve, reject) => {
    const database = new sqlite.Database(databasePath, sqlite.OPEN_READONLY, (error) => {
      if (error) reject(error); else resolve(database);
    });
  }).catch(error => { stopObserving(); throw error; });
  db.configure('busyTimeout', 1000);
  const readTarget = () => new Promise((resolve, reject) => db.get('SELECT tdp FROM setting WHERE id = 1', [], (error, row) => {
    if (error) reject(error); else resolve(row && row.tdp);
  }));
  const readLimits = async () => {
    if (limits.snapshot()) return limits.snapshot();
    if (limits.initialized) return null;
    // Only seed when attachment missed native initialization. Never execute
    // renderer JavaScript on normal polls, writes or wake recovery once ready.
    const revision = limits.revision;
    if (quickWindow.isDestroyed() || quickWindow.webContents.isDestroyed()) return null;
    let timeout;
    try {
      const state = await Promise.race([
        quickWindow.webContents.executeJavaScript(READ_QUICK_STATE_EXPRESSION),
        new Promise(resolve => { timeout = setTimeout(() => resolve(null), 1500); timeout.unref(); })
      ]);
      if (state && revision === limits.revision && !limits.snapshot()) {
        limits.observe('initSettingEvent', { ...state, supportFunc: { cpuPower: true } });
      }
    } finally { clearTimeout(timeout); }
    return limits.snapshot();
  };
  // Verify the read-only native database before exposing any control endpoint.
  if (!Number.isInteger(await readTarget())) { stopObserving(); db.close(); throw new Error('Native current wattage is unavailable.'); }
  const controller = createLiveController({ ipcMain, readTarget, readLimits,
    refreshUi: async (watts) => {
      if (quickWindow.isDestroyed() || quickWindow.webContents.isDestroyed()) throw new Error('Native quick settings closed.');
      quickWindow.webContents.send('setCurrentSetting', { tdp: watts });
    }, onBatteryPower: () => powerMonitor.isOnBatteryPower() });
  const server = net.createServer((socket) => attachConnection(socket, { controller }));
  server.maxConnections = 16;
  try {
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(PIPE_NAME, () => { server.removeListener('error', reject); resolve(); });
    });
  } catch (error) { stopObserving(); db.close(); throw error; }
  server.on('error', (error) => console.error('[OXP3 Live Power Bridge]', error.message));
  server.unref();
  powerMonitor.on('suspend', () => controller.suspend());
  powerMonitor.on('resume', () => controller.resume());
  app.once('before-quit', () => controller.stop());
  quickWindow.once('closed', () => controller.stop());
  app.once('will-quit', () => { controller.stop(); stopObserving(); server.close(); db.close(); });
  const bridge = { controller, server, status: () => controller.getState() };
  global.__oxp3LivePowerBridge = bridge;
  return bridge.status();
}

module.exports = { startLive, createLiveController, createLimitTracker, observeNativeLimits, readQuickState, READ_QUICK_STATE_EXPRESSION };
