#target photoshop
// 探针脚本：返回当前文档信息（JSON 字符串）
// 占位符：无
function probeDocumentInfo() {
    function jsonStr(s) {
        s = String(s);
        s = s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')
             .replace(/[\x00-\x1f\x7f-\x9f\u2028\u2029\ud800-\udfff]/g, function (ch) {
                 return '\\u' + ('0000' + ch.charCodeAt(0).toString(16)).slice(-4);
             });
        return '"' + s + '"';
    }
    var empty = '{"name":"","hasArtboards":false,"artboardCount":0,"hasSlices":false,"width":0,"height":0,"path":""}';
    try {
        if (app.documents.length === 0) {
            $.writeln('PSX_LOG|info|no open document');
            return empty;
        }
        var doc = app.activeDocument;
        var name = doc.name;
        var width = 0, height = 0;
        try { width = Math.round(doc.width.as('px')); } catch (e) {}
        try { height = Math.round(doc.height.as('px')); } catch (e) {}
        var path = '';
        try { path = doc.path ? doc.path.fsName : ''; } catch (e) {}

        var abCount = 0;
        try {
            var propId = charIDToTypeID('Prpr');
            var numLayersId = stringIDToTypeID('numberOfLayers');
            var abEnabledId = stringIDToTypeID('artboardEnabled');
            var lyrId = charIDToTypeID('Lyr ');
            var docRef = new ActionReference();
            docRef.putProperty(propId, numLayersId);
            docRef.putEnumerated(charIDToTypeID('Dcmn'), charIDToTypeID('Ordn'), charIDToTypeID('Trgt'));
            var appDesc = executeActionGet(docRef);
            var numOfLayers = appDesc.getInteger(numLayersId);
            for (var i = 1; i <= numOfLayers; i++) {
                var layerRef = new ActionReference();
                layerRef.putProperty(propId, abEnabledId);
                layerRef.putIndex(lyrId, i);
                var layerDesc = executeActionGet(layerRef);
                if (layerDesc.hasKey(abEnabledId) && layerDesc.getBoolean(abEnabledId)) {
                    abCount++;
                }
            }
        } catch (e) {
            $.writeln('PSX_LOG|warn|artboard count failed: ' + e);
        }

        // 只把实际存在的原生用户切片/基于图层的切片作为切片目标。
        var hasSlices = false;
        try {
            var slicesId = stringIDToTypeID('slices');
            var originId = stringIDToTypeID('origin');
            var typeId = stringIDToTypeID('type');
            var sliceRef = new ActionReference();
            sliceRef.putProperty(stringIDToTypeID('property'), slicesId);
            sliceRef.putEnumerated(stringIDToTypeID('document'), stringIDToTypeID('ordinal'), stringIDToTypeID('targetEnum'));
            var sliceRoot = executeActionGet(sliceRef).getObjectValue(slicesId);
            var sliceList = sliceRoot.getList(slicesId);
            for (var si = 0; si < sliceList.count; si++) {
                var slice = sliceList.getObjectValue(si);
                var origin = typeIDToStringID(slice.getEnumerationValue(originId));
                var sliceType = typeIDToStringID(slice.getEnumerationValue(typeId));
                if ((origin === 'userGenerated' || origin === 'layerGenerated') && sliceType === 'image') {
                    hasSlices = true;
                    break;
                }
            }
        } catch (sliceError) {
            $.writeln('PSX_LOG|warn|native slice detection failed: ' + sliceError);
        }

        var json = '{"name":' + jsonStr(name) +
                   ',"hasArtboards":' + (abCount > 0 ? 'true' : 'false') +
                   ',"artboardCount":' + abCount +
                   ',"hasSlices":' + (hasSlices ? 'true' : 'false') +
                   ',"width":' + width +
                   ',"height":' + height +
                   ',"path":' + jsonStr(path) + '}';
        $.writeln('PSX_LOG|info|document=' + name + ' artboards=' + abCount + ' slices=' + hasSlices + ' size=' + width + 'x' + height);
        return json;
    } catch (e) {
        $.writeln('PSX_LOG|error|probeDocumentInfo: ' + e);
        return empty;
    }
}
__psx_result = probeDocumentInfo();
