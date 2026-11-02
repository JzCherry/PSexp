import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../PSdodo/Resources/Scripts/probe/document_events.jsx', import.meta.url), 'utf8');
const own = 'C:/PSexp test/notifier.jsx';
const other = 'C:/another tool/notifier.jsx';
function setup(enabled, foreign = false) {
  const app = { notifiersEnabled: enabled, notifiers: [] };
  function add(event, file, eventClass) {
    const item = { event, eventFile: file, eventClass, remove() { app.notifiers.splice(app.notifiers.indexOf(item), 1); } };
    app.notifiers.push(item);
    return item;
  }
  app.notifiers.add = add;
  if (foreign) add('Opn ', { fsName: other });
  return app;
}
function run(app, remove = false, restore = false) {
  const script = source.replaceAll('{{HANDLER_PATH}}', own)
    .replaceAll('{{REMOVE_ONLY}}', String(remove)).replaceAll('{{RESTORE_DISABLED}}', String(restore));
  const context = { app, File: function (path) { return { fsName: path, exists: true }; } };
  vm.runInNewContext(script, context);
  return JSON.parse(context.__psx_result);
}
function check(name, fn) { fn(); console.log('PASS ' + name); }

check('disabled foreign scripts stay disabled and registered', () => {
  const app = setup(false, true);
  assert.equal(run(app).success, false);
  assert.equal(app.notifiersEnabled, false);
  assert.equal(app.notifiers.length, 1);
  assert.equal(app.notifiers[0].eventFile.fsName, other);
});
check('own registration restores an originally disabled empty configuration', () => {
  const app = setup(false);
  const installed = run(app);
  assert.equal(installed.success, true);
  assert.equal(installed.enabledByPSexp, true);
  assert.equal(app.notifiersEnabled, true);
  assert.equal(run(app, true, true).success, true);
  assert.equal(app.notifiersEnabled, false);
  assert.equal(app.notifiers.length, 0);
});
check('already enabled foreign callbacks survive registration and removal', () => {
  const app = setup(true, true);
  const foreign = app.notifiers[0];
  assert.equal(run(app).enabledByPSexp, false);
  assert.equal(run(app, true).success, true);
  assert.equal(app.notifiers.length, 1);
  assert.equal(app.notifiers[0], foreign);
  assert.equal(app.notifiersEnabled, true);
});
check('repeated registration does not accumulate duplicate callbacks', () => {
  const app = setup(false);
  const count = run(app).registered;
  assert.equal(run(app, false, true).registered, count);
  assert.equal(app.notifiers.length, count);
});
check('new foreign registrations prevent disabling the global event switch', () => {
  const app = setup(false);
  run(app);
  app.notifiers.add('save', { fsName: other });
  run(app, true, true);
  assert.equal(app.notifiers.length, 1);
  assert.equal(app.notifiersEnabled, true);
});
