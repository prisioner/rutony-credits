import test from 'node:test';
import assert from 'node:assert/strict';
import {createFontPicker} from '../src/RutonyCredits/web/font-picker.js';

// Node has no DOM. This fixture supplies the element boundary; filtering,
// keyboard decisions, value changes and ARIA state run in the real picker.
class Element extends EventTarget {
  constructor(document) { super(); this.ownerDocument = document; this.attributes = {}; this.children = []; this.value = ''; this.hidden = true; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  removeAttribute(name) { delete this.attributes[name]; }
  replaceChildren(...children) { this.children = children; }
  scrollIntoView() {}
}
function fixture(fonts = ['Arial', 'Arial Black', 'Georgia', 'Шрифт Один', 'Шрифт Два']) {
  const document = {createElement() { return new Element(document); }, activeElement:null};
  const input = new Element(document), list = new Element(document); list.id = 'fonts';
  const picker = createFontPicker(input, list);
  picker.setFonts(fonts);
  function type(value) { input.value = value; input.dispatchEvent(new Event('input', {bubbles:true})); }
  function key(key) { const event = Object.assign(new Event('keydown', {cancelable:true}), {key}); input.dispatchEvent(event); return event; }
  function focus() { document.activeElement = input; input.dispatchEvent(new Event('focus')); }
  return {input, list, picker, type, key, focus};
}

test('font suggestions match substrings without case sensitivity, including Cyrillic', () => {
  const f = fixture(); f.focus(); f.type('РИФТ Д');
  assert.deepEqual(f.list.children.map(option => option.textContent), ['Шрифт Два']);
  assert.equal(f.input.getAttribute('role'), 'combobox');
  assert.equal(f.list.getAttribute('role'), 'listbox');
  assert.equal(f.input.getAttribute('aria-expanded'), 'true');
  f.type('rial');
  assert.deepEqual(f.list.children.map(option => option.textContent), ['Arial', 'Arial Black']);
});

test('arrows select suggestions and Enter commits a value without submitting the form', () => {
  const f = fixture(); f.focus(); f.type('ari');
  assert.equal(f.key('ArrowDown').defaultPrevented, true);
  f.key('ArrowDown');
  assert.equal(f.list.children[1].getAttribute('aria-selected'), 'true');
  assert.equal(f.input.getAttribute('aria-activedescendant'), f.list.children[1].id);
  assert.equal(f.key('Enter').defaultPrevented, true);
  assert.equal(f.input.value, 'Arial Black');
  assert.equal(f.list.hidden, true);
  assert.equal(f.input.getAttribute('aria-expanded'), 'false');
});

test('Escape keeps free input and Tab accepts only an explicitly highlighted suggestion', () => {
  const f = fixture(); f.focus(); f.type('ari'); f.key('ArrowDown'); f.key('Escape');
  assert.equal(f.input.value, 'ari');
  assert.equal(f.list.hidden, true);
  f.type('geo');
  assert.equal(f.key('Tab').defaultPrevented, false);
  assert.equal(f.input.value, 'geo');
  f.focus(); f.key('ArrowUp');
  assert.equal(f.key('Tab').defaultPrevented, false);
  assert.equal(f.input.value, 'Georgia');
  assert.equal(f.list.hidden, true);
});

test('missing fonts and unmatched names preserve manual input and native form keys', () => {
  const f = fixture([]); f.focus(); f.type('Мой шрифт');
  assert.equal(f.input.value, 'Мой шрифт');
  assert.equal(f.list.hidden, true);
  assert.equal(f.key('ArrowDown').defaultPrevented, false);
  assert.equal(f.key('Enter').defaultPrevented, false);
  f.picker.setFonts(['Arial']);
  assert.equal(f.input.value, 'Мой шрифт');
  assert.equal(f.list.hidden, true);
  f.type('ari');
  assert.deepEqual(f.list.children.map(option => option.textContent), ['Arial']);
});

test('pointer selection commits the chosen font and notifies the settings form', () => {
  const f = fixture(); let changed = '';
  f.input.addEventListener('input', () => { changed = f.input.value; });
  f.focus(); f.type('ari');
  const pointer = new Event('pointerdown', {cancelable:true});
  f.list.children[1].dispatchEvent(pointer);
  assert.equal(pointer.defaultPrevented, true);
  assert.equal(f.input.value, 'Arial Black');
  assert.equal(changed, 'Arial Black');
  assert.equal(f.list.hidden, true);
});
