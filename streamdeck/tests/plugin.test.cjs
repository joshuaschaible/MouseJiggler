'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter, once } = require('node:events');
const vm = require('node:vm');
const pluginRoot = path.join(__dirname, '..', 'com.badbadrobot.mousejiggler.sdPlugin');
const { AppClient, requestPipe, validateDescriptor, validateStatus, launchDetached } = require(path.join(pluginRoot, 'lib', 'ipc.cjs'));
const { Controller, ACTION } = require(path.join(pluginRoot, 'lib', 'controller.cjs'));
const { connectPlugin, parseArguments } = require(path.join(pluginRoot, 'lib', 'connection.cjs'));
const WebSocket = require(path.join(pluginRoot, 'vendor', 'ws'));

const status = active => ({ ok: true, protocolVersion: 1, running: true, active, processId: 123 });
const descriptor = { protocolVersion: 1, pipeName: 'MouseJiggler.StreamDeck.Test-123.1', executablePath: 'C:\\My Apps\\MouseJiggler.exe', processId: 123 };
const message = (event, context = 'key-1', payload = {}) => ({ event, action: ACTION, context, payload });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitFor(predicate, timeout = 1500) {
  const deadline = Date.now() + timeout;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('Timed out waiting for plugin feedback.');
    await sleep(5);
  }
}

function fixture(options = {}) {
  let active = false;
  const calls = [];
  const sent = [];
  const client = {
    query: async command => { calls.push(command); if (command === 'toggle') active = !active; return status(active); },
    launch: async () => { throw new Error('Should not launch a running app.'); },
    ...options.client
  };
  const controller = new Controller({ client, send: value => sent.push(value), pollMs: 60000, ...options });
  return { controller, client, calls, sent, setActive: value => { active = value; }, title: context => sent.filter(value => value.event === 'setTitle' && value.context === context).at(-1)?.payload.title };
}

async function pipeServer(handler) {
  const pipeName = 'MouseJiggler.StreamDeck.Test-' + process.pid + '-' + Math.random().toString(16).slice(2);
  const sockets = new Set();
  const server = net.createServer(socket => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
    socket.on('error', () => {});
    handler(socket);
  });
  server.listen('\\\\.\\pipe\\' + pipeName);
  await once(server, 'listening');
  return { pipeName, close: async () => { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); } };
}

test('descriptor and status strictly validate protocol, bool state, process identity and local pipe name', () => {
  assert.equal(validateDescriptor(descriptor), descriptor);
  assert.equal(validateStatus(status(true), 123).active, true);
  for (const value of [{ ...descriptor, pipeName: '\\\\remote\\pipe\\other' }, { ...descriptor, protocolVersion: 2 }, { ...descriptor, processId: 0 }]) assert.throws(() => validateDescriptor(value));
  for (const value of [{ ...status(false), active: 'false' }, { ...status(false), running: false }, { ...status(false), protocolVersion: 2 }, { ...status(false), processId: 124 }, { ok: false, error: 'busy' }]) assert.throws(() => validateStatus(value, 123));
});

test('real pipe accepts split UTF8 response, frames one command and validates current descriptor', async () => {
  const commands = [];
  const server = await pipeServer(socket => socket.once('data', data => {
    commands.push(data.toString());
    const value = JSON.stringify(status(true));
    socket.write(value.slice(0, 14));
    setTimeout(() => socket.end(value.slice(14) + '\n'), 5);
  }));
  try {
    const client = new AppClient({ readFile: async () => JSON.stringify({ ...descriptor, pipeName: server.pipeName }) });
    assert.equal((await client.query('status')).active, true);
    assert.deepEqual(commands, ['status\n']);
  } finally { await server.close(); }
});

test('pipe rejects invalid JSON, partial close, oversized response and bounded timeout', async () => {
  for (const behavior of ['json', 'partial', 'large', 'timeout']) {
    const server = await pipeServer(socket => socket.once('data', () => {
      if (behavior === 'json') socket.end('{oops}\n');
      if (behavior === 'partial') socket.end('{"ok":true');
      if (behavior === 'large') socket.end('x'.repeat(9000) + '\n');
    }));
    try { await assert.rejects(requestPipe(server.pipeName, 'status', { timeoutMs: 40 })); }
    finally { await server.close(); }
  }
  await assert.rejects(requestPipe('anything', 'delete'));
});

