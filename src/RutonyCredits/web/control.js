import {createFontPicker} from './font-picker.js';
const $ = id => document.getElementById(id);
const token = document.querySelector('meta[name="credits-token"]').content;
const labels = {'vk-raids':'Рейды VK Video Live','twitch-raids':'Рейды Twitch',donations:'Донаты',followers:'Фолловеры',subscribers:'Подписчики',moderators:'Модераторы'};
const defaultHeadings = {...labels, donations:'Спасибо за поддержку', followers:'Новые фолловеры', subscribers:'Новые подписчики'};
const settingsForm = $('settings'), obsForm = $('obs-settings');
let state, busy = false, initialized = false, online = false;
let settingsBaseline = null, settingsSaved = false;
let obsAvailable = false, obsInitialized = false, obsLoading = false, obsSaving = false;
let obsBaseline = null, obsSaved = false, obsStatus = null, obsSaveError = '', sceneSignature = '';
for (const [key, label] of Object.entries(labels)) {
  const row = document.createElement('div'); row.className = 'count';
  const text = document.createElement('span'); text.textContent = label;
  const number = document.createElement('strong'); number.id = 'count-' + key; number.textContent = '—';
  row.append(text, number); $('counts').append(row);
  const field = document.createElement('div'); field.className = 'section-setting';
  const toggle = document.createElement('label'); toggle.className = 'check-label';
  const enabled = document.createElement('input'); enabled.type = 'checkbox'; enabled.name = 'enabled-' + key; enabled.checked = true;
  const caption = document.createElement('span'); caption.textContent = label;
  toggle.append(enabled, caption);
  const input = document.createElement('input'); input.name = key; input.required = true; input.maxLength = 120;
  input.setAttribute('aria-label', 'Заголовок: ' + label);
  field.append(toggle, input); $('headings').append(field);
}
const fontPicker = createFontPicker($('font-family'), $('fonts'));
async function loadFonts() {
  try {
    const response = await fetch('/api/fonts', {cache:'no-store'});
    if (!response.ok) throw new Error();
    const fonts = await response.json();
    if (!Array.isArray(fonts)) throw new Error();
    fontPicker.setFonts(fonts);
    $('font-hint').textContent = fonts.length ? 'Начните вводить название и выберите шрифт. Можно ввести название вручную.' : 'Список шрифтов недоступен. Введите название установленного шрифта вручную.';
  } catch {
    fontPicker.setFonts([]);
    $('font-hint').textContent = 'Список шрифтов недоступен. Введите название установленного шрифта вручную.';
  }
}
function readModeratorExclusions(value) {
  return value.split(/\r\n?|\n/).map(name => name.trim().replace(/^@/, '').trim()).filter(Boolean);
}
function settingsError(settings) {
  if (settings.moderatorExclusions.length > 50) return 'Укажите не больше 50 ников в исключениях модераторов.';
  if (settings.moderatorExclusions.some(name => name.length > 200)) return 'Ник в исключениях модераторов не должен быть длиннее 200 знаков.';
  const body = JSON.stringify({sessionId:state.sessionId, runId:state.runId, settings});
  if (new TextEncoder().encode(body).length > 16 * 1024) return 'Настройки слишком длинные. Сократите список исключений или заголовки.';
  return '';
}
function readSettings() {
  const fields = settingsForm.elements;
  const settings = {fontFamily:fields.fontFamily.value, fontSize:Number(fields.fontSize.value), speed:Number(fields.speed.value), textColor:fields.textColor.value, loop:fields.loop.checked, aggregateDonations:fields.aggregateDonations.checked, moderatorExclusions:readModeratorExclusions(fields.moderatorExclusions.value), headings:{}, enabledSections:{}};
  for (const key of Object.keys(labels)) {
    settings.headings[key] = fields[key].value;
    settings.enabledSections[key] = fields['enabled-' + key].checked;
  }
  return settings;
}
function dirtyIndicator(form, status, dirty, saved) {
  form.classList.toggle('is-dirty', dirty);
  status.classList.toggle('unsaved', dirty);
  status.textContent = dirty ? 'Есть несохранённые изменения' : saved ? 'Сохранено' : '';
}
function updateSettingsDirty() {
  dirtyIndicator(settingsForm, $('saved'), settingsBaseline !== null && JSON.stringify(readSettings()) !== settingsBaseline, settingsSaved);
}
$('obs-url').value = location.origin + '/overlay';
function buttons() {
  for (const id of ['run','preview','stop','reset','save']) $(id).disabled = busy || !online;
  $('run').disabled ||= Boolean(state?.error);
}
function notice(message) { $('notice').textContent = message || ''; $('notice').hidden = !message; }
async function refresh() {
  try {
    const response = await fetch('/api/state', {cache:'no-store'});
    if (!response.ok) throw new Error();
    state = await response.json(); online = true;
    $('connection').textContent = state.hostMode === 'standalone' ? 'Тестовые данные' : 'Подключено';
    $('connection').className = 'badge online';
    for (const key of Object.keys(labels)) $('count-' + key).textContent = state.counts[key] || 0;
    $('period').textContent = 'Период ' + state.sessionId.slice(0, 8) + ' · всего событий: ' + state.eventCount;
    const raidStatus = $('twitch-raids-status'), raids = state.twitchRaids;
    if (raidStatus) {
      raidStatus.hidden = raids?.available !== true;
      raidStatus.textContent = raids?.available === true ? 'Рейды Twitch: ' + raids.phase + (raids.error ? ' · ' + raids.error : '') : '';
    }
    $('playback').textContent = state.playback === 'running' ? (state.mode === 'preview' ? 'Сейчас идёт тестовый показ' : 'Титры в эфире') : state.playback === 'finished' ? 'Показ завершён. Экран пуст.' : 'Готовы к финалу стрима';
    $('playback-mode').textContent = state.settings.loop === true ? 'Следующий: по кругу' : 'Следующий: один проход';
    if (state.error) notice(state.error);
    if (!initialized) {
      const fields = settingsForm.elements;
      for (const key of ['fontFamily','fontSize','speed','textColor']) fields[key].value = state.settings[key];
      fields.loop.checked = state.settings.loop === true;
      fields.aggregateDonations.checked = state.settings.aggregateDonations === true;
      fields.moderatorExclusions.value = Array.isArray(state.settings.moderatorExclusions) ? state.settings.moderatorExclusions.join('\n') : '';
      for (const key of Object.keys(labels)) {
        fields[key].value = state.settings.headings?.[key] ?? defaultHeadings[key];
        fields['enabled-' + key].checked = state.settings.enabledSections?.[key] !== false;
      }
      settingsBaseline = JSON.stringify(readSettings());
      initialized = true; updateSettingsDirty();
    }
  } catch {
    online = false; $('connection').textContent = 'Нет связи со скриптом'; $('connection').className = 'badge offline';
    if ($('twitch-raids-status')) { $('twitch-raids-status').hidden = true; $('twitch-raids-status').textContent = ''; }
    $('playback').textContent = 'Проверьте, включён ли скрипт в RutonyChat';
  }
  buttons();
}
async function command(path, extra = {}) {
  if (busy || !online) return;
  busy = true; buttons(); notice('');
  try {
    const response = await fetch(path, {method:'POST', headers:{'Content-Type':'application/json','X-Credits-Token':token}, body:JSON.stringify({sessionId:state.sessionId,runId:state.runId,...extra})});
    if (!response.ok) {
      let message = 'Команда не выполнена. Обновите страницу и повторите.';
      try { message = (await response.json()).error || message; } catch {}
      throw new Error(message);
    }
  } catch (e) { notice(e.message); return false; }
  finally { busy = false; await refresh(); }
  return true;
}
$('run').onclick = () => command('/api/run');
$('preview').onclick = () => command('/api/preview');
$('stop').onclick = () => command('/api/stop');
$('reset').onclick = () => { if (confirm('Сбросить счётчики? Показ остановится, текущий список очистится. Прежний журнал сохранится на диске.')) command('/api/reset'); };
settingsForm.onsubmit = async event => {
  event.preventDefault();
  if (busy || !online) return;
  const settings = readSettings();
  const validationMessage = settingsError(settings);
  if (validationMessage) { notice(validationMessage); updateSettingsDirty(); return; }
  if (await command('/api/settings', {settings})) {
    // Acknowledge the submitted snapshot; the form may have changed meanwhile.
    settingsBaseline = JSON.stringify(settings); settingsSaved = true;
  }
  updateSettingsDirty();
};
settingsForm.oninput = settingsForm.onchange = updateSettingsDirty;

