#target photoshop
// 导出全部画板
// 原理：PS 的"快速导出文档"会自动按画板拆分输出多个文件
// 跟原版"全部画板导出JPG.js"逻辑一致：layer reference + exportDocumentAsFileTypePressed
// 占位符: {{EXPORT_PATH}} {{FORMAT}} {{QUALITY}} {{RENAME}}
function exportAllArtboards() {
    function jsonStr(s) {
        s = String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
        return '"' + s + '"';
    }
    var exportPath = '{{EXPORT_PATH}}';
    var format = '{{FORMAT}}';
    var quality = {{QUALITY}};
    var rename = {{RENAME}}; // true / false（当前由 PS 快速导出自动处理重名）
    try {
        if (app.documents.length === 0) {
            $.writeln('PSX_LOG|error|no open document');
            return '{"success":false,"count":0,"error":"no document"}';
        }

        // 先检查当前文档是否真的有画板，没有画板时 exportDocumentAsFileTypePressed
        // 会把整个文档当成一张图导出，跟"全部画板"语义不符
        var abCount = 0;
        try {
            var propId = charIDToTypeID('Prpr');
            var abEnabledId = stringIDToTypeID('artboardEnabled');
            var docRef = new ActionReference();
            docRef.putProperty(propId, stringIDToTypeID('numberOfLayers'));
            docRef.putEnumerated(charIDToTypeID('Dcmn'), charIDToTypeID('Ordn'), charIDToTypeID('Trgt'));
            var appDesc = executeActionGet(docRef);
            var numOfLayers = appDesc.getInteger(stringIDToTypeID('numberOfLayers'));
            for (var i = 1; i <= numOfLayers; i++) {
                var layerRef = new ActionReference();
                layerRef.putProperty(propId, abEnabledId);
                layerRef.putIndex(charIDToTypeID('Lyr '), i);
                var layerDesc = executeActionGet(layerRef);
                if (layerDesc.hasKey(stringIDToTypeID('artboardEnabled')) &&
                    layerDesc.getBoolean(stringIDToTypeID('artboardEnabled'))) {
                    abCount++;
                }
            }
        } catch (eCount) {
            $.writeln('PSX_LOG|warn|artboard count failed: ' + eCount);
        }
        if (abCount === 0) {
            $.writeln('PSX_LOG|error|no artboards in current document');
            return '{"success":false,"count":0,"error":"no artboards"}';
        }

        // 导出前统计目录中已有文件数（用于后续计算新增数）
        var folderBefore = new Folder(exportPath);
        var filesBefore = folderBefore.getFiles('*.' + format.toLowerCase());
        var beforeCount = filesBefore.length;

        $.writeln('PSX_LOG|info|start export document as ' + format + ' (PS auto-split artboards)');

        // 跟原版一致：layer reference + exportDocumentAsFileTypePressed
        var d = new ActionDescriptor();
        var r = new ActionReference();
        r.putEnumerated(stringIDToTypeID('layer'), stringIDToTypeID('ordinal'), stringIDToTypeID('targetEnum'));
        d.putReference(stringIDToTypeID('null'), r);
        d.putString(stringIDToTypeID('fileType'), format.toLowerCase());
        d.putInteger(stringIDToTypeID('quality'), quality);
        d.putInteger(stringIDToTypeID('metadata'), 0);
        d.putString(stringIDToTypeID('destFolder'), exportPath);
        d.putBoolean(stringIDToTypeID('sRGB'), true);
        d.putBoolean(stringIDToTypeID('openWindow'), false);
        executeAction(stringIDToTypeID('exportDocumentAsFileTypePressed'), d, DialogModes.NO);

        // 快速导出是异步的：命令返回后文件才陆续落盘，轮询等待（最多 20 秒）
        var folderAfter = new Folder(exportPath);
        var filesAfter = folderAfter.getFiles('*.' + format.toLowerCase());
        var afterCount = filesAfter.length;
        var waited = 0;
        while (afterCount <= beforeCount && waited < 200) {
            $.sleep(100);
            waited++;
            filesAfter = folderAfter.getFiles('*.' + format.toLowerCase());
            afterCount = filesAfter.length;
        }
        var newCount = afterCount - beforeCount;
        $.writeln('PSX_LOG|info|all artboards done, new files: ' + newCount + ', total in folder: ' + afterCount + ', waited ' + waited + 'x100ms');
        return '{"success":true,"count":' + newCount + '}';
    } catch (e) {
        $.writeln('PSX_LOG|error|exportAllArtboards: ' + e);
        return '{"success":false,"count":0,"error":' + jsonStr(String(e)) + '}';
    }
}
__psx_result = exportAllArtboards();
