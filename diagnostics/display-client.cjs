'use strict';
const {requestHelper} = require('./helper-client.cjs');
async function request(value) {
  for (let i = 0; i < 50; i++) {
    try { return await requestHelper(value); }
    catch (error) { if (error.code !== 'ENOENT') throw error; await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  throw new Error('Display helper unavailable.');
}
if (require.main === module) {
  const command = process.argv[2] || 'getDisplayState';
  const value = process.argv[3];
  const payload = command === 'setBrightness' ? {command, brightness: Number(value)} : command === 'setNightLight' ? {command, enabled: value === 'true'} : command === 'setNightLightStrength' ? {command, strength: Number(value)} : {command};
  if (!['getDisplayState', 'setBrightness', 'setNightLight', 'setNightLightStrength'].includes(command)) throw new Error('Unsupported display command.');
  request(payload).then(state => console.log(JSON.stringify(state)), error => {console.error(error.message); process.exitCode = 1;});
}
module.exports = {request};
