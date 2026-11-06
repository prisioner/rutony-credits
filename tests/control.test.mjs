import test from 'node:test';
import assert from 'node:assert/strict';

const keys = ['vk-raids', 'twitch-raids', 'donations', 'followers', 'subscribers', 'moderators'];
let instance = 0;
async function control(t, {options = {}, legacy = false, fonts = ['Arial', 'Шрифт Один'], fontError = false, obsAvailable = true, obsGetError = false, obsOptions = {}} = {}) {
  const settings = {fontFamily:'Georgia', fontSize:52, speed:100, textColor:'#ffffff', headings:Object.fromEntries((legacy ? keys.slice(0, 5) : keys).map(key => [key, key])), ...options};
  const state = {sessionId:'session-one', runId:null, hostMode:'standalone', counts:{}, eventCount:0, playback:'stopped', settings};
  const obs = {available:obsAvailable, options:{enabled:false, port:4455, sceneName:'Final', autoStart:true, resetOnStop:true, ...obsOptions}, connected:true, phase:'connected', error:null, hasPassword:true, scenes:['Game', 'Final']};
  const nodes = {}, requests = [], intervals = new Map(), handlers = {};
  class Element extends EventTarget {
    constructor() {
      super(); this.ownerDocument = document; this.style = {}; this.children = []; this.value = ''; this.checked = false; this.attributes = {};
      const classes = new Set(); this.classList = {toggle(name, active) { active ? classes.add(name) : classes.delete(name); }, contains:name => classes.has(name)};
    }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    removeAttribute(name) { delete this.attributes[name]; }
    replaceChildren(...children) { this.children = children; }
    append(...children) { this.children.push(...children); for (const child of children) register(child); }
    scrollIntoView() {}
  }
  function register(node) { if (node.name) form.elements[node.name] = node; if (node.id) nodes[node.id] = node; for (const child of node.children || []) register(child); }
  const document = {activeElement:null, getElementById:id => nodes[id] ?? null, createElement:() => new Element(), querySelector:selector => selector === 'iframe' ? {style:{}} : {content:'control-token'}};
  const form = new Element(); form.elements = {};
  const obsForm = new Element(); obsForm.elements = {};
  for (const id of ['counts','headings','obs-url','run','preview','stop','reset','save','notice','connection','period','playback','playback-mode','font-family','fonts','font-hint','saved','copy','preview-frame','obs-panel','obs-connection','obs-error','obs-save','obs-saved','obs-password-hint','obs-scene','obs-scene-select','obs-phase','obs-pending-reset','twitch-raids-status']) nodes[id] = new Element();
  nodes.fonts.id = 'fonts'; nodes.settings = form; nodes['obs-settings'] = obsForm;
  form.elements.fontFamily = nodes['font-family'];
  for (const key of ['fontSize','speed','textColor','loop','aggregateDonations','moderatorExclusions']) form.elements[key] = new Element();
  form.elements.loop.checked = true; form.elements.aggregateDonations.checked = true;
  for (const key of ['enabled','port','autoStart','resetOnStop','resetDelayMinutes','password','clearPassword']) obsForm.elements[key] = new Element();
  obsForm.elements.sceneName = nodes['obs-scene'];
  let obsFails = obsGetError;
  const globals = {
    document, location:{origin:'http://127.0.0.1:18765'},
    ResizeObserver:class { observe() {} }, setInterval(callback, period) { intervals.set(period, callback); },
    async fetch(path, options) {
      if (path === '/api/state') return {ok:true, json:async () => state};
      if (path === '/api/fonts') { if (fontError) throw new Error('unavailable'); return {ok:true, json:async () => fonts}; }
      if (path === '/api/obs' && options?.method !== 'POST') { if (obsFails) throw new Error('unavailable'); return {ok:true, json:async () => structuredClone(obs)}; }
      const body = JSON.parse(options.body); requests.push({path, body, headers:options.headers});
      const response = handlers[path] ? await handlers[path](body) : {ok:true};
      if (response.ok && path === '/api/settings') state.settings = body.settings;
      if (response.ok && path === '/api/obs') {
        obs.options = body.options;
        if (body.password) obs.hasPassword = true;
        if (body.clearPassword) obs.hasPassword = false;
      }
      return response;
    }
  };
  const previous = Object.fromEntries(Object.keys(globals).map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
  for (const [key, value] of Object.entries(globals)) Object.defineProperty(globalThis, key, {configurable:true, writable:true, value});
  t.after(() => { for (const [key, descriptor] of Object.entries(previous)) { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else delete globalThis[key]; } });
  await import('../src/RutonyCredits/web/control.js?test=' + ++instance);
  await new Promise(setImmediate);
  const flush = () => new Promise(setImmediate);
  return {
    nodes, form, obsForm, requests, state, obs, handlers, document,
    change(name, value, isObs = false) {
      const target = isObs ? obsForm : form, field = target.elements[name];
      if (typeof value === 'boolean') field.checked = value; else field.value = value;
      target.oninput?.({target:field});
    },
    async save() { await form.onsubmit({currentTarget:form, preventDefault() {}}); return requests.findLast(r => r.path === '/api/settings')?.body.settings; },
    async saveObs() { await obsForm.onsubmit({currentTarget:obsForm, preventDefault() {}}); return requests.findLast(r => r.path === '/api/obs')?.body; },
    async poll() { await intervals.get(700)(); await flush(); },
    async pollObs() { await intervals.get(2000)(); await flush(); },
    recoverObs() { obsFails = false; }
  };
}

test('legacy five-section settings add enabled moderators and leave aggregation and loop disabled', async t => {
  const f = await control(t, {legacy:true});
  assert.equal(f.form.elements.fontFamily.value, 'Georgia');
  assert.equal(f.form.elements.loop.checked, false);
  assert.equal(f.form.elements.aggregateDonations.checked, false);
  for (const key of keys) assert.equal(f.form.elements['enabled-' + key].checked, true);
  assert.equal(f.form.elements.moderators.value, 'Модераторы');
  const saved = await f.save();
  assert.equal(saved.loop, false); assert.equal(saved.aggregateDonations, false);
  assert.deepEqual(saved.enabledSections, {'vk-raids':true, 'twitch-raids':true, donations:true, followers:true, subscribers:true, moderators:true});
  assert.equal(f.requests.at(-1).body.sessionId, 'session-one');
});

test('six section flags, loop and donation aggregation are restored and submitted', async t => {
  const f = await control(t, {options:{loop:true, aggregateDonations:true, enabledSections:{'vk-raids':false, 'twitch-raids':true, donations:false, followers:false, subscribers:true, moderators:false}}});
  assert.equal(f.form.elements.loop.checked, true);
  assert.equal(f.form.elements.aggregateDonations.checked, true);
  assert.equal(f.form.elements['enabled-moderators'].checked, false);
  f.change('enabled-followers', true); f.change('loop', false);
  const saved = await f.save();
  assert.deepEqual(saved.enabledSections, {'vk-raids':false, 'twitch-raids':true, donations:false, followers:true, subscribers:true, moderators:false});
  assert.equal(saved.loop, false); assert.equal(saved.aggregateDonations, true);
  assert.equal(f.nodes['playback-mode'].textContent, 'Следующий: один проход');
});

test('failed font loading preserves manual font input and all six sections can be disabled', async t => {
  const f = await control(t, {fontError:true});
  f.change('fontFamily', 'Мой установленный шрифт');
  for (const key of keys) f.change('enabled-' + key, false);
  const saved = await f.save();
  assert.equal(saved.fontFamily, 'Мой установленный шрифт');
  assert.ok(Object.values(saved.enabledSections).every(value => value === false));
  assert.equal(Object.keys(saved.enabledSections).length, 6);
  assert.equal(f.nodes.fonts.hidden, true); assert.match(f.nodes['font-hint'].textContent, /вручную/);
});

test('main dirty outline follows actual values and disappears when an edit is reverted', async t => {
  const f = await control(t);
  assert.equal(f.form.classList.contains('is-dirty'), false);
  f.change('fontFamily', 'Verdana');
  assert.equal(f.form.classList.contains('is-dirty'), true);
  assert.equal(f.nodes.saved.textContent, 'Есть несохранённые изменения');
  f.change('fontFamily', 'Georgia');
  assert.equal(f.form.classList.contains('is-dirty'), false);
  assert.equal(f.nodes.saved.textContent, '');
  f.change('aggregateDonations', true);
  assert.equal(f.form.classList.contains('is-dirty'), true);
  f.change('aggregateDonations', false);
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('state polling does not overwrite edited values or clear main dirty state', async t => {
  const f = await control(t); f.change('fontFamily', 'Local font');
  f.state.settings = {...f.state.settings, fontFamily:'Remote font'};
  await f.poll();
  assert.equal(f.form.elements.fontFamily.value, 'Local font');
  assert.equal(f.form.classList.contains('is-dirty'), true);
});

test('main save failure retains edits and the dirty indicator', async t => {
  const f = await control(t);
  f.handlers['/api/settings'] = () => ({ok:false, json:async () => ({error:'Недоступен диск'})});
  f.change('fontFamily', 'Verdana'); await f.save();
  assert.equal(f.form.elements.fontFamily.value, 'Verdana');
  assert.equal(f.form.classList.contains('is-dirty'), true);
  assert.equal(f.nodes.saved.textContent, 'Есть несохранённые изменения');
  assert.match(f.nodes.notice.textContent, /Недоступен диск/);
});

test('main save acknowledges submitted values without accepting edits made while it was in flight', async t => {
  const f = await control(t); let release;
  f.handlers['/api/settings'] = () => new Promise(resolve => { release = () => resolve({ok:true}); });
  f.change('fontFamily', 'Verdana'); const pending = f.save();
  f.change('fontFamily', 'Arial'); release(); await pending;
  assert.equal(f.form.elements.fontFamily.value, 'Arial');
  assert.equal(f.form.classList.contains('is-dirty'), true);
  assert.equal(f.nodes.saved.textContent, 'Есть несохранённые изменения');
  f.change('fontFamily', 'Verdana');
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('a successful main save clears the outline and records the new baseline', async t => {
  const f = await control(t); f.change('fontFamily', 'Verdana'); await f.save();
  assert.equal(f.form.classList.contains('is-dirty'), false);
  assert.equal(f.nodes.saved.textContent, 'Сохранено');
  f.change('fontFamily', 'Georgia'); assert.equal(f.form.classList.contains('is-dirty'), true);
  f.change('fontFamily', 'Verdana'); assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('unavailable OBS stays hidden and does not interfere with ordinary settings', async t => {
  const f = await control(t, {obsAvailable:false});
  assert.equal(f.nodes['obs-panel'].hidden, true);
  assert.equal(f.nodes['obs-save'].disabled, true);
  assert.equal(f.nodes.save.disabled, false);
});

test('initial OBS GET failure is graceful and a later poll loads its form', async t => {
  const f = await control(t, {obsGetError:true});
  assert.equal(f.nodes.save.disabled, false);
  assert.equal(f.nodes['obs-save'].disabled, true);
  assert.ok(f.nodes['obs-error'].textContent);
  f.recoverObs(); await f.pollObs();
  assert.equal(f.obsForm.elements.port.value, 4455);
  assert.equal(f.nodes['obs-save'].disabled, false);
});

test('OBS scenes use a selection list and polling preserves unsaved manual scene and port edits', async t => {
  const f = await control(t);
  assert.equal(f.obsForm.elements.sceneName.value, 'Final');
  assert.equal(f.obsForm.elements.password.value, '');
  assert.equal(f.nodes['obs-scene-select'].hidden, false);
  f.nodes['obs-scene-select'].value = 'Game'; f.nodes['obs-scene-select'].onchange();
  assert.equal(f.obsForm.elements.sceneName.value, 'Game');
  f.change('port', '4466', true); f.obs.connected = false; f.obs.scenes = [];
  await f.pollObs();
  assert.equal(f.obsForm.elements.port.value, '4466');
  assert.equal(f.obsForm.elements.sceneName.value, 'Game');
  assert.equal(f.nodes['obs-scene'].hidden, false);
  assert.equal(f.nodes['obs-scene-select'].hidden, true);
  assert.equal(f.obsForm.classList.contains('is-dirty'), true);
});

test('OBS dirty state clears on reverting edits and blank password preserves the saved password', async t => {
  const f = await control(t);
  f.change('enabled', true, true); assert.equal(f.obsForm.classList.contains('is-dirty'), true);
  f.change('enabled', false, true); assert.equal(f.obsForm.classList.contains('is-dirty'), false);
  const payload = await f.saveObs();
  assert.deepEqual(payload, {options:{enabled:false, port:4455, sceneName:'Final', autoStart:true, resetOnStop:true, resetDelayMinutes:5}, password:null, clearPassword:false});
  const request = f.requests.at(-1);
  assert.equal(request.headers['X-Credits-Token'], 'control-token');
  assert.equal('sessionId' in payload, false);
});

test('OBS save failure retains a new password without reflecting secret text in status or errors', async t => {
  const f = await control(t);
  f.handlers['/api/obs'] = () => ({ok:false, json:async () => ({error:'typed-secret leaked by server'})});
  f.change('password', 'typed-secret', true); await f.saveObs();
  assert.equal(f.obsForm.elements.password.value, 'typed-secret');
  assert.equal(f.obsForm.classList.contains('is-dirty'), true);
  assert.ok(f.nodes['obs-error'].textContent);
  assert.doesNotMatch(f.nodes['obs-error'].textContent, /typed-secret/);
  assert.doesNotMatch(f.nodes['obs-saved'].textContent, /typed-secret/);
});

test('successful OBS save clears only the submitted password and acknowledged option values', async t => {
  const f = await control(t); let release;
  f.handlers['/api/obs'] = () => new Promise(resolve => { release = () => resolve({ok:true}); });
  f.change('sceneName', 'Submitted scene', true); f.change('password', 'first-secret', true);
  const pending = f.saveObs();
  f.change('sceneName', 'Next scene', true); f.change('password', 'next-secret', true);
  release(); await pending;
  assert.equal(f.obsForm.elements.sceneName.value, 'Next scene');
  assert.equal(f.obsForm.elements.password.value, 'next-secret');
  assert.equal(f.obsForm.classList.contains('is-dirty'), true);
  f.handlers['/api/obs'] = () => ({ok:true}); await f.saveObs();
  assert.equal(f.obsForm.elements.password.value, '');
  assert.equal(f.obsForm.classList.contains('is-dirty'), false);
});

test('explicit OBS password clearing sends a flag and resets the successful action', async t => {
  const f = await control(t); f.change('clearPassword', true, true);
  const payload = await f.saveObs();
  assert.equal(payload.password, null); assert.equal(payload.clearPassword, true);
  assert.equal(f.obsForm.elements.clearPassword.checked, false);
  assert.equal(f.obsForm.classList.contains('is-dirty'), false);
});

test('OBS connection errors remain safe even if a backend message contains a password', async t => {
  const f = await control(t); f.obs.error = 'unsafe secret from adapter';
  await f.pollObs();
  assert.ok(f.nodes['obs-error'].textContent);
  assert.doesNotMatch(f.nodes['obs-error'].textContent, /unsafe secret/);
});

test('OBS bootstrap permits an empty scene and explains that scene selection is still needed', async t => {
  const f = await control(t, {obsOptions:{enabled:true, sceneName:''}});
  assert.match(f.nodes['obs-phase'].textContent, /Выберите сцену титров/);
  const payload = await f.saveObs();
  assert.equal(payload.options.enabled, true);
  assert.equal(payload.options.sceneName, '');
});

test('OBS polling does not replace a focused native scene menu', async t => {
  const f = await control(t);
  f.document.activeElement = f.nodes['obs-scene-select'];
  f.nodes['obs-scene-select'].value = 'Game';
  f.obs.scenes = ['Brand new'];
  await f.pollObs();
  assert.equal(f.nodes['obs-scene-select'].value, 'Game');
  f.nodes['obs-scene-select'].onchange();
  f.document.activeElement = null;
  f.nodes['obs-scene-select'].onblur();
  assert.equal(f.obsForm.elements.sceneName.value, 'Game');
  assert.ok(f.nodes['obs-scene-select'].children.some(option => option.value === 'Brand new'));
  assert.equal(f.nodes['obs-scene-select'].value, 'Game');
});

test('OBS rejects simultaneous replacement and deletion without submitting or revealing the password', async t => {
  const f = await control(t);
  f.change('password', 'typed-secret', true); f.change('clearPassword', true, true);
  await f.saveObs();
  assert.equal(f.requests.filter(request => request.path === '/api/obs').length, 0);
  assert.equal(f.obsForm.elements.password.value, 'typed-secret');
  assert.equal(f.obsForm.classList.contains('is-dirty'), true);
  assert.ok(f.nodes['obs-error'].textContent);
  assert.doesNotMatch(f.nodes['obs-error'].textContent, /typed-secret/);
});

test('legacy settings load empty moderator exclusions and submit an independent empty list', async t => {
  const f = await control(t, {legacy:true});
  assert.equal(f.form.elements.moderatorExclusions.value, '');
  const saved = await f.save();
  assert.deepEqual(saved.moderatorExclusions, []);
  assert.equal(saved.enabledSections.donations, true);
  assert.equal(saved.enabledSections.moderators, true);
});

test('moderator exclusions restore as lines and trim optional @ without splitting spaces or commas', async t => {
  const f = await control(t, {options:{moderatorExclusions:['First Bot', 'Comma,Bot']}});
  assert.equal(f.form.elements.moderatorExclusions.value, 'First Bot\nComma,Bot');
  f.change('moderatorExclusions', '  @First Bot  \r\n\r\n @Name  With Spaces \n Comma,Bot \n');
  const saved = await f.save();
  assert.deepEqual(saved.moderatorExclusions, ['First Bot', 'Name  With Spaces', 'Comma,Bot']);
  assert.equal(saved.headings.donations, 'donations');
  assert.equal(saved.enabledSections.donations, true);
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('moderator exclusion whitespace edits use semantic dirty state and reverting clears it', async t => {
  const f = await control(t, {options:{moderatorExclusions:['First Bot', 'Name With Spaces']}});
  f.change('moderatorExclusions', '  @First Bot \n Name With Spaces \n\n');
  assert.equal(f.form.classList.contains('is-dirty'), false);
  f.change('moderatorExclusions', 'First Bot\nAnother Bot');
  assert.equal(f.form.classList.contains('is-dirty'), true);
  f.change('moderatorExclusions', '@First Bot\nName With Spaces');
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('failed exclusion save and polling preserve raw editor text and dirty state', async t => {
  const f = await control(t);
  f.handlers['/api/settings'] = () => ({ok:false, json:async () => ({error:'Запись не выполнена'})});
  const raw = ' @Test  Bot \n@Comma,Bot ';
  f.change('moderatorExclusions', raw);
  await f.save(); await f.poll();
  assert.equal(f.form.elements.moderatorExclusions.value, raw);
  assert.equal(f.form.classList.contains('is-dirty'), true);
  assert.match(f.nodes.notice.textContent, /Запись не выполнена/);
});

test('exclusion save acknowledges only submitted lines when the editor changes during the request', async t => {
  const f = await control(t); let release;
  f.handlers['/api/settings'] = () => new Promise(resolve => { release = () => resolve({ok:true}); });
  f.change('moderatorExclusions', ' @First Bot ');
  const pending = f.save();
  f.change('moderatorExclusions', '@Second Bot\nThird Bot');
  release(); await pending;
  assert.equal(f.form.elements.moderatorExclusions.value, '@Second Bot\nThird Bot');
  assert.equal(f.form.classList.contains('is-dirty'), true);
  f.change('moderatorExclusions', 'First Bot');
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('more than fifty moderator exclusions are rejected without sending or erasing the editor', async t => {
  const f = await control(t), raw = Array.from({length:51}, (_, i) => 'Example ' + i).join('\n');
  f.change('moderatorExclusions', raw); await f.save();
  assert.equal(f.requests.filter(request => request.path === '/api/settings').length, 0);
  assert.equal(f.form.elements.moderatorExclusions.value, raw);
  assert.equal(f.form.classList.contains('is-dirty'), true);
  assert.match(f.nodes.notice.textContent, /50/);
});

test('moderator nickname limit applies after trimming and removing optional @', async t => {
  const f = await control(t);
  f.change('moderatorExclusions', '@' + 'x'.repeat(201)); await f.save();
  assert.equal(f.requests.filter(request => request.path === '/api/settings').length, 0);
  assert.match(f.nodes.notice.textContent, /200/);
  f.change('moderatorExclusions', ' @' + 'x'.repeat(200) + ' ');
  const saved = await f.save();
  assert.deepEqual(saved.moderatorExclusions, ['x'.repeat(200)]);
});

test('UTF-8 request size prevents a character-valid Cyrillic list from exceeding the HTTP body limit', async t => {
  const f = await control(t);
  const raw = Array.from({length:50}, (_, i) => String(i).padStart(2, '0') + 'я'.repeat(198)).join('\n');
  f.change('moderatorExclusions', raw); await f.save();
  assert.equal(f.requests.filter(request => request.path === '/api/settings').length, 0);
  assert.equal(f.form.elements.moderatorExclusions.value, raw);
  assert.match(f.nodes.notice.textContent, /длин/);
});

test('fifty 200-character ASCII names remain valid even with optional @ on every line', async t => {
  const f = await control(t);
  const names = Array.from({length:50}, (_, i) => String(i).padStart(2, '0') + 'x'.repeat(198));
  f.change('moderatorExclusions', names.map(name => '@' + name).join('\n'));
  const saved = await f.save();
  assert.deepEqual(saved.moderatorExclusions, names);
  assert.equal(f.form.classList.contains('is-dirty'), false);
});

test('known OBS password failures stay specific instead of becoming a generic connection error', async t => {
  const f = await control(t);
  f.obs.connected = false;
  for (const message of ['Не удалось войти в OBS. Проверьте пароль WebSocket.', 'Для подключения к OBS введите пароль WebSocket.', 'OBS не ответил. Проверьте порт и WebSocket-сервер.', 'OBS не завершил подключение вовремя.', 'Ожидается OBS WebSocket версии 5.', 'Не удалось прочитать настройки OBS или защищённый пароль. Сохраните настройки заново.']) {
    f.obs.error = message;
    await f.pollObs();
    assert.equal(f.nodes['obs-error'].textContent, message);
    assert.equal(f.nodes['obs-error'].hidden, false);
  }
});

test('an unknown error extending an approved OBS message cannot reflect a secret', async t => {
  const f = await control(t);
  f.obs.connected = false;
  f.obs.error = 'Не удалось войти в OBS. Проверьте пароль WebSocket. synthetic-private-value';
  await f.pollObs();
  assert.doesNotMatch(f.nodes['obs-error'].textContent, /synthetic-private-value/);
  assert.equal(f.nodes['obs-error'].textContent, 'Не удалось подключиться к OBS. Проверьте порт, пароль и настройки сервера WebSocket в OBS.');
});


test('OBS legacy delay defaults to five, participates in dirty/save and survives edits during polling', async t => {
  const f = await control(t);
  assert.equal(f.obsForm.elements.resetDelayMinutes.value, 5);
  assert.equal((await f.saveObs()).options.resetDelayMinutes, 5);
  f.change('resetDelayMinutes', '12', true);
  assert.equal(f.nodes['obs-saved'].textContent, 'Есть несохранённые изменения');
  await f.pollObs();
  assert.equal(f.obsForm.elements.resetDelayMinutes.value, '12');
  assert.equal((await f.saveObs()).options.resetDelayMinutes, 12);
  assert.equal(f.nodes['obs-saved'].textContent, 'Сохранено');
});

test('OBS delay rejects fractional and out-of-range values without losing input', async t => {
  const f = await control(t);
  for (const value of ['0', '121', '1.5', '', 'NaN']) {
    f.change('resetDelayMinutes', value, true);
    const before = f.requests.length;
    await f.saveObs();
    assert.equal(f.requests.length, before);
    assert.equal(f.obsForm.elements.resetDelayMinutes.value, value);
    assert.match(f.nodes['obs-error'].textContent, /от 1 до 120/);
  }
  for (const value of ['1', '120']) {
    f.change('resetDelayMinutes', value, true);
    assert.equal((await f.saveObs()).options.resetDelayMinutes, Number(value));
  }
});

test('OBS shows transient pending reset and removes it on cancellation; malformed status is harmless', async t => {
  const f = await control(t, {obsOptions:{resetDelayMinutes:17}});
  assert.equal(f.obsForm.elements.resetDelayMinutes.value, 17);
  assert.equal(f.nodes['obs-pending-reset'].hidden, true);
  f.obs.pendingResetUntilUtc = '2026-01-01T12:05:00Z';
  await f.pollObs();
  assert.equal(f.nodes['obs-pending-reset'].hidden, false);
  assert.match(f.nodes['obs-pending-reset'].textContent, /Возобновление трансляции отменит сброс/);
  f.obs.pendingResetUntilUtc = null;
  await f.pollObs();
  assert.equal(f.nodes['obs-pending-reset'].hidden, true);
  f.obs.pendingResetUntilUtc = 'RAW_SERVER_DETAIL';
  await f.pollObs();
  assert.equal(f.nodes['obs-pending-reset'].hidden, true);
  assert.doesNotMatch(f.nodes['obs-pending-reset'].textContent, /RAW_SERVER_DETAIL/);
});

test('Twitch raid source status follows connection and is hidden for unavailable preview', async t => {
  const f = await control(t);
  assert.equal(f.nodes['twitch-raids-status'].hidden, true);
  f.state.hostMode = 'rutony';
  f.state.twitchRaids = {available:true, connected:true, phase:'Подключено', error:null};
  await f.poll();
  assert.equal(f.nodes['twitch-raids-status'].hidden, false);
  assert.equal(f.nodes['twitch-raids-status'].textContent, 'Рейды Twitch: Подключено');
  f.state.twitchRaids = {available:true, connected:false, phase:'Переподключение', error:'Нет связи с Rutony'};
  await f.poll();
  assert.match(f.nodes['twitch-raids-status'].textContent, /Переподключение.*Нет связи с Rutony/u);
  f.state.twitchRaids.available = false;
  await f.poll();
  assert.equal(f.nodes['twitch-raids-status'].hidden, true);
  assert.equal(f.nodes['twitch-raids-status'].textContent, '');
});