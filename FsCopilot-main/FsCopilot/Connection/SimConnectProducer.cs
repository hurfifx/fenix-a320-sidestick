namespace FsCopilot.Connection;

using System.Threading.Channels;
using Microsoft.FlightSimulator.SimConnect;

public sealed class SimConnectProducer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _appName;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly Task _reconnectTask;

    private readonly Channel<Action<SimConnect>> _priorityQueue =
        Channel.CreateUnbounded<Action<SimConnect>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    private readonly Channel<string> _latestPriorityQueue =
        Channel.CreateUnbounded<string>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    private readonly Channel<Action<SimConnect>> _queue =
        Channel.CreateUnbounded<Action<SimConnect>>(new()
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = true
        });
    private readonly ConcurrentDictionary<string, Action<SimConnect>> _latestPriorityJobs = new();
    private readonly ConcurrentDictionary<string, byte> _queuedLatestPriorityKeys = new();
    private readonly List<Action<SimConnect>> _configure = [];
    private readonly Lock _cfgLock = new();
    private readonly SemaphoreSlim _workAvailable = new(0);

    public SimConnectProducer(string appName)
    {
        _appName = appName;
        _reconnectTask = Task.Factory.StartNew(
            () => AutoReconnect(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _priorityQueue.Writer.TryComplete();
        _latestPriorityQueue.Writer.TryComplete();
        _queue.Writer.TryComplete();
        try { _reconnectTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _workAvailable.Dispose();
        _cts.Dispose();
    }

    private async Task AutoReconnect(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Connect(ct); }
            catch (OperationCanceledException) { /* ignore */ }
            catch (Exception) { await Task.Delay(_reconnectDelay, ct); }
        }
    }

    private async Task Connect(CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        using var evt = new AutoResetEvent(false);
        using var sim = new SimConnect(_appName, IntPtr.Zero, 0, evt, 0);
        evt.WaitOne();
        Log.Information("[SimConnect] Producer connected");

        lock (_cfgLock) foreach (var action in _configure)
        {
            try { action(sim); }
            catch (Exception e) { Log.Fatal(e, "[SimConnect] Producer initialization error"); }

            while (_priorityQueue.Reader.TryRead(out _))
            {
            }

            while (_latestPriorityQueue.Reader.TryRead(out var key))
            {
                _queuedLatestPriorityKeys.TryRemove(key, out _);
                _latestPriorityJobs.TryRemove(key, out _);
            }

            while (_queue.Reader.TryRead(out _))
            {
            }
        }

        while (!ct.IsCancellationRequested)
        {
            if (!await WaitToReadAsync(ct)) return;

            while (TryReadNext(out var job))
            {
                try { job(sim); }
                catch (System.Runtime.InteropServices.COMException) { Log.Information("[SimConnect] Producer disconnected"); return; }
                catch (Exception e) { Log.Fatal(e, "[SimConnect] Producer execution error"); }
            }
        }
    }

    public void Post(Action<SimConnect> action) => TryWrite(_queue.Writer, action);

    public void PostHighPriority(Action<SimConnect> action) => TryWrite(_priorityQueue.Writer, action);

    public void PostLatestHighPriority(string key, Action<SimConnect> action)
    {
        _latestPriorityJobs[key] = action;
        if (_queuedLatestPriorityKeys.TryAdd(key, 0))
            TryWrite(_latestPriorityQueue.Writer, key);
    }

    public IDisposable Configure(Action<SimConnect> configure, Action<SimConnect> deconfigure)
    {
        lock (_cfgLock) _configure.Add(configure);
        Post(configure);

        return Disposable.Create(() =>
        {
            lock (_cfgLock) _configure.Remove(configure);
            Post(deconfigure);
        });
    }

    private bool TryReadNext(out Action<SimConnect> job)
    {
        if (_latestPriorityQueue.Reader.TryRead(out var key))
        {
            _queuedLatestPriorityKeys.TryRemove(key, out _);
            if (_latestPriorityJobs.TryRemove(key, out job!))
                return true;
        }

        if (_priorityQueue.Reader.TryRead(out job!))
            return true;

        return _queue.Reader.TryRead(out job!);
    }

    private async Task<bool> WaitToReadAsync(CancellationToken ct)
    {
        if (_latestPriorityQueue.Reader.TryPeek(out _)
            || _priorityQueue.Reader.TryPeek(out _)
            || _queue.Reader.TryPeek(out _))
            return true;

        await _workAvailable.WaitAsync(ct);
        while (_workAvailable.Wait(0))
        {
        }

        return true;
    }

    private void TryWrite<T>(ChannelWriter<T> writer, T value)
    {
        if (writer.TryWrite(value))
            _workAvailable.Release();
    }
}
