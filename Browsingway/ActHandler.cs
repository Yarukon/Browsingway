using System.Diagnostics;

namespace Browsingway;

public class ActHandler
{
	public bool IsRunning { get; private set; }
	public event EventHandler<bool>? AvailabilityChanged;
	private int _ticksSinceCheck = 500;
	private int _notify = -1;

	public void Check()
	{
		if (Interlocked.CompareExchange(ref _notify, -1, 1) == 1)
			OnAvailabilityChanged(true);
		else if (Interlocked.CompareExchange(ref _notify, -1, 0) == 0)
			OnAvailabilityChanged(false);

		if (_ticksSinceCheck < 500)
		{
			_ticksSinceCheck++;
			return;
		}

		_ticksSinceCheck = 0;
		Task.Run(() =>
		{
			var proc = Process.GetProcessesByName("Advanced Combat Tracker").FirstOrDefault();
			if (proc is not null)
			{
				// check if the main window is up and we aren't loading
				if (proc.MainWindowTitle.Contains("Advanced Combat Tracker") || (DateTime.Now - proc.StartTime).TotalSeconds >= 5)
				{
					if (!IsRunning)
						ChangeState(true);

					return;
				}
			}
			// check for IINACT
			else if ((proc = Process.GetProcessesByName("IINACT").FirstOrDefault()) is not null)
			{
				if ((DateTime.Now - proc.StartTime).TotalSeconds >= 5)
				{
					if (!IsRunning)
						ChangeState(true);

					return;
				}
			// check for CafeACT
			} else if ((proc = Process.GetProcessesByName("CafeACT").FirstOrDefault()) is not null)
			{
				if (proc.MainWindowTitle.Contains("ACT国服整合") || (DateTime.Now - proc.StartTime).TotalSeconds >= 5)
				{
					if (!IsRunning)
						ChangeState(true);

					return;
				}
			}

			if (IsRunning)
				ChangeState(false);
		});
	}

	private void ChangeState(bool state)
	{
		IsRunning = state;
		Interlocked.Exchange(ref _notify, state ? 1 : 0);
	}

	protected virtual void OnAvailabilityChanged(bool e)
	{
		AvailabilityChanged?.Invoke(this, e);
	}
}