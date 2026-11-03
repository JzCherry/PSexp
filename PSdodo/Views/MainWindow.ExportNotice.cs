using System.Diagnostics;
using Avalonia.Threading;
using PsExporter.Models;

namespace PsExporter.Views;

public partial class MainWindow
{
    private ExportNoticeWindow? _exportNotice;

    private void OnExportCompleted(string directory, int count)
    {
        var model = _animationViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing || model?.ShowExportNotice != true || !ReferenceEquals(model, _animationViewModel)) return;
            var screen = (_isDocked ? Screens.ScreenFromPoint(_dockExpandedPos) : Screens.ScreenFromWindow(this))
                ?? Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen is null) return;

            _exportNotice?.Close();
            var notice = new ExportNoticeWindow();
            _exportNotice = notice;
            notice.Closed += (_, _) =>
            {
                if (ReferenceEquals(_exportNotice, notice)) _exportNotice = null;
            };
            try
            {
                var parent = model.ExportNoticeTarget == ExportNoticeTarget.ParentFolder
                    ? Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))?.FullName
                    : null;
                notice.ShowOn(screen, TimeSpan.FromSeconds(model.ExportNoticeDurationSeconds), count, directory, parent is not null,
                    () => model.TryOpenExportDirectory(parent ?? directory));
            }
            catch (Exception error)
            {
                notice.Close();
                Trace.TraceWarning($"无法显示导出提示：{error.Message}");
            }
        });
    }
}
