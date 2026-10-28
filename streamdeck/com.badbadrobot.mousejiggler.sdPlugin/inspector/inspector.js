'use strict';

let socket;
let context;
let settings = {};
let saveTimer;
let buttonTimer;
const appPath = document.getElementById('app-path');
const status = document.getElementById('status');
const detail = document.getElementById('detail');
const testButton = document.getElementById('test-button');
let actionUUID;

function send(event, payload) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ event, action: actionUUID, context, payload }));
}

window.connectElgatoStreamDeckSocket = (port, uuid, registerEvent, info, actionInfo) => {
  const action = JSON.parse(actionInfo);
  actionUUID = action.action;
  // PI commands must use this socket's registered UI identifier. Stream Deck
  // routes it to the action; actionInfo.context is a different connection context.
  context = uuid;
  settings = action.payload?.settings || {};
  appPath.value = typeof settings.appPath === 'string' ? settings.appPath : '';
  socket = new WebSocket('ws://127.0.0.1:' + Number(port));
  socket.onopen = () => {
    socket.send(JSON.stringify({ event: registerEvent, uuid }));
    testButton.disabled = false;
    send('getSettings');
    socket.send(JSON.stringify({ event: 'sendToPlugin', action: action.action, context, payload: { event: 'refresh' } }));
  };
  socket.onmessage = message => {
    let value;
    try { value = JSON.parse(message.data); } catch { return; }
    if (value.event === 'didReceiveSettings') {
      settings = value.payload?.settings || {};
      if (document.activeElement !== appPath) appPath.value = typeof settings.appPath === 'string' ? settings.appPath : '';
    }
    if (value.event === 'sendToPropertyInspector') {
      clearTimeout(buttonTimer);
      testButton.disabled = false;
      const label = value.payload?.status;
      status.dataset.status = label || '';
      status.textContent = label === 'ACTIVE' ? 'Active — jiggling is on' : label === 'IDLE' ? 'Idle — jiggling is off' : label === 'STARTING' ? 'Starting Mouse Jiggler…' : 'Mouse Jiggler is closed or unavailable';
      detail.textContent = value.payload?.detail || '';
    }
  };
  socket.onclose = () => { testButton.disabled = true; };
};

testButton.addEventListener('click', () => {
  if (socket?.readyState !== WebSocket.OPEN) return;
  testButton.disabled = true;
  socket.send(JSON.stringify({ event: 'sendToPlugin', action: actionUUID, context, payload: { event: 'toggle' } }));
  clearTimeout(buttonTimer);
  buttonTimer = setTimeout(() => { testButton.disabled = socket?.readyState !== WebSocket.OPEN; }, 6500);
});

appPath.addEventListener('input', () => {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => { settings = { ...settings, appPath: appPath.value.trim() }; send('setSettings', settings); }, 300);
});
appPath.addEventListener('change', () => {
  clearTimeout(saveTimer);
  settings = { ...settings, appPath: appPath.value.trim() };
  send('setSettings', settings);
});
