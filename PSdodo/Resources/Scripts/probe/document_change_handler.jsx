// PSexp notifier: signal a change only. Never probe/export/activate a document here.
(function (eventId) {
    try {
        if (!(new File("{{LEASE_PATH}}")).exists) return;
        var signal = new File("{{SIGNAL_PATH}}");
        signal.encoding = "UTF-8";
        if (signal.open("w")) {
            signal.write(String(new Date().getTime()) + "|" + String(eventId));
            signal.close();
        }
    } catch (ignored) { /* Notifications must never interrupt Photoshop. */ }
})(typeof arguments !== "undefined" && arguments.length > 1 ? arguments[1] : "unknown");
