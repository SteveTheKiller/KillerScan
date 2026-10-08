const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');
const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'scan-landing/help.html'), 'utf8');
for (const match of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
const generated = fs.readFileSync(path.join(root, 'scan-landing/shortcuts.generated.js'), 'utf8');
const body = { children: [], replaceChildren() { this.children = []; }, appendChild(child) { this.children.push(child); } };
const context = { window: {}, document: { documentElement: { getAttribute() { return context.language; } },
  getElementById(id) { return id === 'shortcutCatalogBody' ? body : null; },
  createElement() { return { children: [], appendChild(child) { this.children.push(child); } }; } }, language: 'en' };
vm.createContext(context);
vm.runInContext(generated, context);
context.KS_SHORTCUTS = context.window.KS_SHORTCUTS;
const start = html.indexOf('var KBL = ');
const end = html.indexOf('var KBROWS = ', start);
vm.runInContext(html.slice(start, end), context);
vm.runInContext(html.slice(end, html.indexOf('var MODS = ', end)), context);
const physicalKeys = new Set(context.KBROWS.flat().map(key => key[0]));
const bindings = Object.values(context.KBL).flatMap(layer => Object.entries(layer));
assert.equal(bindings.reduce((count, entry) => count + entry[1][1].length, 0), context.KS_SHORTCUTS.length);
for (const [key] of bindings) assert(physicalKeys.has(key), `Website map is missing ${key}`);
for (const [site, app] of Object.entries({en:'en-US',es:'es',pt:'pt-BR',fr:'fr-FR',de:'de-DE',nb:'nb-NO',uk:'uk-UA',ru:'ru-RU',it:'it-IT',cs:'cs-CZ',pl:'pl-PL',hu:'hu-HU',tr:'tr-TR',kk:'kk-KZ',bn:'bn',ja:'ja-JP','zh-Hans':'zh-CN','zh-Hant':'zh-TW',vi:'vi-VN'})) {
  context.language = site;
  context.paintCatalog();
  assert.equal(context.shortcutLocale(), app);
  assert.equal(body.children.length, context.KS_SHORTCUTS.length);
  body.children.forEach((row, index) => {
    assert.equal(row.children[0].textContent, context.KS_SHORTCUTS[index].gesture);
    assert.equal(row.children[1].textContent, context.KS_SHORTCUTS[index].labels[app]);
  });
}
assert(context.KBL.ctrlshift.Y[1].length === 3, 'Services, history and terminal share their scoped chord in the map.');
assert(context.KBL.ctrlshift.G && context.KBL.ctrlalt.H && context.KBL.base.Menu && context.KBL.base.Delete);
console.log(`PASS: website scripts parse, all ${context.KS_SHORTCUTS.length} bindings reach list/map, and 19 locale mappings display their app labels.`);
