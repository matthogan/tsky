using System.Windows;

namespace tsky;

internal sealed class TaskGroupPopoutManager : IDisposable
{
    private readonly TaskGroupSurfaceSession _session;
    private readonly Func<TaskGroupSurfaceSnapshot> _snapshotProvider;
    private readonly TaskGroupChildWindowActions _actions;
    private TaskGroupChildWindow? _childWindow;

    public TaskGroupPopoutManager(
        TaskGroupSurfaceSession session,
        Func<TaskGroupSurfaceSnapshot> snapshotProvider,
        TaskGroupChildWindowActions actions)
    {
        _session = session;
        _snapshotProvider = snapshotProvider;
        _actions = actions;
        _session.StateChanged += Session_StateChanged;
    }

    public bool IsOpen => _childWindow is not null;

    public void OpenOrActivate()
    {
        var snapshot = _snapshotProvider();
        if (!snapshot.HasValidGroup)
        {
            return;
        }

        if (_childWindow is not null)
        {
            _childWindow.Refresh(snapshot);
            if (_childWindow.WindowState == WindowState.Minimized)
            {
                _childWindow.WindowState = WindowState.Normal;
            }

            _childWindow.Activate();
            return;
        }

        _childWindow = new TaskGroupChildWindow(_session, _actions);

        _childWindow.Closed += ChildWindow_Closed;
        _childWindow.Refresh(snapshot);
        _childWindow.Show();
        _childWindow.Activate();
    }

    public void RefreshIfOpen()
    {
        if (_childWindow is null)
        {
            return;
        }

        var snapshot = _snapshotProvider();
        if (!snapshot.HasValidGroup)
        {
            CloseIfOpen();
            return;
        }

        _childWindow.Refresh(snapshot);
    }

    public void CloseIfOpen()
    {
        if (_childWindow is null)
        {
            return;
        }

        _childWindow.Closed -= ChildWindow_Closed;
        _childWindow.Close();
        _childWindow = null;
    }

    public void Dispose()
    {
        _session.StateChanged -= Session_StateChanged;
        CloseIfOpen();
    }

    private void Session_StateChanged(object? sender, TaskGroupSurfaceSessionChangedEventArgs e)
    {
        RefreshIfOpen();
    }

    private void ChildWindow_Closed(object? sender, EventArgs e)
    {
        if (_childWindow is not null)
        {
            _childWindow.Closed -= ChildWindow_Closed;
        }

        _childWindow = null;
    }
}