test('descriptor BOM is supported; oversized/malformed descriptors never reach a pipe', async () => {
  const client = new AppClient({ readFile: async () => '\uFEFF' + JSON.stringify(descriptor), request: async () => status(false) });
  assert.equal((await client.query('status')).active, false);
  for (const content of ['x'.repeat(9000), '{invalid}', JSON.stringify({ ...descriptor, protocolVersion: 9 })]) {
    const invalid = new AppClient({ readFile: async () => content, request: async () => { assert.fail('Invalid descriptor reached pipe.'); } });
    await assert.rejects(invalid.query('status'));
  }
});

test('offline launch chooses configured path, then remembered path, then portable default', async () => {
  const launched = [];
  const client = new AppClient({ readFile: async () => JSON.stringify(descriptor), stat: async () => ({ isFile: () => true }), launch: async file => launched.push(file) });
  await client.launch('C:\\Alternate App\\MouseJiggler.exe');
  await client.launch('');
  client.readFile = async () => { throw new Error('missing'); };
  client.defaultExecutable = 'C:\\Fallback\\MouseJiggler.exe';
  await client.launch();
  assert.deepEqual(launched, ['C:\\Alternate App\\MouseJiggler.exe', descriptor.executablePath, 'C:\\Fallback\\MouseJiggler.exe']);
  await assert.rejects(client.launch('cmd.exe'));
  await assert.rejects(client.launch('C:\\Windows\\cmd.exe'));
  await assert.rejects(client.launch('C:\\MouseJiggler.exe\n'));
});

test('launch uses a literal argv array, no shell and hidden idle background mode', async () => {
  let invocation;
  const child = new EventEmitter();
  child.unref = () => {};
  const promise = launchDetached('C:\\Some Apps\\MouseJiggler.exe', { spawn: (...args) => { invocation = args; queueMicrotask(() => child.emit('spawn')); return child; } });
  await promise;
  assert.equal(invocation[0], 'C:\\Some Apps\\MouseJiggler.exe');
  assert.deepEqual(invocation[1], ['--background']);
  assert.equal(invocation[2].shell, false);
  assert.equal(invocation[2].windowsHide, true);
});

test('button follows actual external changes in both directions and all visible contexts', async () => {
  const f = fixture();
  try {
    await f.controller.handle(message('willAppear'));
    await f.controller.handle(message('willAppear', 'key-2'));
    assert.equal(f.title('key-1'), 'IDLE');
    f.setActive(true);
    await f.controller.refresh();
    assert.equal(f.title('key-1'), 'ACTIVE');
    assert.equal(f.title('key-2'), 'ACTIVE');
    assert.equal(f.sent.filter(v => v.event === 'setState').at(-1).payload.state, 1);
    f.setActive(false);
    await f.controller.refresh();
    assert.equal(f.title('key-1'), 'IDLE');
    assert.equal(f.title('key-2'), 'IDLE');
  } finally { f.controller.dispose(); }
});

test('one keyDown toggles once; keyUp and other action UUIDs are ignored', async () => {
  const f = fixture();
  try {
    await f.controller.handle(message('willAppear'));
    await f.controller.handle(message('keyDown'));
    await f.controller.handle(message('keyUp'));
    await f.controller.handle({ ...message('keyDown'), action: 'other.action' });
    assert.equal(f.calls.filter(v => v === 'toggle').length, 1);
    assert.equal(f.title('key-1'), 'ACTIVE');
  } finally { f.controller.dispose(); }
});

test('rapid presses are serialized and each physical press performs one toggle', async () => {
  const f = fixture();
  let busy = false;
  const base = f.client.query;
  f.client.query = async command => { assert.equal(busy, false); busy = true; await sleep(2); const result = await base(command); busy = false; return result; };
  try {
    await f.controller.handle(message('willAppear'));
    await Promise.all([f.controller.handle(message('keyDown')), f.controller.handle(message('keyDown'))]);
    assert.equal(f.calls.filter(v => v === 'toggle').length, 2);
    assert.equal(f.title('key-1'), 'IDLE');
  } finally { f.controller.dispose(); }
});

