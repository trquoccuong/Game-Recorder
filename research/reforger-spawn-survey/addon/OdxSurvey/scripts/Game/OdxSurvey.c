// Survey for spawn points (README.md): when the game mode starts, samples the terrain every 100 m and writes the open,
// dry, gentle land points to $profile:odx/survey.txt as "x z y". Server only; load it alone, never with players.
modded class SCR_BaseGameMode
{
	override protected void OnGameStart()
	{
		super.OnGameStart();
		if (IsMaster())
			TBC_Survey.Run();
	}
}

class TBC_Survey
{
	static void Run()
	{
		BaseWorld world = GetGame().GetWorld();
		vector mins, maxs;
		world.GetBoundBox(mins, maxs);
		float ocean = world.GetOceanBaseHeight();
		Print("TBC_Survey: bounds " + mins.ToString() + " " + maxs.ToString() + " ocean " + ocean);
		FileHandle survey = FileIO.OpenFile("$profile:odx/survey.txt", FileMode.WRITE);
		int kept = 0;
		int total = 0;
		for (float x = mins[0] + 100; x < maxs[0]; x += 100)
		{
			for (float z = mins[2] + 100; z < maxs[2]; z += 100)
			{
				total++;
				float y = world.GetSurfaceY(x, z);
				if (y < ocean + 3)
					continue;
				TraceParam trace = new TraceParam();
				trace.Start = Vector(x, y + 60, z);
				trace.End = Vector(x, y - 2, z);
				trace.Flags = TraceFlags.ENTS | TraceFlags.WORLD | TraceFlags.OCEAN;
				float f = world.TraceMove(trace, null);
				float hit = trace.Start[1] + (trace.End[1] - trace.Start[1]) * f;
				if (hit > y + 0.5)
					continue;
				if (trace.TraceNorm[1] < 0.93)
					continue;
				float maxDy = 0;
				maxDy = Math.Max(maxDy, Math.AbsFloat(world.GetSurfaceY(x + 6, z) - y));
				maxDy = Math.Max(maxDy, Math.AbsFloat(world.GetSurfaceY(x - 6, z) - y));
				maxDy = Math.Max(maxDy, Math.AbsFloat(world.GetSurfaceY(x, z + 6) - y));
				maxDy = Math.Max(maxDy, Math.AbsFloat(world.GetSurfaceY(x, z - 6) - y));
				if (maxDy > 2)
					continue;
				survey.WriteLine(x.ToString() + " " + z.ToString() + " " + y.ToString());
				kept++;
			}
		}
		survey.Close();
		Print("TBC_Survey: kept " + kept + " of " + total);
	}
}

