'use strict';
// This is a persistent elevated parent for ONEXConsole's private CDP handles.
// It never changes vendor files and never changes wattage on launch.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { spawn, execFileSync } = require('child_process');
const { CdpPipe } = require('./cdp-pipe.cjs');
const EXE_PATH = 'C:/Program Files/OneXConsole/OneXConsole.exe';
const ASAR_PATH = 'C:/Program Files/OneXConsole/resources/app.asar';
const EXE_SHA256 = '20610ea185f83d2b2acc709681125f0f4e1c9b1dcb2ce3881e9b43e8c067adc9';
const ASAR_SHA256 = 'b232761f04bfffbc7bb77c664d661e1625186b1121c24bc4ba02e7c8b532d5b0';
const wait = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));
function hashFile(file) { return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'); }
function powershell(script) {
  // A Node process started by PowerShell 7 inherits its module search path.
  // Windows PowerShell must load its own Security module, and all failures
  // must terminate the query instead of coercing a missing signature to zero.
  const setup = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
    "$env:PSModulePath=Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\Modules'; ";
  const encoded = Buffer.from(setup + script, 'utf16le').toString('base64');
  return execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
    // At sign-in, signature verification and WMI can take longer while Windows
    // starts services. A cold query must not discard an otherwise valid bridge.
    { encoding: 'utf8', windowsHide: true, timeout: 60000 }).trim();
}
function verifyVendor() {
  if (process.platform !== 'win32') throw new Error('This launcher requires Windows.');
  if (hashFile(EXE_PATH) !== EXE_SHA256 || hashFile(ASAR_PATH) !== ASAR_SHA256) throw new Error('Unreviewed ONEXConsole executable/archive. Native launch refused.');
  const status = powershell("$s = Get-AuthenticodeSignature -LiteralPath 'C:/Program Files/OneXConsole/OneXConsole.exe'; if($null -eq $s){throw 'Signature result missing'}; [int]$s.Status");
  if (status !== '0') throw new Error('The original vendor signature is not valid. Native launch refused.');
  const admin = powershell("$i=[Security.Principal.WindowsIdentity]::GetCurrent(); $p=[Security.Principal.WindowsPrincipal]::new($i); $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)");
  if (admin !== 'True') throw new Error('The private-pipe launcher needs elevation as the same Windows user, matching ONEXConsole\'s executable requirement.');
  const processCount = Number(powershell("@(Get-Process -Name 'OneXConsole' -ErrorAction SilentlyContinue).Count"));
  if (!Number.isInteger(processCount) || processCount !== 0) throw new Error('ONEXConsole is already running. Exit it normally before launching the private bridge.');
  const root = path.join(process.env.LOCALAPPDATA || '', 'OXP3PowerWidget');
  return root;
}
function startEnabledHelper() {
  // Windows' Xbox fullscreen startup mode defers ordinary desktop startup
  // tasks. Respect the user's existing StartupTask state, then activate the
  // signed-in user's registered widget normally once native controls are ready.
  return powershell(`
    $key='HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\OXP3.PowerWidget_pv4gfha69c7qe\\OXP3PowerWidgetHelper';
    $preference=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue;
    if($null -eq $preference -or [int]$preference.State -notin @(2,4)) { 'disabled'; exit 0 };
    $package=Get-AppxPackage -Name 'OXP3.PowerWidget';
    if($null -eq $package -or $package.PackageFamilyName -ne 'OXP3.PowerWidget_pv4gfha69c7qe' -or $package.Publisher -ne 'CN=OXP3Personal' -or $package.Status.ToString() -ne 'Ok') { throw 'The registered widget package is unavailable' };
    Start-Process -FilePath 'ms-gamebar:activate/OXP3.PowerWidget_pv4gfha69c7qe_App_PowerProfiles' -WindowStyle Hidden;
    'requested'
  `);
}
function bootstrapExpression(modulePath) {
  return `(async()=>{
    if (!Array.isArray(window.webpackJsonp)) throw new Error('Native renderer module loader is unavailable');
    window.webpackJsonp.push([['oxp3-live-bootstrap-v1'],{
      'oxp3-live-capture-v1':function(module,exports,load){window.__oxp3LiveModuleLoader=load;}
    },[['oxp3-live-capture-v1']]]);
    const remote=window.__oxp3LiveModuleLoader('191f');
    if (!remote || typeof remote.require!=='function') throw new Error('Native remote module is unavailable');
    const bridge=remote.require(${JSON.stringify(modulePath)});
    return await bridge.startLive(remote.getCurrentWindow().id);
  })()`;
}
async function evaluate(transport, sessionId, expression) {
  const result = await transport.request('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sessionId);
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception && result.exceptionDetails.exception.description || result.exceptionDetails.text || 'Native bootstrap evaluation failed.');
  return result.result && result.result.value;
}
async function bootstrap(transport, modulePath, { timeoutMs = 45000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  let sessionId = null;
  let lastError = null;
  while (Date.now() < deadline) {
    try {
      if (!sessionId) {
        const targets = await transport.request('Target.getTargets');
        const quick = targets.targetInfos.find((target) => target.type === 'page' &&
          /^app:\/\//.test(target.url) && /#\/quicksetting(?:[/?]|$)/.test(target.url));
        if (!quick) { await wait(500); continue; }
        const attached = await transport.request('Target.attachToTarget', { targetId: quick.targetId, flatten: true });
        sessionId = attached.sessionId;
      }
      const state = await evaluate(transport, sessionId, bootstrapExpression(modulePath));
      if (state && state.nativeConnected === true) return state;
    } catch (error) {
      lastError = error;
      if (transport.closed) throw error;
      sessionId = null; // A renderer can be replaced while native is starting.
    }
    await wait(500);
  }
  throw new Error(lastError ? `Native bridge did not become ready: ${lastError.message}` : 'Native bridge did not become ready within its startup interval.');
}
async function recoverBridge({ connect, isAlive, delay = wait, log = () => {} }) {
  let backoff = 3000, lastError = '';
  while (isAlive()) {
    try { return await connect(); }
    catch (error) {
      if (!isAlive()) return null;
      if (error.message !== lastError) { log('native-bridge-retrying', { error: error.message }); lastError = error.message; }
      await delay(backoff);
      backoff = Math.min(30000, backoff * 2);
    }
  }
  return null;
}
async function main() {
  if (process.argv.length > 2) throw new Error('This launcher accepts no arguments; it uses only the reviewed native executable and private pipe.');
  const profileRoot = verifyVendor();
  const modulePath = path.join(__dirname, 'live-native-bridge.cjs');
  if (!fs.existsSync(modulePath)) throw new Error('Native companion module is missing.');
  // Protected startup logs remain visible from both unpackaged Windows startup
  // and packaged development hosts with redirected LocalAppData.
  const protectedRoot = 'C:\\Program Files\\OXP3 Game Power Bridge';
  const moduleRoot = path.resolve(__dirname).toLowerCase();
  const logRoot = moduleRoot === protectedRoot.toLowerCase() || moduleRoot.startsWith(protectedRoot.toLowerCase() + '\\versions\\')
    ? protectedRoot : profileRoot;
  fs.mkdirSync(logRoot, { recursive: true });
  const logPath = path.join(logRoot, 'bridge-launch.log');
  function log(phase, details = {}) {
    const line = JSON.stringify({ at: new Date().toISOString(), phase, ...details });
    fs.appendFileSync(logPath, line + '\n', 'utf8');
    console.log(line);
  }
  const native = spawn(EXE_PATH, ['--startupHidden', '--remote-debugging-pipe'], {
    cwd: path.dirname(EXE_PATH), windowsHide: true,
    stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'],
  });
  // Drain diagnostics without exposing vendor log content or raw debug traffic.
  native.stderr.on('data', () => {});
  native.on('error', (error) => {
    log('native-launch-error', { error: error.message });
    // 20 exclusively means no native process was created. Startup may safely
    // use the original vendor launcher only for this prelaunch failure class.
    if (!native.pid) process.exitCode = 20;
  });
  native.on('exit', (code) => { log('native-exited', { code }); process.exitCode = code ? 31 : 0; });
  const transport = new CdpPipe(native.stdio[3], native.stdio[4]);
  transport.on('unavailable', (error) => log('private-transport-unavailable', { error: error.message }));
  log('native-launched', { pid: native.pid, transport: 'private-inherited-pipes', vendorFilesChanged: false });
  try {
    const version = await transport.request('Browser.getVersion');
    if (!version || !/Electron\/30\.5\.1\b/.test(version.userAgent || '')) throw new Error('Unreviewed native Electron runtime.');
    const state = await recoverBridge({ connect: () => bootstrap(transport, modulePath),
      isAlive: () => !transport.closed && native.exitCode === null && native.signalCode === null, log });
    if (!state) return;
    log('native-bridge-ready', { state });
    try { log('helper-startup', { result: startEnabledHelper() }); }
    catch (error) { log('helper-startup-unavailable', { error: error.message }); }
  } catch (error) {
    log('native-bridge-unavailable', { error: error.message,
      guidance: 'ONEXConsole remains running. Keep this parent open and use native Exit before stopping it.' });
    // No killing, Browser.close, fallback TCP port, or pipe closure. Any one of
    // those would change the native app lifecycle after a bootstrap failure.
  }
  // Child stdio retains the event loop and private handles until native Exit.
}
if (require.main === module) main().catch((error) => { console.error(error.message); process.exitCode = 20; });
module.exports = { bootstrapExpression, bootstrap, recoverBridge, evaluate, verifyVendor, startEnabledHelper, EXE_PATH, ASAR_PATH, EXE_SHA256, ASAR_SHA256 };
