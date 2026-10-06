'use strict';

// This module is loaded inside ONEXConsole after its existing bundle boots.
// It delegates current target watts to the same handler used by the native slider.
const net = require('net');

const PIPE_NAME = '\\\\.\\pipe\\OXP3.OneXConsole.Power.v1';
const MAX_BYTES = 4096;
const REQUEST_TIMEOUT_MS = 6000;
const MAX_PENDING_UPDATES = 8;

function createController({ config, settings, ipcMain, isNativeReady = () => true }) {
  if (!config || !settings || !ipcMain) throw new Error('Native bridge dependencies are missing.');
  let queue = Promise.resolve();
  let pendingUpdates = 0;

  function nativeHandler() {
    if (!isNativeReady()) throw new Error('ONEXConsole is still starting or resuming.');
    const handlers = ipcMain.listeners('tdpChanged');
    if (handlers.length !== 1 || typeof handlers[0] !== 'function') {
      throw new Error('ONEXConsole power controls are not ready.');
    }
    if (!config.QuickSettingWindow || typeof config.QuickSettingWindow.setCurrentSetting !== 'function') {
      throw new Error('ONEXConsole quick settings are not ready.');
    }
    return handlers[0];
  }

  function bounds() {
    const minimum = Number(config.minTdp);
    const maximum = Number(config.maxTdp);
    if (!Number.isFinite(minimum) || !Number.isFinite(maximum)) {
      throw new Error('Native power limits are not ready.');
    }
    const minTdp = Math.max(4, Math.ceil(minimum));
    const maxTdp = Math.floor(maximum);
    if (minTdp > maxTdp) throw new Error('Native power limits are invalid.');
    return { minTdp, maxTdp };
  }

  async function getState() {
    let nativeConnected = true;
    try { nativeHandler(); } catch { nativeConnected = false; }
    let limits = { minTdp: null, maxTdp: null };
    try { limits = bounds(); } catch { nativeConnected = false; }
    // Native startup initializes the database and current power limits. Never
    // race that initialization with companion reads or writes.
    const value = nativeConnected ? await settings.load() : null;
    const tdp = value && Number.isInteger(value.tdp) ? value.tdp : null;
    if (tdp === null) nativeConnected = false;
    return {
      nativeConnected,
      tdp,
      ...limits,
      onBatteryPower: Boolean(config.onBatteryPower),
      stateKind: 'configured-target',
    };
  }

  function setWatts(watts) {
    if (!Number.isInteger(watts)) return Promise.reject(new Error('Watts must be an integer.'));
    if (pendingUpdates >= MAX_PENDING_UPDATES) return Promise.reject(new Error('The power update queue is full.'));
    // Recheck readiness and native limits inside the serialized update, because
    // the power-source limits can change while another request is being handled.
    pendingUpdates++;
    const update = queue.then(async () => {
      const handler = nativeHandler();
      const limits = bounds();
      if (watts < limits.minTdp || watts > limits.maxTdp) {
        throw new Error(`Watts must be between ${limits.minTdp} and ${limits.maxTdp}.`);
      }
      await handler({}, watts);
      config.QuickSettingWindow.setCurrentSetting({ tdp: watts });
      const state = await getState();
      if (state.tdp !== watts) throw new Error('Native target wattage was not confirmed.');
      return state;
    });
    queue = update.catch(() => {});
    return update.finally(() => { pendingUpdates--; });
  }

  return { getState, setWatts };
}

async function handleRequest(request, controller) {
  const id = request && typeof request.id === 'string' && request.id.length <= 80 ? request.id : null;
  if (!request || typeof request !== 'object' || Array.isArray(request)) throw new Error('Invalid request.');
  if (request.id !== undefined && id === null) throw new Error('Request ID must be a short string.');
  let state;
  if (request.command === 'getState') state = await controller.getState();
  else if (request.command === 'setWatts') state = await controller.setWatts(request.watts);
  else throw new Error('Unsupported bridge command.');
  return { ...(id === null ? {} : { id }), ok: true, result: state };
}

function attachConnection(socket, { controller }) {
  let buffer = '';
  let receivedBytes = 0;
  let handled = false;
  const deadline = setTimeout(() => socket.destroy(), REQUEST_TIMEOUT_MS);
  deadline.unref?.();
  socket.setEncoding('utf8');
  socket.on('error', () => {});
  socket.on('close', () => clearTimeout(deadline));
  socket.on('data', async (chunk) => {
    if (handled) return;
    receivedBytes += Buffer.byteLength(chunk, 'utf8');
    if (receivedBytes > MAX_BYTES) { handled = true; socket.destroy(); return; }
    buffer += chunk;
    const end = buffer.indexOf('\n');
    if (end < 0) return;
    handled = true;
    let request;
    try {
      request = JSON.parse(buffer.slice(0, end));
      const response = await handleRequest(request, controller);
      if (!socket.destroyed) socket.end(JSON.stringify(response) + '\n');
    } catch (error) {
      if (!socket.destroyed) socket.end(JSON.stringify({
        id: request && typeof request.id === 'string' && request.id.length <= 80 ? request.id : null,
        ok: false,
        error: error.message || 'Bridge request failed.',
      }) + '\n');
    }
  });
}

function start({ config, settings }) {
  if (global.__oxp3PowerBridgeServer) return global.__oxp3PowerBridgeServer;
  try {
    const { ipcMain, app, powerMonitor } = require('electron');
    const controller = createController({ config, settings, ipcMain,
      isNativeReady: () => global.__oxp3PowerBridgeNativeReady === true });
    // Windows controls access to the local named pipe; no shared-key file.
    const server = net.createServer((socket) => attachConnection(socket, { controller }));
    server.maxConnections = 16;
    server.on('error', (error) => console.error('[OXP3 Power Bridge]', error.message));
    server.listen(PIPE_NAME);
    server.unref();
    powerMonitor.on('suspend', () => { global.__oxp3PowerBridgeNativeReady = false; });
    app.once('will-quit', () => { global.__oxp3PowerBridgeNativeReady = false; server.close(); });
    global.__oxp3PowerBridgeServer = server;
    return server;
  } catch (error) {
    // A missing companion must never prevent normal ONEXConsole startup.
    console.error('[OXP3 Power Bridge]', error.message);
    return null;
  }
}

module.exports = { start, createController, handleRequest, attachConnection, PIPE_NAME, MAX_BYTES };
