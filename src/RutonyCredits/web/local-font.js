// CSS strings are not JSON strings: control characters require CSS hex escapes.
export function cssString(value) {
  return '"' + String(value).replace(/["\\\x00-\x1f\x7f]/g, character => {
    if (character === '"' || character === '\\') return '\\' + character;
    return '\\' + (character.charCodeAt(0) || 0xfffd).toString(16) + ' ';
  }) + '"';
}

let sequence = 0;
export async function loadLocalFont(family, {
  fetch:request = globalThis.fetch, FontFace:Face = globalThis.FontFace,
  fonts = globalThis.document?.fonts, timeout = 1200
} = {}) {
  const fallback = {family:cssString(family), dispose() {}};
  const controller = typeof AbortController === 'function' ? new AbortController() : null;
  let expired = false, timer;
  const loaded = [];
  const dispose = () => { for (const face of loaded) fonts.delete(face); loaded.length = 0; };
  try {
    return await Promise.race([
      (async () => {
        if (typeof Face !== 'function' || typeof fonts?.add !== 'function') return fallback;
        const response = await request('/api/font-faces?family=' + encodeURIComponent(family), {cache:'no-store', ...controller ? {signal:controller.signal} : {}});
        if (!response.ok) return fallback;
        const faces = await response.json();
        if (!Array.isArray(faces) || faces.length < 1 || faces.length > 2
          || faces.some(f => typeof f?.localName !== 'string' || !f.localName.trim() || f.localName.length > 64 || /[\x00-\x1f\x7f-\x9f]/.test(f.localName))
          || faces[0].weight !== 400
          || (faces.length === 2 && (faces[1].weight !== 700 || faces[0].localName.toLowerCase() === faces[1].localName.toLowerCase()))) return fallback;
        const alias = '__rutony_local_' + ++sequence;
        const result = await Promise.all(faces.map(f => new Face(alias, 'local(' + cssString(f.localName) + ')', {weight:String(f.weight),style:'normal'}).load()));
        if (expired) return fallback;
        for (const face of result) { fonts.add(face); loaded.push(face); }
        return {family:cssString(alias), dispose};
      })(),
      new Promise(resolve => { timer = setTimeout(() => { expired = true; controller?.abort(); resolve(fallback); }, timeout); })
    ]);
  } catch { dispose(); return fallback; }
  finally { clearTimeout(timer); }
}
