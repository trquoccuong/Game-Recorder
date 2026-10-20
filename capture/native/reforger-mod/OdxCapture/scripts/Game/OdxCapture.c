// Odx capture, client part (document/games/arma-reforger.md §5). While odx-app records a round it writes the
// round's name to $profile:odx/control.txt; this mod then logs the render camera every frame to
// $profile:odx/<round>.csv and ends the file with "end" once control.txt is gone. Raw log only: odx-app converts it
// (Odx.Capture ReforgerCamera). Format pinned by capture/dotnet/fixtures/reforger_camera.csv:
// tick_ms (System.GetTickCount), unix_s (System.GetUnixTime: its rollovers place tick_ms on the Windows clock),
// third_person (1 / 0, -1 without a controlled character), the camera transform rows (right, up, forward, position)
// and the vertical FOV in degrees.
modded class ArmaReforgerScripted
{
	protected ref TBC_CameraLog m_TbcCamera;

	override void OnUpdate(BaseWorld world, float timeslice)
	{
		super.OnUpdate(world, timeslice);
		if (!m_TbcCamera)
			m_TbcCamera = new TBC_CameraLog();
		m_TbcCamera.Update(this);
	}
}

class TBC_CameraLog
{
	static const string DIR = "$profile:odx/";
	static const string CONTROL = "$profile:odx/control.txt";
	static const string HEADER = "tick_ms,unix_s,third_person,right_x,right_y,right_z,up_x,up_y,up_z,fwd_x,fwd_y,fwd_z,pos_x,pos_y,pos_z,vfov_deg";
	static const int POLL_MS = 250;

	protected ref FileHandle m_Log;
	protected string m_Round;
	protected int m_NextPollMs;

	void Update(ArmaReforgerScripted game)
	{
		int tick = System.GetTickCount();
		if (tick >= m_NextPollMs)
		{
			m_NextPollMs = tick + POLL_MS;
			Follow(ReadControl());
		}
		if (!m_Log)
			return;

		CameraManager cameras = game.GetCameraManager();
		if (!cameras)
			return;
		CameraBase camera = cameras.CurrentCamera();
		if (!camera)
			return;
		vector mat[4];
		camera.GetWorldCameraTransform(mat);

		string line = string.Format("%1,%2,%3", tick, System.GetUnixTime(), ThirdPerson());
		for (int r = 0; r < 4; r++)
			line += string.Format(",%1,%2,%3", F(mat[r][0]), F(mat[r][1]), F(mat[r][2]));
		line += "," + F(camera.GetVerticalFOV());
		m_Log.WriteLine(line);
	}

	// The round odx-app is recording, "" when none.
	protected static string ReadControl()
	{
		if (!FileIO.FileExists(CONTROL))
			return "";
		FileHandle control = FileIO.OpenFile(CONTROL, FileMode.READ);
		if (!control)
			return "";
		string round;
		control.ReadLine(round);
		control.Close();
		return round.Trim();
	}

	protected void Follow(string round)
	{
		if (round == m_Round)
			return;
		if (m_Log)
		{
			m_Log.WriteLine("end");
			m_Log.Close();
			m_Log = null;
			Print("TBC_CameraLog: closed " + m_Round);
		}
		m_Round = round;
		if (round == "")
			return;
		m_Log = FileIO.OpenFile(DIR + round + ".csv", FileMode.WRITE);
		if (!m_Log)
		{
			Print("TBC_CameraLog: cannot open " + DIR + round + ".csv");
			return;
		}
		m_Log.WriteLine(HEADER);
		Print("TBC_CameraLog: logging " + round);
	}

	// 1 / 0 for the locally controlled entity's camera, -1 when there is none (dead, spectating, no camera handler).
	protected static int ThirdPerson()
	{
		IEntity controlled = SCR_PlayerController.GetLocalControlledEntity();
		if (!controlled)
			return -1;
		CameraHandlerComponent handler = CameraHandlerComponent.Cast(controlled.FindComponent(CameraHandlerComponent));
		if (!handler)
			return -1;
		if (handler.IsInThirdPerson())
			return 1;
		return 0;
	}

	protected static string F(float v)
	{
		return v.ToString(-1, 6);
	}
}
