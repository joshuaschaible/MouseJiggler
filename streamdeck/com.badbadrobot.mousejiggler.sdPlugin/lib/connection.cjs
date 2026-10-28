'use strict';

const WebSocket = require('../vendor/ws');
const { Controller } = require('./controller.cjs');
const { AppClient } = require('./ipc.cjs');

function parseArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length - 1; index += 2) values[argv[index]] = argv[index + 1];
  const port = Number(values['-port']);
  if (!Number.isInteger(port) || port < 1 || port > 65535 ||
      typeof values['-pluginUUID'] !== 'string' || !values['-pluginUUID'] ||
      values['-registerEvent'] !== 'registerPlugin') throw new Error('Stream Deck must launch this plugin.');
  return { port, uuid: values['-pluginUUID'], registerEvent: values['-registerEvent'] };
}

function connectPlugin({ argv = process.argv.slice(2), client = new AppClient(), WebSocketClass = WebSocket, pollMs = 500, log = message => console.error(message) } = {}) {
  const args = parseArguments(argv);
  const socket = new WebSocketClass('ws://127.0.0.1:' + args.port, { perMessageDeflate: false, maxPayload: 1024 * 1024 });
  const controller = new Controller({ client, pollMs, send: message => {
    if (socket.readyState === WebSocketClass.OPEN) socket.send(JSON.stringify(message));
  } });
  socket.on('open', () => socket.send(JSON.stringify({ event: args.registerEvent, uuid: args.uuid })));
  socket.on('message', data => {
    let message;
    try { message = JSON.parse(data.toString()); } catch { return; }
    controller.handle(message).catch(error => log('Mouse Jiggler action: ' + error.message));
  });
  socket.on('error', error => log('Stream Deck connection: ' + error.message));
  socket.on('close', () => controller.dispose());
  return { socket, controller };
}

module.exports = { parseArguments, connectPlugin };