test('property inspector Test button uses the same single-toggle action', async () => {
  const f = fixture();
  try {
    await f.controller.handle(message('willAppear'));
    await f.controller.handle(message('sendToPlugin', 'key-1', { event: 'toggle' }));
    assert.equal(f.calls.filter(value => value === 'toggle').length, 1);
    assert.equal(f.title('key-1'), 'ACTIVE');
  } finally { f.controller.dispose(); }
});

test('lost toggle reply is never retried and later status restores truthful ACTIVE feedback', async () => {
  let active = false;
  let mutations = 0;
  const f = fixture({ client: { query: async command => { if (command === 'toggle') { mutations++; active = !active; throw new Error('reply lost'); } return status(active); }, launch: async () => assert.fail('No launch expected') } });
  try {
    await f.controller.handle(message('willAppear'));
    await f.controller.handle(message('keyDown'));
    assert.equal(mutations, 1);
    assert.equal(f.title('key-1'), 'ACTIVE');
    assert.ok(f.sent.some(v => v.event === 'showAlert'));
  } finally { f.controller.dispose(); }
});

test('offline polling never launches or claims ACTIVE; a press launches once then toggles once', async () => {
  let running = false;
  let active = false;
  let launches = 0;
  let mutations = 0;
  const f = fixture({ client: { query: async command => { if (!running) throw new Error('closed'); if (command === 'toggle') { mutations++; active = !active; } return status(active); }, launch: async appPath => { assert.equal(appPath, 'C:\\Jiggler\\MouseJiggler.exe'); launches++; running = true; } } });
  try {
    await f.controller.handle(message('willAppear', 'key-1', { settings: { appPath: 'C:\\Jiggler\\MouseJiggler.exe' } }));
    await f.controller.refresh();
    assert.equal(f.title('key-1'), 'OFFLINE');
    assert.equal(launches, 0);
    await f.controller.handle(message('keyDown'));
    assert.equal(launches, 1);
    assert.equal(mutations, 1);
    assert.equal(f.title('key-1'), 'ACTIVE');
  } finally { f.controller.dispose(); }
});

test('failed launch is bounded, alerts the key and leaves it OFFLINE', async () => {
  let reads = 0;
  const f = fixture({ client: { query: async () => { reads++; throw new Error('closed'); }, launch: async () => {} }, sleep: async () => {}, launchAttempts: 3 });
  try {
    await f.controller.handle(message('willAppear'));
    await f.controller.handle(message('keyDown'));
    assert.equal(f.title('key-1'), 'OFFLINE');
    assert.equal(reads, 6); // appearance + initial press + three startup attempts + final read
    assert.ok(f.sent.some(v => v.event === 'showAlert'));
  } finally { f.controller.dispose(); }
});

test('inspector refresh resends current state even if the hardware image did not change', async () => {
  const f = fixture();
  try {
    await f.controller.handle(message('willAppear'));
    const count = f.sent.filter(v => v.event === 'sendToPropertyInspector').length;
    await f.controller.handle(message('sendToPlugin', 'key-1', { event: 'refresh' }));
    assert.equal(f.sent.filter(v => v.event === 'sendToPropertyInspector').length, count + 1);
    await f.controller.handle(message('willDisappear'));
    assert.equal(f.controller.interval, null);
    assert.equal(f.controller.status, null);
  } finally { f.controller.dispose(); }
});

