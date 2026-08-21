using Godot;

namespace DeepProf;

public partial class ProfilerTail : Node
{
    public ProfilerRuntime Runtime;

    public override void _EnterTree()
    {
        Name = "DeepProfTail";
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = int.MaxValue;
        ProcessPhysicsPriority = int.MaxValue;
    }

    public override void _Process(double delta)
    {
        Runtime?.CloseProcessStep();
    }

    public override void _PhysicsProcess(double delta)
    {
        Runtime?.ClosePhysicsStep();
    }
}
