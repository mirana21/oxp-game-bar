'use strict';
const { EventEmitter } = require('events');
class CdpPipe extends EventEmitter {
  constructor(input, output, { timeoutMs = 5000, maxBytes = 2097152 } = {}) {
    super();
    this.input = input;
    this.pending = new Map();
    this.nextId = 1;
    this.timeoutMs = timeoutMs;
    this.maxBytes = maxBytes;
    this.buffer = Buffer.alloc(0);
    this.closed = false;
    output.on('data', (chunk) => this.consume(chunk));
    output.on('end', () => this.fail(new Error('Private native debug pipe ended.')));
    output.on('error', (error) => this.fail(error));
    input.on('error', (error) => this.fail(error));
  }
  consume(chunk) {
    if (this.closed) return;
    this.buffer = Buffer.concat([this.buffer, Buffer.from(chunk)]);
    if (this.buffer.length > this.maxBytes) return this.fail(new Error('Private debug response exceeded its bound.'));
    let end;
    while ((end = this.buffer.indexOf(0)) >= 0) {
      const bytes = this.buffer.subarray(0, end);
      this.buffer = this.buffer.subarray(end + 1);
      let message;
      try { message = JSON.parse(bytes.toString('utf8')); } catch { return this.fail(new Error('Invalid private debug response.')); }
      if (message.id) {
        const entry = this.pending.get(message.id);
        if (!entry) continue;
        this.pending.delete(message.id);
        clearTimeout(entry.timer);
        if (message.error) entry.reject(new Error(message.error.message || 'Native debug request failed.'));
        else entry.resolve(message.result);
      } else this.emit('notification', message);
    }
  }
  request(method, params = {}, sessionId) {
    if (this.closed) return Promise.reject(new Error('Private debug transport unavailable.'));
    if (this.pending.size >= 8) return Promise.reject(new Error('Private debug request queue is full.'));
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Private debug ${method} timed out.`));
      }, this.timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      const message = { id, method, params, ...(sessionId ? { sessionId } : {}) };
      this.input.write(JSON.stringify(message) + '\0', (error) => {
        if (!error) return;
        const entry = this.pending.get(id);
        if (!entry) return;
        this.pending.delete(id);
        clearTimeout(timer);
        reject(error);
      });
    });
  }
  fail(error) {
    if (this.closed) return;
    this.closed = true;
    for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(error); }
    this.pending.clear();
    // Do not close owned handles: Electron treats pipe loss as app quit.
    this.emit('unavailable', error);
  }
}
module.exports = { CdpPipe };
