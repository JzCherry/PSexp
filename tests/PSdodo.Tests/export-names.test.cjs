const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../../PSdodo/Resources/Scripts');
const context = vm.createContext({});
new vm.Script(fs.readFileSync(path.join(root, 'export/export_names.psjs'), 'utf8')).runInContext(context);
for (const [input, expected] of [
    ['中文👩‍💻', '中文👩‍💻'], ['图\r\n\t层\u0085\u2028\ufffd\ud800', '图___层____'],
    ['CON', '_CON'], ['nul.png', '_nul.png'], ['LPT¹', '_LPT¹'], ['...', '未命名'], ['a/b:*?"<>|', 'a_b_______']
]) assert.equal(context.psxSafeName(input), expected);

context.psxCheckNativeNames({ name: '正常😀.psd' }, [{ id: 1, name: '画板👩‍💻', layers: [{ name: '\r' }] }]);
assert.throws(() => context.psxCheckNativeNames({ name: '设计.psd' }, [{ id: 7, name: '画板\r\n' }]), /ID=7.*U\+000D.*U\+000A/);
assert.throws(() => context.psxCheckNativeNames({ name: '坏\ud800.psd' }, []), /U\+D800/);

// Run the actual ExtendScript JSON encoder in isolation: preserve names for display,
// including controls and lone UTF-16 surrogates, without corrupting the JSON protocol.
for (const file of ['document_info.jsx', 'list_artboards.jsx']) {
    const source = fs.readFileSync(path.join(root, 'probe', file), 'utf8');
    const encoder = source.slice(source.indexOf('    function jsonStr'), source.indexOf('\n    var ', source.indexOf('    function jsonStr')));
    const scope = vm.createContext({});
    new vm.Script(encoder).runInContext(scope);
    const name = '中文\r\n\t\0\b\f\u0085\u2028\ud800😀"\\';
    assert.equal(JSON.parse(scope.jsonStr(name)), name);
}
console.log('PASS shared filename rules, native preflight, and JSX JSON name round-trip');
