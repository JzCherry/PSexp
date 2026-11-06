// 执行真实嵌入脚本，模拟 UXP DOM 和存储；不能替代 Photoshop 实机验收。
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const template = fs.readFileSync(path.join(__dirname, "../../PSdodo/Resources/Scripts/export/export_names.psjs"), "utf8")
    + "\n" + fs.readFileSync(path.join(__dirname, "../../PSdodo/Resources/Scripts/export/layer_targets.psjs"), "utf8")
    + "\n" + fs.readFileSync(path.join(__dirname, "../../PSdodo/Resources/Scripts/export/layer_batch.psjs"), "utf8");

async function run(options = {}) {
    const layers = options.layers || [
        { id: 1, name: "首层", visible: true },
        { id: 2, name: "次层", visible: true },
        { id: 3, name: "隐藏层", visible: false },
        { id: 4, name: "排除覆盖", visible: true },
        { id: 5, name: "背景", visible: true, isBackgroundLayer: true }
    ];
    const original = layers.map(layer => layer.visible);
    const entries = new Set(options.existing || []);
    const saves = [], reports = [], alerts = [];
    const folder = {
        async getEntries() {
            throw new Error("no such file or directory");
        },
        async createFile(name, { overwrite }) {
            if (!overwrite && entries.has(name)) throw new Error("file exists");
            entries.add(name);
            return { name };
        }
    };
    const doc = { name: "设计.psd", layers, saveAs: {} };
    for (const format of ["png", "jpg"]) doc.saveAs[format] = async (entry, settings, asCopy) => {
        assert.equal(asCopy, true);
        if (options.failAt === saves.length + 1) throw new Error("save failed");
        saves.push({ name: entry.name, format, settings, visible: layers.filter(layer => layer.visible).map(layer => layer.name) });
    };
    const ps = {
        app: { documents: options.noDocument ? [] : [doc], activeDocument: doc },
        core: {
            async executeAsModal(fn) { await fn({ isCancelled: !!options.cancel }); },
            async showAlert(message) { alerts.push(message); }
        }
    };
    const storage = { localFileSystem: {
        async getEntryWithUrl(url) {
            if (url === "file:/task") return { async createFile() { return {
                async write(json) {
                    const state = JSON.parse(json);
                    if (state.done) assert.deepEqual(layers.map(layer => layer.visible), original);
                    reports.push(state);
                }
            }; } };
            assert.equal(url, "file:/output");
            if (options.missingDirectory) throw new Error("no such file or directory");
            return folder;
        }
    } };
    const args = {
        RECURSIVE: String(!!options.recursive), SELECTED_ONLY: "false", EXPORT_FOLDER_URL: "file:/output", RESULT_FOLDER_URL: "file:/task",
        FORMAT: options.format || "png", QUALITY: String(options.quality || 80), RENAME: String(options.rename !== false),
        EXISTING_EXPORT_NAMES: JSON.stringify(options.rename === false ? [] : [...entries]),
        IMAGE_NAME: options.imageName || "", FILE_NAME: "export", WITH_DOC_NAME: "true", SHOW_HINT: String(!!options.hint)
    };
    const script = template.replace(/\{\{([A-Z_]+)\}\}/g, (_, key) => args[key]);
    assert.ok(!script.includes("{{"));
    const context = vm.createContext({ require(name) {
        if (name === "photoshop") return ps;
        if (name === "uxp") return { storage };
        throw new Error("Unexpected module " + name);
    } });
    await new vm.Script("(async () => {\n" + script + "\n})()").runInContext(context);
    assert.deepEqual(layers.map(layer => layer.visible), original);
    return { saves, reports, result: reports.at(-1), alerts };
}

