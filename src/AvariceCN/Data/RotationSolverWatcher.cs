using Avarice.StaticData;
using ECommons.EzIpcManager;

namespace Avarice.Data;

internal class RotationSolverWatcher : IDisposable
{
	public RotationSolverWatcher()
	{
		EzIPC.Init(this);
	}

	public static bool IsRSREnabled()
	{
		try
		{
			const string rsrName = "Rotation Solver Reborn";
			foreach (var p in Svc.PluginInterface.InstalledPlugins)
			{
				if ((p.Name.Equals(rsrName, StringComparison.OrdinalIgnoreCase) || p.InternalName.Equals(rsrName, StringComparison.OrdinalIgnoreCase)) && p.IsLoaded)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	public void Dispose()
	{
	}

    public bool IPCAvailable { get; private set; }

    /// <summary>
    /// RSR's currently desired positional for its next GCD action.
    /// Falls back to None if RSR is not installed/loaded.
    /// </summary>
    public EnemyPositional DesiredPositional { get; private set; } = EnemyPositional.None;

	[EzIPCEvent("RotationSolverReborn.ActionUpdater.DesiredPositionalChanged", false)]
	public void OnDesiredPositionalChanged(byte value)
	{
		IPCAvailable = true;
		DesiredPositional = MapPositional(value);
	}

	private static EnemyPositional MapPositional(byte value) => value switch
    {
        1 => EnemyPositional.Rear,
        2 => EnemyPositional.Flank,
        3 => EnemyPositional.Front,
        _ => EnemyPositional.None,
    };
}
