import {frame} from './playback.js';
import {loadLocalFont} from './local-font.js';
const stage = document.getElementById('stage'), content = document.getElementById('credits');
if (new URLSearchParams(location.search).get('preview') === '1') document.body.style.background = '#131827';
let current = null, baseElapsed = 0, baseClock = 0, height = 0, loading = false, finishing = false, completed = null, lastAttempt = 0, generation = 0, pending = null, activeFont = null;
function size() {
  const scale = Math.min(innerWidth / 1920, innerHeight / 1080);
  stage.style.transform = `translate(${(innerWidth - 1920 * scale) / 2}px, ${(innerHeight - 1080 * scale) / 2}px) scale(${scale})`;
}
size(); addEventListener('resize', size);
function clear() {
  generation++; pending = null; current = null;
  activeFont?.dispose(); activeFont = null;
  content.style.visibility = 'hidden'; content.replaceChildren();
}
// State polling continues while font metadata/local loading is pending. Each
// preparation owns a generation so Stop or a replacement cannot be undone.
async function prepare(state, stateClock, ticket) {
  let font;
  try {
    const snapshot = await fetch('/api/runs/' + encodeURIComponent(state.runId), {cache:'no-store'});
    if (!snapshot.ok) throw new Error();
    const run = await snapshot.json();
    if (run.runId !== state.runId) throw new Error();
    if (ticket !== generation) return;
    font = await loadLocalFont(run.settings.fontFamily);
    if (ticket !== generation) { font.dispose(); return; }
    content.style.fontFamily = font.family;
    content.style.fontSize = run.settings.fontSize + 'px'; content.style.color = run.settings.textColor;
    for (const group of run.sections) {
      const section = document.createElement('section'), heading = document.createElement('h2');
      heading.textContent = group.heading; section.append(heading);
      for (const name of group.names) { const line = document.createElement('p'); line.textContent = name; section.append(line); }
      content.append(section);
    }
    height = content.offsetHeight;
    // The server timestamp belongs to the state observation, before snapshot
    // and font loading. Keep its local clock so all waiting remains elapsed.
    baseElapsed = (Date.parse(state.serverTimeUtc) - Date.parse(run.startedAtUtc)) / 1000;
    baseClock = stateClock; activeFont = font; current = run; pending = null; finishing = false;
    // animate() computes the current phase before revealing content.
  } catch {
    font?.dispose();
    if (ticket === generation) clear();
  }
}
async function poll() {
  if (loading) return; loading = true;
  try {
    const response = await fetch('/api/state', {cache:'no-store'});
    if (!response.ok) throw new Error();
    const state = await response.json(), stateClock = performance.now();
    if (state.playback !== 'running' || !state.runId) { clear(); return; }
    if (current?.runId === state.runId || pending === state.runId) return;
    clear(); pending = state.runId;
    void prepare(state, stateClock, generation);
  } catch { clear(); }
  finally { loading = false; }
}
async function complete(run) {
  if (finishing || completed === run.runId || performance.now() - lastAttempt < 1000) return;
  finishing = true; lastAttempt = performance.now();
  try { const result = await fetch(`/api/runs/${encodeURIComponent(run.runId)}/complete`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:run.completionToken})}); if (result.ok || result.status === 409) completed = run.runId; }
  catch { /* A later poll/reload can retry; never replay from zero. */ }
  finally { finishing = false; }
}
function animate() {
  if (current) {
    const f = frame(baseElapsed + (performance.now() - baseClock) / 1000, height, current.settings.speed, current.settings.loop === true);
    content.style.transform = `translateY(${f.y}px)`;
    content.style.visibility = f.finished ? 'hidden' : 'visible';
    if (f.finished && current.settings.loop !== true) complete(current);
  }
  requestAnimationFrame(animate);
}
animate(); poll(); setInterval(poll, 500);
