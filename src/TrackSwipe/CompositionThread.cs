namespace TrackSwipe;

/// <summary>Owns gesture state, native windows and GPU resources on a dedicated STA message loop.</summary>
internal sealed class CompositionThread : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private Exception? _error;
    public UiPump Pump { get; private set; } = null!;

    public CompositionThread(Action<double> frame, Action<Exception> fault, Action cleanup, Action exited)
    {
        _thread = new Thread(() =>
        {
            try { Pump = new UiPump(frame); }
            catch (Exception ex) { _error = ex; }
            finally { _started.Set(); }
            if (_error is not null) return;
            try { MessageLoop.Run(fault); }
            finally
            {
                try { cleanup(); }
                finally { Pump.Dispose(); exited(); }
            }
        })
        { IsBackground = true, Name = "TrackSwipe composition", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
        if (_error is not null) throw _error;
    }

    public T Invoke<T>(Func<T> action)
    {
        if (Thread.CurrentThread == _thread) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pump.Post(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action) => Invoke(() => { action(); return true; });
    public void Post(Action action) => Pump.Post(action);

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            Pump.Post(MessageLoop.Quit);
            _thread.Join();
        }
        _started.Dispose();
    }
}
