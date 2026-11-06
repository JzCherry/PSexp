const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.join(__dirname, '../../PSdodo/Resources/Scripts/export');
const wrapper = fs.readFileSync(path.join(root, 'receipt_task.psjs'), 'utf8');
const names = fs.readFileSync(path.join(root, 'export_names.psjs'), 'utf8');
(async () => {
    let count = 0;
    for (const mode of ['quick_export', 'all_artboards', 'selected_artboards']) {
        for (const scenario of ['success', 'no document', 'action error', 'throw', 'no output', 'unsafe document', 'unsafe artboard',
            ...(mode === 'quick_export' ? ['delayed save'] : [])]) {
            const reports = [];
            const args = { EXPORT_PATH: 'C:/output/', FORMAT: 'png', QUALITY: '32', RENAME: 'true' };
            const body = names + '\n' + fs.readFileSync(path.join(root, mode + '.psjs'), 'utf8').replace(/\{\{([A-Z_]+)\}\}/g, (_, key) => args[key]);
            const background = mode !== 'quick_export';
            const script = wrapper.replace('{{SCRIPT_BODY}}', body)
                .replace('{{RESULT_FOLDER_URL}}', 'file:/task').replace('{{EXPORT_FOLDER_URL}}', 'file:/output')
                .replace('{{WAIT_FOR_BACKGROUND_EXPORT}}', String(background))
                .replace('{{EXPORT_FORMAT}}', 'png').replace('{{EXPECTED_FILE_COUNT}}', '0');
            let submitted = false;
            let saveStarted = false;
            let releaseSave;
            const saveGate = new Promise(resolve => { releaseSave = resolve; });
            const badLayer = { id: 7, name: '画板\r\n\ufffd' };
            const ps = {
                app: { documents: scenario === 'no document' ? [] : [{}], activeDocument: {
                    id: 42, path: 'C:/source/output.psd',
                    name: scenario === 'unsafe document' ? '设计\u0001.psd' : 'output.psd',
                    layers: scenario === 'unsafe artboard' ? [badLayer] : [],
                    activeLayers: scenario === 'unsafe artboard' ? [badLayer] : [],
                    saveAs: { async png() {
                        if (scenario === 'throw' || scenario === 'action error') throw new Error('save failed');
                        if (scenario === 'delayed save') {
                            saveStarted = true;
                            await saveGate;
                        }
                    } }
                } },
                core: { async executeAsModal(fn) { await fn(); }, showAlert() { throw new Error('Unexpected Photoshop dialog'); } },
                action: { async batchPlay() {
                    submitted = true;
                    if (scenario === 'throw') throw new Error('action threw');
                    return scenario === 'action error' ? [{ _obj: 'error', message: 'action failed' }] : [{}];
                } }
            };
            const uxp = { storage: { localFileSystem: { async getEntryWithUrl(url) {
                return url === 'file:/task' ? { async createFile() { return { async write(json) { reports.push(JSON.parse(json)); } }; } }
                    : { async createFile(name) { return { name }; }, async getEntries() { return scenario === 'no output' ? [] : [{ name: 'output.png', isFile: true }]; } };
            } } } };
            const executing = new vm.Script('(async()=>{' + script + '})()').runInNewContext({ require(name) { return name === 'photoshop' ? ps : uxp; } });
            if (scenario === 'delayed save') {
                for (let i = 0; i < 50 && !saveStarted; i++) await Promise.resolve();
                assert.equal(saveStarted, true);
                assert.equal(reports.at(-1).done, false, 'must not report completion before saveAs finishes');
                releaseSave();
            }
            await executing;
            const result = reports.at(-1);
            assert.equal(result.done, true);
            // 原生画板的空目录仅代表后台尚未写入，由 C# 等待并确认完整文件。
            const accepted = scenario === 'success' || (background && scenario === 'no output') ||
                (!background && scenario.startsWith('unsafe')) || scenario === 'delayed save';
            assert.equal(result.success, accepted);
            if (accepted) assert.deepEqual(result.sourceDocument, { id: 42, path: 'C:/source/output.psd' });
            if (!background && accepted) {
                for (const key of ['modalWaitMs', 'prepareFileMs', 'saveMs', 'totalMs'])
                    assert.ok(Number.isFinite(result.timings[key]) && result.timings[key] >= 0);
            }
            if (background && accepted) {
                assert.equal(result.backgroundExport, true);
                assert.equal(result.format, 'png');
                assert.equal(result.files.length, 0);
                assert.equal(result.expectedFileCount, 0);
            }
            if (!accepted) {
                assert.ok(result.error.length > 0);
                assert.ok(result.stage.length > 0);
                assert.equal(result.path, 'file:/output');
                assert.ok(result.stack.length > 0);
                if (scenario.startsWith('unsafe')) {
                    assert.equal(submitted, false);
                    assert.match(result.error, /名称/);
                    assert.match(result.error, scenario === 'unsafe document' ? /U\+0001/ : /U\+000D/);
                    assert.match(result.error, /后重试/);
                }
            }
            console.log('PASS ' + mode + ': ' + scenario); count++;
        }
    }
    const closeScript = fs.readFileSync(path.join(root, 'save_and_close.psjs'), 'utf8')
        .replaceAll('{{DOCUMENT_ID}}', '42')
        .replace('{{DOCUMENT_PATH}}', 'C:/source/output.psd')
        .replace('{{RESULT_FOLDER_URL}}', 'file:/task');
    for (const scenario of ['original', 'path changed', 'missing document', 'save failed']) {
        const reports = [];
        const other = { id: 90, path: 'C:/source/output.psd', close() { throw new Error('Wrong document'); } };
        let closed = false;
        const target = {
            id: 42, path: scenario === 'path changed' ? 'C:/source/renamed.psd' : other.path,
            async close(option) {
                assert.equal(option, 'saveChanges');
                if (scenario === 'save failed') throw new Error('Save failed');
                closed = true;
                ps.app.documents = [other];
            }
        };
        const ps = {
            app: { documents: scenario === 'missing document' ? [other] : [other, target], activeDocument: other },
            core: { async executeAsModal(fn) { await fn(); } },
            constants: { SaveOptions: { SAVECHANGES: 'saveChanges' } }
        };
        const uxp = { storage: { localFileSystem: { async getEntryWithUrl() {
            return { async createFile() { return { async write(json) { reports.push(JSON.parse(json)); } }; } };
        } } } };
        await new vm.Script('(async()=>{' + closeScript + '})()').runInNewContext({ require(name) { return name === 'photoshop' ? ps : uxp; } });
        assert.equal(reports.at(-1).done, true);
        assert.equal(reports.at(-1).success, scenario === 'original');
        assert.equal(closed, scenario === 'original');
        console.log('PASS save_and_close: ' + scenario); count++;
    }
    console.log(count + ' UXP completion behavior tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
