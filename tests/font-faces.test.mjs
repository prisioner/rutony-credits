import test from 'node:test';
import assert from 'node:assert/strict';
import {loadLocalFont, cssString} from '../src/RutonyCredits/web/local-font.js';

function fixture(metadata = [{localName:'Exact-Regular',weight:400},{localName:'Exact-Bold',weight:700}], options = {}) {
  const constructed = [], added = [], deleted = [], requests = [];
  class Face {
    constructor(family, source, descriptors) { Object.assign(this, {family, source, ...descriptors}); constructed.push(this); }
    async load() { if (options.fail) throw Error('missing local face'); if (options.hang) await new Promise(() => {}); return this; }
  }
  const environment = {
    FontFace:options.unsupported ? undefined : Face,
    fonts:{ready:Promise.resolve(), add:f => added.push(f), delete:f => deleted.push(f)},
    async fetch(path) { requests.push(path); if (options.httpError) return {ok:false}; if (options.networkError) throw Error(); if (options.fetchHang) return new Promise(() => {}); return {ok:true, json:async () => metadata}; },
    timeout:20
  };
  return {environment, constructed, added, deleted, requests};
}

test('exact local face names bind ordinary and distinct bold, using an internal alias', async () => {
  const f = fixture();
  const result = await loadLocalFont('Legacy Family', f.environment);
  assert.match(result.family, /^"__rutony_local_\d+"$/);
  assert.deepEqual(f.constructed.map(x => [x.source,x.weight,x.style]), [['local("Exact-Regular")','400','normal'],['local("Exact-Bold")','700','normal']]);
  assert.equal(f.added.length, 2);
  assert.equal(f.requests[0], '/api/font-faces?family=Legacy%20Family');
  result.dispose(); assert.equal(f.deleted.length, 2);
});

test('one physical face is registered only at400 so browser bold synthesis stays available', async () => {
  const f = fixture([{localName:'Single-Black',weight:400}]);
  await loadLocalFont('Black', f.environment);
  assert.equal(f.constructed.length, 1); assert.equal(f.added[0].weight, '400');
});

test('CSS significant names remain quoted local strings and never form URL sources', async () => {
  const name = 'a"\\);url(https://invalid.example)/*';
  const f = fixture([{localName:name,weight:400}]);
  await loadLocalFont(name, f.environment);
  assert.equal(f.constructed[0].source, 'local(' + cssString(name) + ')');
  assert.equal(cssString('x\ny'), '"x\\a y"');
  assert.equal(cssString('x\0y'), '"x\\fffd y"');
});

for (const [name, metadata, options] of [
  ['unknown', [], {}], ['old backend', [], {httpError:true}], ['network', [], {networkError:true}],
  ['unsupported', [], {unsupported:true}], ['local rejection', undefined, {fail:true}],
  ['local timeout', undefined, {hang:true}], ['HTTP timeout', undefined, {fetchHang:true}],
  ['duplicate physical face', [{localName:'Same',weight:400},{localName:'same',weight:700}], {}],
  ['unsafe control', [{localName:'bad\nname',weight:400}], {}],
  ['unexpected weight', [{localName:'Bad',weight:900}], {}],
  ['missing ordinary', [{localName:'OnlyBold',weight:700}], {}]
]) test('fallback keeps original family on ' + name, async () => {
  const f = fixture(metadata, options);
  const result = await loadLocalFont('Legacy Family', f.environment);
  assert.equal(result.family, '"Legacy Family"'); assert.equal(f.added.length, 0);
});

test('a local load completing after timeout never registers a late face', async () => {
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const f = fixture([{localName:'SlowFace',weight:400}]);
  const Face = f.environment.FontFace;
  f.environment.FontFace = class extends Face { async load() { await gate; return this; } };
  const result = await loadLocalFont('Slow Legacy', f.environment);
  assert.equal(result.family, '"Slow Legacy"');
  release(); await new Promise(setImmediate);
  assert.equal(f.added.length, 0);
});

test('a partially rejected ordinary/bold load registers neither face', async () => {
  const f = fixture();
  const Face = f.environment.FontFace;
  f.environment.FontFace = class extends Face { async load() { if (this.weight === '700') throw Error(); return this; } };
  const result = await loadLocalFont('Legacy', f.environment);
  assert.equal(result.family, '"Legacy"'); assert.equal(f.added.length, 0);
});
