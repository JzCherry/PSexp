// Manage only this exact handler path. Other applications' notifiers are never removed.
function managePSexpEvents() {
    var handler = new File("{{HANDLER_PATH}}");
    var removeOnly = {{REMOVE_ONLY}};
    var restoreDisabled = {{RESTORE_DISABLED}};
    var removed = 0, added = 0, rejected = [];
    for (var i = app.notifiers.length - 1; i >= 0; --i) {
        try {
            if (app.notifiers[i].eventFile.fsName.toLowerCase() === handler.fsName.toLowerCase()) {
                app.notifiers[i].remove();
                removed++;
            }
        } catch (ignored) {}
    }
    for (var remaining = 0; remaining < app.notifiers.length; ++remaining) {
        if (app.notifiers[remaining].eventFile.fsName.toLowerCase() === handler.fsName.toLowerCase())
            return '{"success":false,"error":"Could not remove an existing PSexp notifier; no duplicate registration was added"}';
    }
    if (restoreDisabled && removed > 0 && app.notifiers.length === 0) app.notifiersEnabled = false;
    if (removeOnly) return '{"success":true,"removed":' + removed + '}';
    if (!handler.exists) return '{"success":false,"error":"PSexp event handler is missing"}';
    // Enabling the global switch would also run unrelated disabled scripts. Respect their state.
    if (!app.notifiersEnabled && app.notifiers.length > 0)
        return '{"success":false,"error":"Photoshop events are disabled and other registered scripts exist; global state was preserved"}';
    var enabledByPSexp = !app.notifiersEnabled;
    if (enabledByPSexp) app.notifiersEnabled = true;
    var events = [
        ["Opn ", ""], ["Cls ", ""], ["Mk  ", "Dcmn"], ["slct", "Dcmn"], ["save", ""],
        ["ImgS", ""], ["CnvS", ""], ["Crop", ""], ["slct", "HstS"], ["undo", ""], ["redo", ""],
        ["Mk  ", "Lyr "], ["Dlt ", "Lyr "], ["Dplc", "Lyr "],
        ["newArtboard", ""], ["editArtboardEvent", ""],
        ["newSlice", ""], ["deleteSlice", ""], ["clearAllSlices", ""], ["divideSlice", ""]
    ];
    for (var j = 0; j < events.length; ++j) {
        try {
            if (events[j][1]) app.notifiers.add(events[j][0], handler, events[j][1]);
            else app.notifiers.add(events[j][0], handler);
            added++;
        } catch (ignored) { rejected.push(events[j][0]); }
    }
    if (added === 0 && enabledByPSexp && app.notifiers.length === 0) app.notifiersEnabled = false;
    return '{"success":' + (added > 0 ? 'true' : 'false') + ',"registered":' + added
        + ',"rejected":' + rejected.length + ',"enabledByPSexp":' + (enabledByPSexp ? 'true' : 'false') + '}';
}
__psx_result = managePSexpEvents();