test('real Stream Deck WebSocket peer registers, toggles and polls external state', async () => {
  const server = new WebSocket.Server({ host: '127.0.0.1', port: 0, perMessageDeflate: false });
  await once(server, 'listening');
  const messages = [];
  let active = false;
  let toggles = 0;
  const peerPromise = once(server, 'connection');
  const errors = [];
  const connection = connectPlugin({ argv: ['-port', String(server.address().port), '-pluginUUID', 'registration-id', '-registerEvent', 'registerPlugin', '-info', '{}'], client: { query: async command => { if (command === 'toggle') { toggles++; active = !active; } return status(active); }, launch: async () => assert.fail('No launch expected') }, pollMs: 25, log: message => errors.push(message) });
  let peer;
  try {
    [peer] = await Promise.race([peerPromise, once(connection.socket, 'error').then(([error]) => { throw error; })]);
    peer.on('message', data => messages.push(JSON.parse(data.toString())));
    await waitFor(() => messages.some(v => v.event === 'registerPlugin'));
    assert.deepEqual(messages[0], { event: 'registerPlugin', uuid: 'registration-id' });
    peer.send(JSON.stringify(message('willAppear')));
    await waitFor(() => messages.some(v => v.event === 'setTitle' && v.payload.title === 'IDLE'));
    peer.send(JSON.stringify(message('keyDown')));
    await waitFor(() => messages.some(v => v.event === 'setTitle' && v.payload.title === 'ACTIVE'));
    peer.send(JSON.stringify(message('keyUp')));
    active = false;
    await waitFor(() => messages.filter(v => v.event === 'setTitle' && v.payload.title === 'IDLE').length >= 2);
    assert.equal(toggles, 1);
    assert.ok(messages.some(v => v.event === 'setImage' && v.payload.image === 'assets/power-on.svg' && v.payload.state === 1));
    assert.deepEqual(errors, []);
  } finally {
    connection.controller.dispose();
    connection.socket.terminate();
    peer?.terminate();
    await new Promise(resolve => server.close(resolve));
  }
});

test('registration rejects invalid ports and unexpected registration event', () => {
  assert.throws(() => parseArguments(['-port', '0', '-pluginUUID', 'test', '-registerEvent', 'registerPlugin']));
  assert.throws(() => parseArguments(['-port', '80', '-pluginUUID', 'test', '-registerEvent', 'sendToPlugin']));
});

test('property inspector routes settings, refresh and Test button with its registered UI UUID', async () => {
  const elements = new Map();
  for (const id of ['app-path', 'status', 'detail', 'test-button']) elements.set(id, {
    value: '', textContent: '', disabled: true, dataset: {}, listeners: {},
    addEventListener(event, listener) { this.listeners[event] = listener; }
  });
  const sockets = [];
  class InspectorSocket {
    static OPEN = 1;
    constructor(url) { this.url = url; this.readyState = 1; this.messages = []; sockets.push(this); }
    send(value) { this.messages.push(JSON.parse(value)); }
  }
  const window = {};
  const timers = new Map();
  let timerId = 0;
  vm.runInNewContext(await fs.readFile(path.join(pluginRoot, 'inspector', 'inspector.js'), 'utf8'), {
    window, document: { getElementById: id => elements.get(id), activeElement: null }, WebSocket: InspectorSocket,
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; }, clearTimeout: id => timers.delete(id)
  }, { filename: 'inspector.js' });
  const uiUUID = 'registered-property-inspector-uuid';
  window.connectElgatoStreamDeckSocket('28196', uiUUID, 'registerPropertyInspector', '{}', JSON.stringify({
    action: ACTION, context: 'different-action-context-that-must-not-be-used', payload: { settings: { preserved: true } }
  }));
  const socket = sockets[0];
  socket.onopen();
  assert.deepEqual(socket.messages[0], { event: 'registerPropertyInspector', uuid: uiUUID });
  assert.equal(socket.url, 'ws://127.0.0.1:28196');
  elements.get('test-button').listeners.click();
  elements.get('app-path').value = 'C:\\Another Folder\\MouseJiggler.exe';
  elements.get('app-path').listeners.change();
  const commands = socket.messages.slice(1);
  assert.deepEqual(commands.map(value => value.event), ['getSettings', 'sendToPlugin', 'sendToPlugin', 'setSettings']);
  for (const command of commands) {
    assert.equal(command.context, uiUUID, command.event + ' must use the registered UI UUID');
    assert.equal(command.action, ACTION, command.event + ' must identify its action type');
  }
  assert.deepEqual(commands[1].payload, { event: 'refresh' });
  assert.deepEqual(commands[2].payload, { event: 'toggle' });
  assert.deepEqual(commands[3].payload, { preserved: true, appPath: 'C:\\Another Folder\\MouseJiggler.exe' });
  socket.onmessage({ data: JSON.stringify({ event: 'sendToPropertyInspector', payload: { status: 'ACTIVE', detail: 'Jiggling is on.' } }) });
  assert.equal(elements.get('status').textContent, 'Active — jiggling is on');
  assert.equal(elements.get('test-button').disabled, false);
  socket.onclose();
  assert.equal(elements.get('test-button').disabled, true);
});
