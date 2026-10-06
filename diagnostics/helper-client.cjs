'use strict';
const net = require('net');
function requestHelper(request) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection('\\\\.\\pipe\\OXP3.PowerWidget.Bridge.v1');
    let buffer = '', settled = false;
    const timer = setTimeout(() => finish(new Error('Helper request timed out.')), 25000);
    function finish(error, result) {
      if (settled) return;
      settled = true; clearTimeout(timer); socket.destroy();
      if (error) reject(error); else resolve(result);
    }
    socket.setEncoding('utf8');
    socket.once('connect', () => socket.write(JSON.stringify(request) + '\n'));
    socket.on('error', error => finish(error));
    socket.on('end', () => { if (!settled) finish(new Error('Helper closed before replying.')); });
    socket.on('data', data => {
      buffer += data;
      if (buffer.length > 4 * 1024 * 1024) return finish(new Error('Helper reply exceeded its limit.'));
      const end = buffer.indexOf('\n'); if (end < 0) return;
      try {
        const reply = JSON.parse(buffer.slice(0, end));
        if (reply.ok !== true) throw new Error(reply.error || 'Helper request failed.');
        finish(null, reply.result);
      } catch (error) { finish(error); }
    });
  });
}
if (require.main === module) {
  const command = process.argv[2] || 'getState';
  if (!['getState', 'listGames'].includes(command)) throw new Error('CLI permits read-only state/library queries.');
  requestHelper({ command }).then(result => console.log(JSON.stringify(result)), error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { requestHelper };