(async () => {
    const tests = {
        async "unsafe layer characters normalize and collisions receive suffixes"() {
            const { saves, result } = await run({ layers: [
                { id: 1, name: "海报\r\n😀\u0001\ufffd\ud800", visible: true },
                { id: 2, name: "海报\r\n😀\u0001\ufffd\ud800", visible: true }
            ] });
            assert.deepEqual(saves.map(save => save.name), ["设计_海报__😀___.png", "设计_海报__😀____1.png"]);
            assert.equal(result.success, true);
        },
        async "top-level exports include hidden targets and skip excluded targets"() {
            const { saves, result } = await run({});
            assert.deepEqual(saves.map(save => save.name), ["设计_首层.png", "设计_次层.png", "设计_隐藏层.png"]);
            assert.ok(saves.every(save => save.visible.includes("排除覆盖") && save.visible.includes("背景")));
            assert.equal(result.success, true);
        },
        async "top-level mode exports hidden targets and keeps background and excluded overlays"() {
            const { saves, result } = await run();
            assert.deepEqual(saves.map(save => save.name), ["设计_首层.png", "设计_次层.png", "设计_隐藏层.png"]);
            assert.deepEqual(saves[1].visible, ["次层", "排除覆盖", "背景"]);
            assert.equal(result.success, true);
        },
        async "JPG quality is converted to 0-12 and custom prefix is preserved"() {
            const { saves } = await run({ format: "jpg", quality: 75, imageName: "自定义" });
            assert.equal(saves[0].settings.quality, 9);
            assert.equal(saves[0].name, "自定义_首层.jpg");
        },
        async "case-insensitive file conflicts receive suffix"() {
            const { saves } = await run({ existing: ["设计_首层.PNG", "设计_首层_1.png"] });
            assert.equal(saves[0].name, "设计_首层_2.png");
        },
        async "overwrite keeps requested name"() {
            const { saves } = await run({ rename: false, existing: ["设计_首层.png"] });
            assert.equal(saves[0].name, "设计_首层.png");
        },
        async "same layer names overwrite one final file without duplicate receipts"() {
            const { saves, result } = await run({ rename: false, layers: [
                { id: 1, name: "同名", visible: true }, { id: 2, name: "同名", visible: true }
            ] });
            assert.equal(saves.length, 2);
            assert.deepEqual(result.files, ["设计_同名.png"]);
            assert.equal(result.success, true);
        },
        async "failed save retains completed files and restores visibility"() {
            const { result } = await run({ failAt: 2 });
            assert.equal(result.success, false);
            assert.deepEqual(result.files, ["设计_首层.png"]);
            assert.match(result.error, /save failed/);
        },
        async "missing directory identifies the failing operation and path"() {
            const { result, saves } = await run({ missingDirectory: true });
            assert.equal(result.success, false);
            assert.equal(saves.length, 0);
            assert.match(result.error, /读取导出目录/);
            assert.match(result.error, /file:\/output/);
            assert.match(result.error, /no such file or directory/);
        },
        async "save failure identifies the destination and retains partial files"() {
            const { result } = await run({ failAt: 2 });
            assert.equal(result.success, false);
            assert.deepEqual(result.files, ["设计_首层.png"]);
            assert.match(result.error, /保存 PNG 副本/);
            assert.match(result.error, /设计_次层\.png/);
        },
        async "empty document returns explicit failure"() {
            const { result } = await run({ noDocument: true });
            assert.equal(result.success, false);
            assert.equal(result.files.length, 0);
        },
        async "no eligible targets returns failure"() {
            const { result } = await run({ layers: [{ id: 1, name: "排除隐藏", visible: false }] });
            assert.equal(result.success, false);
        },
        async "Photoshop cancellation returns failure and restores layers"() {
            const { result } = await run({ cancel: true });
            assert.equal(result.success, false);
            assert.match(result.error, /取消/);
        },
        async "legacy hint setting never creates Photoshop dialogs"() {
            const { reports, alerts } = await run({ hint: true });
            assert.equal(reports.at(-1).done, true);
            assert.equal(alerts.length, 0);
            assert.ok(reports.slice(0, -1).every(report => !report.done));
        }
    };
    for (const [name, test] of Object.entries(tests)) { await test(); console.log("PASS " + name); }
    console.log(Object.keys(tests).length + " UXP script behavior tests passed.");
})().catch(error => { console.error(error); process.exitCode = 1; });
