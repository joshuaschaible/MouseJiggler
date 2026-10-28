'use strict';

const { connectPlugin } = require('./lib/connection.cjs');

try {
  const connection = connectPlugin();
  const shutdown = () => { connection.controller.dispose(); connection.socket.close(); };
  process.once('SIGINT', shutdown);
  process.once('SIGTERM', shutdown);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
