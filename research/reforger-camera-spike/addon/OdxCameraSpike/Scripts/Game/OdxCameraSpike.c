// Camera spike (research/reforger-camera-spike/README.md). Every frame: the render camera's transform and FOV, and the
// engine's own projection of fixed world points (the ground truth that tells the matrix layout and the FOV meaning).
// Every 4-6 s (FlashIntervalMs): one frame with a white square in the top-left corner, found again in odx record's
// video to map System.GetTickCount() onto QPC.
modded class ArmaReforgerScripted
{
	protected ref TBC_CameraSpike m_TbcSpike;

	override void OnUpdate(BaseWorld world, float timeslice)
	{
		super.OnUpdate(world, timeslice);
		if (!m_TbcSpike)
			m_TbcSpike = new TBC_CameraSpike();
		m_TbcSpike.Update(this, world, timeslice);
	}
}

class TBC_CameraSpike
{
	static const string DIR = "$profile:odx";
	static const int FLASH_SIZE_PX = 64;

	protected FileHandle m_Log;
	protected ref array<vector> m_Points = {};
	protected ImageWidget m_Flash;
	protected int m_Frame;
	protected int m_NextFlashMs;
	protected int m_Flashes;

	void Update(ArmaReforgerScripted game, BaseWorld world, float timeslice)
	{
		CameraManager cameras = game.GetCameraManager();
		if (!cameras || !world)
			return;
		CameraBase camera = cameras.CurrentCamera();
		if (!camera)
			return;

		vector mat[4];
		camera.GetWorldCameraTransform(mat);
		if (!m_Log && !Open(mat[3]))
			return;
		WorkspaceWidget workspace = game.GetWorkspace();

		int tick = System.GetTickCount();
		int flash = 0;
		if (tick >= m_NextFlashMs)
		{
			flash = 1;
			m_NextFlashMs = tick + FlashIntervalMs(m_Flashes);
			m_Flashes++;
		}
		ShowFlash(workspace, flash == 1);

		float screenW, screenH;
		workspace.GetScreenSize(screenW, screenH);
		int renderW, renderH;
		System.GetRenderingResolution(renderW, renderH);

		string line = string.Format("%1,%2,%3,%4,%5,%6", m_Frame, tick, F(timeslice), flash, camera.GetCameraIndex(), camera.GetProjType());
		for (int r = 0; r < 4; r++)
			line += string.Format(",%1,%2,%3", F(mat[r][0]), F(mat[r][1]), F(mat[r][2]));
		line += string.Format(",%1,%2,%3,%4,%5,%6,%7", F(camera.GetVerticalFOV()), F(camera.GetNearPlane()), F(camera.GetFarPlane()),
			F(screenW), F(screenH), renderW, renderH);
		// Points relative to the camera: world coordinates in the thousands lose too much precision once printed.
		foreach (vector p : m_Points)
		{
			vector d = p - mat[3];
			vector s = workspace.ProjWorldToScreenNative(p, world);
			line += string.Format(",%1,%2,%3,%4,%5,%6", F(d[0]), F(d[1]), F(d[2]), F(s[0]), F(s[1]), F(s[2]));
		}
		m_Log.FPrintln(line);
		m_Frame++;
	}

	// 12 points fixed in the world around the first camera position: two rings (8 m and 30 m), three heights.
	protected bool Open(vector origin)
	{
		FileIO.MakeDirectory(DIR);
		m_Log = FileIO.OpenFile(DIR + "/camera_spike.csv", FileMode.WRITE);
		if (!m_Log)
			return false;
		for (int i = 0; i < 12; i++)
		{
			float angle = i * 30 * Math.DEG2RAD;
			float radius = 8;
			if (i % 2 == 1)
				radius = 30;
			float height = (i % 3 - 1) * 2;
			m_Points.Insert(origin + Vector(radius * Math.Cos(angle), height, radius * Math.Sin(angle)));
		}
		string header = "frame,tick_ms,timeslice_s,flash,camera_index,proj_type";
		for (int r = 0; r < 4; r++)
			header += string.Format(",m%1x,m%1y,m%1z", r);
		header += ",vfov_deg,near_m,far_m,screen_w,screen_h,render_w,render_h";
		for (int k = 0; k < m_Points.Count(); k++)
			header += string.Format(",p%1_dx,p%1_dy,p%1_dz,p%1_sx,p%1_sy,p%1_sz", k);
		m_Log.FPrintln(header);
		return true;
	}

	protected void ShowFlash(WorkspaceWidget workspace, bool on)
	{
		if (!m_Flash)
		{
			m_Flash = ImageWidget.Cast(workspace.CreateWidget(WidgetType.ImageWidgetTypeID, WidgetFlags.VISIBLE | WidgetFlags.IGNORE_CURSOR,
				Color.Black, 10000, null));
			if (!m_Flash)
				return;
			FrameSlot.SetPos(m_Flash, 0, 0);
			FrameSlot.SetSize(m_Flash, FLASH_SIZE_PX, FLASH_SIZE_PX);
		}
		if (on)
			m_Flash.SetColor(Color.White);
		else
			m_Flash.SetColor(Color.Black);
	}

	// Uneven intervals, so analyze.py can tell which flash of the video is which flash of the log.
	// Same sequence as analyze.py flash_interval_ms.
	protected static int FlashIntervalMs(int index)
	{
		return 4000 + (index * 1237) % 2000;
	}

	protected static string F(float v)
	{
		return v.ToString(-1, 6);
	}
}
