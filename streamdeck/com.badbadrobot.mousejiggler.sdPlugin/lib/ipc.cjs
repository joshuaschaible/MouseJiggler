'use strict';

const fs = require('node:fs/promises');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const COMMANDS = new Set(['status', 'toggle', 'on', 'off']);

function validateDescriptor(value) {
  if (!value || value.protocolVersion !== 1 ||
      typeof value.pipeName !== 'string' ||
      !/^MouseJiggler\.StreamDeck\.[A-Za-z0-9_.-]{1,160}$/.test(value.pipeName) ||
      !Number.isSafeInteger(value.processId) || value.processId <= 0) {
    throw new Error('Mouse Jiggler connection information is unavailable.');
  }
  return value;
}

function validateStatus(value, expectedProcessId) {
  if (value?.ok === false) throw new Error(String(value.error || 'Mouse Jiggler could not handle the request.'));
  if (!value || value.ok !== true || value.protocolVersion !== 1 || value.running !== true ||
      typeof value.active !== 'boolean' || !Number.isSafeInteger(value.processId) ||
      value.processId <= 0 || (expectedProcessId && value.processId !== expectedProcessId)) {
    throw new Error('Mouse Jiggler returned an unsupported response.');
  }
  return value;
}

function requestPipe(pipeName, command, options = {}) {
  if (!COMMANDS.has(command)) return Promise.reject(new Error('Unsupported Mouse Jiggler command.'));
  const connect = options.connect || net.createConnection;
  const timeoutMs = options.timeoutMs ?? 1200;
  return new Promise((resolve, reject) => {
    const socket = connect('\\\\.\\pipe\\' + pipeName);
    let received = '';
    let settled = false;
    const finish = (error, response) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error); else resolve(response);
    };
    const timer = setTimeout(() => finish(new Error('Mouse Jiggler did not respond.')), timeoutMs);
    socket.setEncoding('utf8');
    socket.on('connect', () => socket.write(command + '\n'));
    socket.on('data', chunk => {
      received += chunk;
      if (Buffer.byteLength(received, 'utf8') > 8192) return finish(new Error('Mouse Jiggler response was too large.'));
      const newline = received.indexOf('\n');
      if (newline < 0) return;
      try { finish(null, JSON.parse(received.slice(0, newline))); }
      catch { finish(new Error('Mouse Jiggler returned an invalid response.')); }
    });
    socket.on('error', error => finish(error));
    socket.on('end', () => finish(new Error('Mouse Jiggler closed the connection.')));
    socket.on('close', () => finish(new Error('Mouse Jiggler closed the connection.')));
  });
}

function launchDetached(executable, options = {}) {
  const spawnProcess = options.spawn || spawn;
  return new Promise((resolve, reject) => {
    // A separate argv array and shell:false preserve spaces and prevent shell expansion.
    const child = spawnProcess(executable, ['--background'], {
      cwd: path.win32.dirname(executable), detached: true, stdio: 'ignore', windowsHide: true, shell: false
    });
    child.once('error', reject);
    child.once('spawn', () => { child.unref(); resolve(); });
  });
}

class AppClient {
  constructor(options = {}) {
    this.descriptorPath = options.descriptorPath || path.join(
      process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'MouseJiggler', 'streamdeck.json');
    this.readFile = options.readFile || fs.readFile;
    this.stat = options.stat || fs.stat;
    this.request = options.request || requestPipe;
    this.launchProcess = options.launch || launchDetached;
    this.defaultExecutable = options.defaultExecutable || path.win32.join(os.homedir(), 'Dropbox', 'MouseJiggler', 'MouseJiggler.exe');
  }

  async descriptor() {
    const content = await this.readFile(this.descriptorPath, 'utf8');
    if (Buffer.byteLength(content, 'utf8') > 8192) throw new Error('Mouse Jiggler connection information is too large.');
    return validateDescriptor(JSON.parse(content.replace(/^\uFEFF/, '')));
  }

  async query(command) {
    if (!COMMANDS.has(command)) throw new Error('Unsupported Mouse Jiggler command.');
    const descriptor = await this.descriptor();
    return validateStatus(await this.request(descriptor.pipeName, command), descriptor.processId);
  }

  async launch(configuredPath) {
    if (typeof configuredPath === 'string' && /[\r\n\0]/.test(configuredPath)) throw new Error('Choose the MouseJiggler.exe file in the button settings.');
    let rememberedPath;
    try { rememberedPath = (await this.descriptor()).executablePath; } catch { /* First launch has no descriptor yet. */ }
    const executable = (typeof configuredPath === 'string' && configuredPath.trim()) || rememberedPath || this.defaultExecutable;
    if (typeof executable !== 'string' || !path.win32.isAbsolute(executable) ||
        path.win32.basename(executable).toLowerCase() !== 'mousejiggler.exe' || /[\r\n\0]/.test(executable)) {
      throw new Error('Choose the MouseJiggler.exe file in the button settings.');
    }
    if (!(await this.stat(executable)).isFile()) throw new Error('MouseJiggler.exe was not found.');
    await this.launchProcess(executable);
  }
}

module.exports = { AppClient, requestPipe, validateDescriptor, validateStatus, launchDetached };
