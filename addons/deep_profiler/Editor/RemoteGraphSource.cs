using Godot;

namespace DeepProf;

public sealed class RemoteGraphSource : IGraphSource
{
    private readonly ProfilerDebuggerPlugin debugger;

    public RemoteGraphSource(ProfilerDebuggerPlugin debugger)
    {
        this.debugger = debugger;
    }

    public bool Live => debugger != null && debugger.IsRunning;

    public void RequestTree(ulong id, int offset, int limit)
    {
        debugger?.SendCommand(Protocol.CmdTree, id, offset, limit);
    }

    public void RequestObject(ulong id)
    {
        debugger?.SendCommand(Protocol.CmdObject, id);
    }

    public void RequestCensus()
    {
        debugger?.SendCommand(Protocol.CmdCensus);
    }

    public void RequestSignals()
    {
        debugger?.SendCommand(Protocol.CmdSignals, 6000);
    }

    public void RequestInstances(string className)
    {
        debugger?.SendCommand(Protocol.CmdInstances, className, 500);
    }

    public void RequestAblate(ulong id, int frames)
    {
        debugger?.SendCommand(Protocol.CmdAblate, id, frames);
    }

    public void RequestHighlight(ulong id)
    {
        debugger?.SendCommand(Protocol.CmdHighlight, id, 4.0f);
    }

    public void RequestWatch(int slot, ulong id, string path)
    {
        debugger?.SendCommand(Protocol.CmdWatch, slot, id, path);
    }

    public void RequestFrameCapture()
    {
        debugger?.SendCommand(Protocol.CmdFrameDetail);
    }

    public void SetTimeScale(float scale)
    {
        debugger?.SendCommand(Protocol.CmdTimeScale, scale);
    }

    public void SetGamePaused(bool paused)
    {
        debugger?.SendCommand(Protocol.CmdPauseGame, paused);
    }

    public void ForceCollect()
    {
        debugger?.SendCommand(Protocol.CmdCollect);
    }

    public void SetRate(int hz)
    {
        debugger?.SendCommand(Protocol.CmdRate, hz);
    }

    public void SetPaused(bool paused)
    {
        debugger?.SendCommand(Protocol.CmdPause, paused);
    }

    public void SetScopeCapture(bool enabled)
    {
        debugger?.SendCommand(Protocol.CmdScopes, enabled);
    }

    public void SetOverlay(bool visible)
    {
        debugger?.SendCommand(Protocol.CmdOverlay, visible);
    }
}
