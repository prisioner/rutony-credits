export function createFontPicker(input, list) {
  let fonts = [], matches = [], active = -1;
  input.setAttribute('role', 'combobox');
  input.setAttribute('aria-autocomplete', 'list');
  input.setAttribute('aria-controls', list.id);
  input.setAttribute('aria-expanded', 'false');
  list.setAttribute('role', 'listbox');
  const fold = value => value.normalize('NFC').toLocaleLowerCase();

  function close() {
    list.hidden = true; active = -1;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
  }
  function select(name) {
    input.value = name;
    input.dispatchEvent(new Event('input', {bubbles:true}));
    close();
  }
  function render() {
    const query = fold(input.value.trim());
    matches = fonts.filter(name => fold(name).includes(query));
    active = -1; input.removeAttribute('aria-activedescendant');
    list.replaceChildren(...matches.map((name, index) => {
      const option = input.ownerDocument.createElement('div');
      option.id = `${list.id}-${index}`; option.textContent = name;
      option.setAttribute('role', 'option'); option.setAttribute('aria-selected', 'false');
      // Keep focus in the input so selecting a suggestion does not blur it first.
      option.addEventListener('pointerdown', event => { event.preventDefault(); select(name); });
      return option;
    }));
    list.hidden = matches.length === 0;
    input.setAttribute('aria-expanded', String(!list.hidden));
  }
  function highlight(index) {
    active = index;
    for (let i = 0; i < list.children.length; i++) list.children[i].setAttribute('aria-selected', String(i === active));
    const option = list.children[active];
    input.setAttribute('aria-activedescendant', option.id);
    option.scrollIntoView({block:'nearest'});
  }
  input.addEventListener('focus', render);
  input.addEventListener('input', render);
  input.addEventListener('blur', close);
  input.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (list.hidden) render();
      if (!matches.length) return;
      event.preventDefault();
      highlight(event.key === 'ArrowDown' ? (active + 1) % matches.length : active <= 0 ? matches.length - 1 : active - 1);
    } else if (event.key === 'Enter' && !list.hidden && active >= 0) {
      event.preventDefault(); select(matches[active]);
    } else if (event.key === 'Tab') {
      if (!list.hidden && active >= 0) select(matches[active]);
      else close();
    } else if (event.key === 'Escape' && !list.hidden) {
      event.preventDefault(); close();
    }
  });
  close();
  return {setFonts(names) {
    fonts = Array.isArray(names) ? names.filter(name => typeof name === 'string' && name.trim()) : [];
    if (input.ownerDocument.activeElement === input) render();
    else close();
  }};
}
