using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Threading;
using PsExporter.Models;
using PsExporter.ViewModels;

namespace PsExporter.Views;

public partial class MainWindow
{
    private ExportNoticeWindow? _exportNotice;

    private void OnExportStarted(string directory, int? expectedFileCount)
    {
        var model = _animationViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (NoticeModel(model) is not { } attached || NoticeScreen() is not { } screen) return;
            // 上一轮的卡片可能还在倒计时：新一轮整张重来，位置也按这一轮所在的屏幕重算。
            _exportNotice?.Close();
            TryNotice("导出进度", notice => notice.ShowProgress(screen, directory, expectedFileCount, attached.ExportedCount,
                NoticeParentFolder(attached, directory) is not null));
        });
    }

    private void OnExportProgressChanged()
    {
        if (_exportNotice is not { ShowsProgress: true } notice) return;
        notice.UpdateProgress(_animationViewModel?.ExportedCount ?? 0);
    }

    private void OnExportCompleted(string directory, int count)
    {
        var model = _animationViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (NoticeModel(model) is not { } attached || NoticeScreen() is not { } screen) return;
            TryNotice("导出提示", notice =>
            {
                var parent = NoticeParentFolder(attached, directory);
                notice.ShowSuccess(screen, TimeSpan.FromSeconds(attached.ExportNoticeDurationSeconds), count, directory,
                    parent is not null, () => attached.TryOpenExportDirectory(parent ?? directory));
            });
        });
    }

    private void OnExportEndedWithoutResult(bool cancelled)
    {
        var model = _animationViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (NoticeModel(model) is not { } attached || _exportNotice is not { ShowsProgress: true } notice) return;
            notice.ShowEnded(TimeSpan.FromSeconds(attached.ExportNoticeDurationSeconds), cancelled);
        });
    }

    /// <summary>提示窗只归“完成提示”这一枚开关管：事件里那个视图模型可能已经换过，只认还挂在窗上的。</summary>
    private MainViewModel? NoticeModel(MainViewModel? model)
        => _isClosing || model?.ShowExportNotice != true || !ReferenceEquals(model, _animationViewModel) ? null : model;

    /// <summary>"点击打开"落点：设置选了上级就打开上级，纯路径计算，导出没跑完也能先算出卡片文案。</summary>
    private static string? NoticeParentFolder(MainViewModel model, string directory)
        => model.ExportNoticeTarget == ExportNoticeTarget.ParentFolder
            ? Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))?.FullName
            : null;

    private Screen? NoticeScreen()
        => (_isDocked ? Screens.ScreenFromPoint(_dockExpandedPos) : Screens.ScreenFromWindow(this))
            ?? Screens.ScreenFromPoint(Position) ?? Screens.Primary;

    private void TryNotice(string what, Action<ExportNoticeWindow> apply)
    {
        if (_exportNotice is null)
        {
            var notice = new ExportNoticeWindow();
            notice.Closed += (_, _) => { if (ReferenceEquals(_exportNotice, notice)) _exportNotice = null; };
            _exportNotice = notice;
        }

        try
        {
            apply(_exportNotice);
        }
        catch (Exception error)
        {
            _exportNotice?.Close();
            Trace.TraceWarning($"无法显示{what}：{error.Message}");
        }
    }
}
