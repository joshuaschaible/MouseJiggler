'use strict';

const ACTION = 'com.badbadrobot.mousejiggler.toggle';
const IMAGES = { ACTIVE: 'assets/power-on.svg', IDLE: 'assets/power-off.svg', OFFLINE: 'assets/power-offline.svg', STARTING: 'assets/power-offline.svg' };

class Controller {
  constructor({ client, send, pollMs = 500, sleep = ms => new Promise(resolve => setTimeout(resolve, ms)), launchAttempts = 20 }) {
    this.client = client;
    this.send = send;
    this.pollMs = pollMs;
    this.sleep = sleep;
    this.launchAttempts = launchAttempts;
    this.contexts = new Map();
    this.status = null;
    this.queue = Promise.resolve();
    this.pending = 0;
    this.interval = null;
    this.closed = false;
  }

  enqueue(task) {
    if (this.closed) return Promise.resolve();
    this.pending++;
    const result = this.queue.then(task);
    this.queue = result.catch(() => {}).finally(() => { this.pending--; });
    return result;
  }

  startPolling() {
    if (this.interval || this.closed) return;
    this.interval = setInterval(() => {
      if (this.pending === 0 && this.contexts.size) this.enqueue(() => this.refresh()).catch(() => {});
    }, this.pollMs);
    this.interval.unref?.();
  }

  async handle(message) {
    if (this.closed || !message || typeof message.event !== 'string') return;
    if (message.event === 'applicationDidLaunch' || message.event === 'applicationDidTerminate') {
      return this.enqueue(() => this.refresh());
    }
    if (message.action !== ACTION || typeof message.context !== 'string') return;
    const context = message.context;
    switch (message.event) {
      case 'willAppear':
        this.contexts.set(context, { settings: message.payload?.settings || {}, rendered: null });
        this.startPolling();
        this.paint(context, this.status ? (this.status.active ? 'ACTIVE' : 'IDLE') : 'OFFLINE');
        return this.enqueue(() => this.refresh());
      case 'willDisappear':
        this.contexts.delete(context);
        if (!this.contexts.size) { clearInterval(this.interval); this.interval = null; this.status = null; }
        return;
      case 'didReceiveSettings': {
        const entry = this.contexts.get(context);
        if (entry) entry.settings = message.payload?.settings || {};
        return this.enqueue(() => this.refresh());
      }
      case 'keyDown':
        // KeyUp is deliberately ignored. A press issues only one mutation.
        return this.enqueue(() => this.toggle(context));
      case 'propertyInspectorDidAppear':
        if (this.contexts.has(context)) this.contexts.get(context).rendered = null;
        return this.enqueue(() => this.refresh());
      case 'sendToPlugin':
        if (message.payload?.event === 'toggle') return this.enqueue(() => this.toggle(context));
        if (message.payload?.event === 'refresh') {
          if (this.contexts.has(context)) this.contexts.get(context).rendered = null;
          return this.enqueue(() => this.refresh());
        }
        return;
    }
  }

  paint(context, title, detail) {
    const entry = this.contexts.get(context);
    if (!entry) return;
    const signature = title + '|' + (detail || '');
    if (entry.rendered === signature) return;
    entry.rendered = signature;
    const state = title === 'ACTIVE' ? 1 : 0;
    this.send({ event: 'setState', context, payload: { state } });
    this.send({ event: 'setImage', context, payload: { image: IMAGES[title], state, target: 0 } });
    this.send({ event: 'setTitle', context, payload: { title, state, target: 0 } });
    this.send({ event: 'sendToPropertyInspector', action: ACTION, context, payload: {
      status: title, detail: detail || (title === 'ACTIVE' ? 'Jiggling is on.' : title === 'IDLE' ? 'Jiggling is off.' : 'Press the button to start Mouse Jiggler.')
    } });
  }

  publish(status, title, detail) {
    this.status = status;
    const label = title || (status ? (status.active ? 'ACTIVE' : 'IDLE') : 'OFFLINE');
    for (const context of this.contexts.keys()) this.paint(context, label, detail);
  }

  async refresh() {
    if (!this.contexts.size || this.closed) return;
    try { this.publish(await this.client.query('status')); }
    catch { this.publish(null); }
  }

  async toggle(context) {
    if (!this.contexts.has(context) || this.closed) return;
    try {
      try { this.publish(await this.client.query('status')); }
      catch {
        this.publish(null, 'STARTING', 'Starting Mouse Jiggler…');
        await this.client.launch(this.contexts.get(context)?.settings.appPath);
        let ready = false;
        for (let attempt = 0; attempt < this.launchAttempts; attempt++) {
          if (this.closed) return;
          try { this.publish(await this.client.query('status')); ready = true; break; }
          catch { await this.sleep(200); }
        }
        if (!ready) throw new Error('Mouse Jiggler could not be reached. Check its app path and update the app.');
      }
      // Never retry this mutation: a lost reply may mean it already succeeded.
      this.publish(await this.client.query('toggle'));
    } catch (error) {
      this.publish(null, 'OFFLINE', error?.message || 'Mouse Jiggler could not be reached.');
      if (this.contexts.has(context)) this.send({ event: 'showAlert', context });
      // A status read can recover the truthful result after an ambiguous reply.
      try { this.publish(await this.client.query('status')); } catch { /* Remain offline until a later poll. */ }
    }
  }

  dispose() {
    this.closed = true;
    clearInterval(this.interval);
    this.interval = null;
    this.contexts.clear();
  }
}

module.exports = { Controller, ACTION };
