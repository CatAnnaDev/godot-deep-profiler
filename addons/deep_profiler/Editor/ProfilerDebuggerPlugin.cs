using System;
using Godot;
using GDArray = Godot.Collections.Array;
using GDDict = Godot.Collections.Dictionary;

namespace DeepProf;

[Tool]
public partial class ProfilerDebuggerPlugin : EditorDebuggerPlugin
{
    public ProfilerData Data;
    public Action<bool> ConnectionChanged;

    private int activeSession = -1;

    public override bool _HasCapture(string capture)
    {
        return capture == Protocol.Prefix;
    }

    public override void _SetupSession(int sessionId)
    {
        EditorDebuggerSession session = GetSession(sessionId);
        if (session == null)
            return;
        activeSession = sessionId;
        session.Started += () => OnStarted(sessionId);
        session.Stopped += () => OnStopped(sessionId);
        SendCommand(Protocol.CmdHello);
    }

    private void OnStarted(int sessionId)
    {
        activeSession = sessionId;
        Data?.Reset();
        SendCommand(Protocol.CmdHello);
    }

    private void OnStopped(int sessionId)
    {
        if (Data != null)
            Data.Connected = false;
        ConnectionChanged?.Invoke(false);
    }

    public bool IsRunning
    {
        get
        {
            EditorDebuggerSession session = activeSession >= 0 ? GetSession(activeSession) : null;
            return session != null && session.IsActive();
        }
    }

    public void SendCommand(params Variant[] arguments)
    {
        EditorDebuggerSession session = activeSession >= 0 ? GetSession(activeSession) : null;
        if (session == null || !session.IsActive())
            return;
        GDArray payload = new GDArray();
        foreach (Variant argument in arguments)
            payload.Add(argument);
        session.SendMessage(Protocol.MsgCommand, payload);
    }

    public override bool _Capture(string message, GDArray data, int sessionId)
    {
        activeSession = sessionId;
        if (Data == null)
            return true;
        try
        {
            Route(message, data);
        }
        catch (Exception exception)
        {
            GD.PushWarning("Deep Profiler failed to read " + message + ": " + exception.Message);
        }
        Data.Notify();
        return true;
    }

    private void Route(string message, GDArray data)
    {
        switch (message)
        {
            case Protocol.MsgHello:
                Data.Reset();
                Data.Hello = data[0].AsGodotDictionary();
                Data.Connected = true;
                ConnectionChanged?.Invoke(true);
                break;
            case Protocol.MsgNames:
                Data.ApplyNames(data[0].AsInt32(), data[1].AsStringArray());
                break;
            case Protocol.MsgFrames:
                Data.ApplyFrames(data[0].AsInt64(), data[1].AsInt32(), data[2].AsFloat32Array());
                break;
            case Protocol.MsgScopes:
                Data.ApplyScopes(data[0].AsGodotDictionary(), data[1].AsGodotDictionary(), data[2].AsDouble(),
                    data[3].AsInt64(), data[4].AsGodotArray(), data[5].AsGodotArray());
                break;
            case Protocol.MsgEvents:
                Data.ApplyEvents(data[0].AsGodotArray());
                break;
            case Protocol.MsgCensus:
                Data.ApplyCensus(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgSignals:
                Data.ApplySignals(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgHeap:
                Data.ApplyHeap(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgInstances:
                Data.ApplyInstances(data[0].AsString(), data[1].AsGodotArray());
                break;
            case Protocol.MsgTree:
                Data.ApplyTree(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgObject:
                Data.ApplyObject(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgAblation:
                Data.ApplyAblation(data[0].AsGodotDictionary());
                break;
            case Protocol.MsgFrameDetail:
                Data.Captured = ScopeView.FromDict(data[0].AsGodotDictionary(), Data.NameOf);
                break;
            case Protocol.MsgFailure:
                GD.PushWarning("Deep Profiler runtime error on " + data[0].AsString() + ": " + data[1].AsString());
                break;
        }
    }
}
