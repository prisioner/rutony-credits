import test from 'node:test';
import assert from 'node:assert/strict';

let instance = 0;
async function overlay(t, {loop = false, elapsed = 2, playback = 'running', fontFamily = 'Arial', fontLoad, fontsReady = Promise.resolve()} = {}) {
  let clock = 10000, animation, interval;
  const content = {style:{}, children:[], offsetHeight:520, replaceChildren() { this.children = []; }, append(child) { this.children.push(child); }};
  const stage = {style:{}};
  const state = {playback, runId:'run-one', serverTimeUtc:new Date(elapsed * 1000).toISOString(), settings:{loop:false}};
  const run = {runId:'run-one', startedAtUtc:new Date(0).toISOString(), completionToken:'run-token', settings:{fontFamily, fontSize:48, textColor:'#ffffff', speed:80, ...loop === undefined ? {} : {loop}}, sections:[{key:'donations',heading:'Спасибо',names:['Анна — 100 RUB']}]};
  const completions = [], faces = [], added = [], deleted = [];
  let snapshots = 0;
  const globals = {
    document:{getElementById(id) { return id === 'stage' ? stage : content; }, fonts:{ready:fontsReady, add:face => added.push(face), delete:face => deleted.push(face)}, createElement() { return {children:[], append(child) { this.children.push(child); }}; }},
    FontFace:fontLoad ? class { constructor(family, source) { this.family = family; this.source = source; faces.push(this); } async load() { await fontLoad(this); return this; } } : undefined,
    location:{search:''}, innerWidth:1920, innerHeight:1080,
    performance:{now:() => clock}, addEventListener() {},
    requestAnimationFrame(callback) { animation = callback; },
    setInterval(callback) { interval = callback; },
    async fetch(path, options) {
      if (path === '/api/state') return {ok:true, json:async () => ({...state})};
      if (path.startsWith('/api/font-faces?')) return {ok:true, json:async () => [{localName:'Physical-Regular',weight:400}]};
      if (path === '/api/runs/' + state.runId) { snapshots++; return {ok:true, json:async () => ({...run, settings:{...run.settings}, runId:state.runId})}; }
      if (path === '/api/runs/run-one/complete') { completions.push(JSON.parse(options.body)); return {ok:true}; }
      throw new Error(`Unexpected request: ${path}`);
    }
  };
  const previous = Object.fromEntries(Object.keys(globals).map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  for (const [key, value] of Object.entries(globals)) Object.defineProperty(globalThis, key, {configurable:true, writable:true, value});
  t.after(() => { for (const [key, descriptor] of Object.entries(previous)) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; } });
  await import(`../src/RutonyCredits/web/overlay.js?test=${++instance}`);
  await new Promise(setImmediate);
  return {content, state, run, faces, added, deleted, completions, setClock(value) { clock = value; }, get snapshots() { return snapshots; }, async frameAt(milliseconds) { clock = milliseconds; animation(); await new Promise(setImmediate); }, poll:() => interval()};
}

test('a looping snapshot resumes its elapsed phase, never completes a cycle, and Stop clears it', async t => {
  const f = await overlay(t, {loop:true, elapsed:62});
  await f.frameAt(10000);
  assert.equal(f.content.style.transform, 'translateY(920px)');
  assert.equal(f.content.children[0].children[1].textContent, 'Анна — 100 RUB');
  await f.frameAt(28000);
  assert.equal(f.content.style.transform, 'translateY(1080px)');
  assert.equal(f.content.style.visibility, 'visible');
  assert.deepEqual(f.completions, []);
  // Saved settings describe the next snapshot; they must not change this loop.
  await f.poll();
  await f.frameAt(30000);
  assert.equal(f.content.style.transform, 'translateY(920px)');
  f.state.playback = 'stopped';
  await f.poll();
  await f.frameAt(31000);
  assert.equal(f.content.style.visibility, 'hidden');
  assert.equal(f.content.children.length, 0);
  assert.deepEqual(f.completions, []);
});

test('a finite snapshot completes once when its final line leaves the canvas', async t => {
  const f = await overlay(t);
  await f.frameAt(27990);
  assert.equal(f.content.style.visibility, 'visible');
  assert.deepEqual(f.completions, []);
  await f.frameAt(28000);
  assert.equal(f.content.style.visibility, 'hidden');
  assert.deepEqual(f.completions, [{token:'run-token'}]);
  await f.frameAt(30000);
  assert.deepEqual(f.completions, [{token:'run-token'}]);
});

test('an already finished empty run leaves the screen clear without fetching a snapshot', async t => {
  const f = await overlay(t, {loop:true, playback:'finished'});
  await f.frameAt(10000);
  assert.equal(f.content.style.visibility, 'hidden');
  assert.equal(f.content.children.length, 0);
  assert.equal(f.snapshots, 0);
  assert.deepEqual(f.completions, []);
});


test('font families with CSS-significant characters stay one literal family', async t => {
  const name = '123 Font';
  const f = await overlay(t, {fontFamily:name});
  assert.equal(f.content.style.fontFamily, '"123 Font"');
});

function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return {promise,resolve}; }
async function flush() { await new Promise(setImmediate); }

test('Stop while a font is loading clears immediately and late completion cannot revive it', async t => {
  const gate = deferred();
  const f = await overlay(t, {fontLoad:() => gate.promise, fontsReady:gate.promise});
  f.state.playback = 'stopped'; await f.poll();
  gate.resolve(); await flush(); await f.frameAt(11000);
  assert.equal(f.content.style.visibility, 'hidden');
  assert.equal(f.content.children.length, 0);
  assert.equal(f.added.length, f.deleted.length);
  assert.deepEqual(f.completions, []);
});

test('font loading delay counts toward elapsed playback before the first visible frame', async t => {
  const gate = deferred();
  const f = await overlay(t, {fontLoad:() => gate.promise, fontsReady:gate.promise});
  f.setClock(12500); gate.resolve(); await flush(); await f.frameAt(12500);
  assert.equal(f.content.style.transform, 'translateY(720px)');
  assert.match(f.content.style.fontFamily, /^"__rutony_local_\d+"$/);
});

test('replacement run wins even when its predecessor font completes last', async t => {
  const gates = [deferred(),deferred()]; let loads = 0;
  const f = await overlay(t, {fontLoad:() => gates[loads++].promise});
  f.state.runId = 'run-two'; f.run.settings.fontSize = 70;
  await f.poll(); await flush();
  assert.equal(f.faces.length, 2);
  gates[1].resolve(); await flush(); await f.frameAt(11000);
  const family = f.content.style.fontFamily;
  assert.equal(f.content.style.fontSize, '70px');
  gates[0].resolve(); await flush(); await f.frameAt(12000);
  assert.equal(f.content.style.fontFamily, family);
  assert.equal(f.content.style.fontSize, '70px');
  assert.equal(f.content.style.visibility, 'visible');
  assert.equal(f.deleted.length, 1);
});