function readObsOptions() {
  const fields = obsForm.elements;
  return {enabled:fields.enabled.checked, port:Number(fields.port.value), sceneName:fields.sceneName.value, autoStart:fields.autoStart.checked, resetOnStop:fields.resetOnStop.checked, resetDelayMinutes:Number(fields.resetDelayMinutes.value)};
}
function updateObsDirty() {
  const fields = obsForm.elements;
  const dirty = obsBaseline !== null && (JSON.stringify(readObsOptions()) !== obsBaseline || fields.password.value !== '' || fields.clearPassword.checked);
  dirtyIndicator(obsForm, $('obs-saved'), dirty, obsSaved);
}
function obsButtons() { $('obs-save').disabled = obsSaving || !obsAvailable || !obsInitialized; }
const publicObsErrors = new Set([
  'Не удалось войти в OBS. Проверьте пароль WebSocket.',
  'Для подключения к OBS введите пароль WebSocket.',
  'OBS не ответил. Проверьте порт и WebSocket-сервер.',
  'OBS не завершил подключение вовремя.',
  'Ожидается OBS WebSocket версии 5.',
  'Не удалось подтвердить остановку OBS или выполнить сброс. Статистика сохранена; проверьте текущий период.',
  'Не удалось прочитать настройки OBS или защищённый пароль. Сохраните настройки заново.'
]);
function obsError() {
  const error = obsStatus?.error;
  const message = obsSaveError || (error ? publicObsErrors.has(error) ? error : 'Не удалось подключиться к OBS. Проверьте порт, пароль и настройки сервера WebSocket в OBS.' : '');
  $('obs-error').textContent = message; $('obs-error').hidden = !message;
}
function renderObsScenes() {
  if (!obsStatus) return;
  const input = obsForm.elements.sceneName, select = $('obs-scene-select');
  // Replacing options while its native menu is in use would interrupt selection.
  if (document.activeElement === select) return;
  const names = [...new Set((obsStatus.scenes || []).filter(name => typeof name === 'string' && name))];
  const useList = obsStatus.connected === true && names.length > 0;
  input.hidden = useList; select.hidden = !useList;
  const signature = JSON.stringify({names, value:input.value});
  if (sceneSignature === signature) return;
  sceneSignature = signature;
  const values = ['', ...names];
  if (input.value && !names.includes(input.value)) values.push(input.value);
  select.replaceChildren(...values.map(name => {
    const option = document.createElement('option'); option.value = name; option.textContent = name || 'Выберите сцену титров'; return option;
  }));
  select.value = input.value;
}
async function refreshObs() {
  if (obsLoading) return;
  obsLoading = true;
  try {
    const response = await fetch('/api/obs', {cache:'no-store'});
    if (!response.ok) throw new Error();
    const status = await response.json();
    obsAvailable = status.available === true;
    $('obs-panel').hidden = !obsAvailable;
    if (!obsAvailable) return;
    obsStatus = status; obsForm.hidden = false;
    if (!obsInitialized) {
      const fields = obsForm.elements, options = status.options;
      for (const key of ['enabled','autoStart','resetOnStop']) fields[key].checked = options[key] === true;
      fields.port.value = options.port; fields.sceneName.value = options.sceneName;
      fields.resetDelayMinutes.value = options.resetDelayMinutes ?? 5;
      fields.password.value = ''; fields.clearPassword.checked = false;
      obsBaseline = JSON.stringify(readObsOptions()); obsInitialized = true;
      updateObsDirty();
    }
    $('obs-connection').textContent = status.connected ? 'Подключено к OBS' : !status.options.enabled ? 'Подключение выключено' : 'Нет связи с OBS';
    $('obs-connection').className = 'badge ' + (status.connected ? 'online' : 'offline');
    $('obs-phase').textContent = status.connected && !status.options.sceneName ? 'Выберите сцену титров и сохраните настройки для автозапуска.' : '';
    const deadline = status.pendingResetUntilUtc ? new Date(status.pendingResetUntilUtc) : null;
    const pending = deadline && Number.isFinite(deadline.getTime());
    $('obs-pending-reset').hidden = !pending;
    $('obs-pending-reset').textContent = pending ? 'Сброс счётчиков после ' + deadline.toLocaleTimeString('ru-RU') + '. Возобновление трансляции отменит сброс.' : '';
    $('obs-password-hint').textContent = status.hasPassword ? 'Пароль сохранён. Пустое поле оставляет его без изменений.' : 'Пароль не задан. Введите его, если он включён в OBS.';
    renderObsScenes(); obsError();
  } catch {
    $('obs-panel').hidden = false;
    obsForm.hidden = !obsInitialized;
    $('obs-connection').textContent = 'Нет связи';
    $('obs-connection').className = 'badge offline';
    $('obs-error').textContent = 'Не удалось получить состояние OBS. Повторим подключение.';
    $('obs-error').hidden = false;
  } finally { obsLoading = false; obsButtons(); }
}
$('obs-scene-select').onchange = () => {
  obsForm.elements.sceneName.value = $('obs-scene-select').value;
  updateObsDirty();
};
$('obs-scene-select').onblur = renderObsScenes;
obsForm.oninput = obsForm.onchange = updateObsDirty;
obsForm.onsubmit = async event => {
  event.preventDefault();
  if (obsSaving || !obsAvailable || !obsInitialized) return;
  const options = readObsOptions(), fields = obsForm.elements;
  const secret = fields.password.value, clearPassword = fields.clearPassword.checked;
  obsSaveError = '';
  if (!Number.isInteger(options.resetDelayMinutes) || options.resetDelayMinutes < 1 || options.resetDelayMinutes > 120) {
    obsSaveError = 'Задержка сброса OBS должна быть от 1 до 120 минут.'; obsError(); return;
  }
  if (secret && clearPassword) {
    obsSaveError = 'Введите новый пароль или выберите удаление сохранённого пароля.'; obsError(); return;
  }
  obsSaving = true; obsButtons(); obsError();
  try {
    const response = await fetch('/api/obs', {method:'POST', headers:{'Content-Type':'application/json','X-Credits-Token':token}, body:JSON.stringify({options, password:secret || null, clearPassword})});
    if (!response.ok) throw new Error();
    obsBaseline = JSON.stringify(options); obsSaved = true;
    // Do not erase a new secret or an action entered after this request began.
    if (fields.password.value === secret) fields.password.value = '';
    if (fields.clearPassword.checked === clearPassword) fields.clearPassword.checked = false;
  } catch {
    // Never reflect adapter errors or a response body that might contain secrets.
    obsSaveError = 'Не удалось сохранить настройки OBS. Проверьте порт и повторите.';
  } finally {
    obsSaving = false; updateObsDirty(); obsButtons(); await refreshObs();
  }
};
$('copy').onclick = async () => {
  try { await navigator.clipboard.writeText($('obs-url').value); $('copy').textContent = 'Скопировано'; }
  catch { $('obs-url').select(); $('copy').textContent = 'Нажмите Ctrl+C'; }
};
new ResizeObserver(() => { document.querySelector('iframe').style.transform = 'scale(' + $('preview-frame').clientWidth / 1920 + ')'; }).observe($('preview-frame'));
loadFonts();
await Promise.all([refresh(), refreshObs()]);
setInterval(() => { if (!busy) return refresh(); }, 700);
setInterval(refreshObs, 2000);
