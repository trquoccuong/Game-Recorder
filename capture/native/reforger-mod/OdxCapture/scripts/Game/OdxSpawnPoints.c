// Odx capture, server part: extra player spawn points (document/games/arma-reforger-spawn-points.md §3). When the game
// mode starts, the server reads $profile:odx/spawn_points.txt, one point per line: "x z [name]" in world metres, "#"
// for comments, and spawns a spawn point on the terrain at each, for each playable faction (the deploy map only shows a
// player the points of their own faction). Points are server config: editing the file and restarting the server changes
// them without a new addon (the clients' script checksum stays the same).
modded class SCR_BaseGameMode
{
	override protected void OnGameStart()
	{
		super.OnGameStart();
		if (IsMaster())
			TBC_SpawnPoints.Create();
	}
}

class TBC_SpawnPoints
{
	static const string FILE = "$profile:odx/spawn_points.txt";
	// Per-faction prefabs (their m_sFaction and flag icon), GUIDs read from the 1.6.0.68 resourceDatabase.rdb; another
	// faction gets SpawnPoint_Base with its key set.
	static const ResourceName PREFAB_US = "{5A2220DD0C16958B}Prefabs/MP/Spawning/SpawnPoint_US.et";
	static const ResourceName PREFAB_USSR = "{4FEA25583D0975F7}Prefabs/MP/Spawning/SpawnPoint_USSR.et";
	static const ResourceName PREFAB_FIA = "{2B1758F237E4820E}Prefabs/MP/Spawning/SpawnPoint_FIA.et";
	static const ResourceName PREFAB_BASE = "{E7F4D5562F48DDE4}Prefabs/MP/Spawning/SpawnPoint_Base.et";
	// Above the terrain, so the point is not buried where the ground is uneven.
	static const float HEIGHT_OFFSET = 0.5;

	static void Create()
	{
		if (!FileIO.FileExists(FILE))
		{
			Print("TBC_SpawnPoints: no " + FILE + ", no extra spawn points");
			return;
		}
		FileHandle file = FileIO.OpenFile(FILE, FileMode.READ);
		if (!file)
		{
			Print("TBC_SpawnPoints: cannot open " + FILE);
			return;
		}
		array<string> factions = PlayableFactions();
		if (factions.IsEmpty())
		{
			Print("TBC_SpawnPoints: no playable faction, no extra spawn points");
			file.Close();
			return;
		}

		BaseWorld world = GetGame().GetWorld();
		string line;
		int lineNumber = 0;
		int created = 0;
		while (file.ReadLine(line) >= 0)
		{
			lineNumber++;
			line = line.Trim();
			if (line == "" || line.StartsWith("#"))
				continue;
			array<string> parts = {};
			line.Split(" ", parts, true);
			int parsedX, parsedZ;
			float x, z;
			if (parts.Count() >= 2)
			{
				x = parts[0].ToFloat(parsed: parsedX);
				z = parts[1].ToFloat(parsed: parsedZ);
			}
			if (parts.Count() < 2 || parsedX != parts[0].Length() || parsedZ != parts[1].Length())
			{
				Print("TBC_SpawnPoints: line " + lineNumber + " is not \"x z [name]\": " + line);
				continue;
			}
			string name = "Odx " + (created + 1);
			if (parts.Count() > 2)
			{
				name = parts[2];
				for (int i = 3; i < parts.Count(); i++)
					name += " " + parts[i];
			}

			vector position = Vector(x, world.GetSurfaceY(x, z) + HEIGHT_OFFSET, z);
			foreach (string faction : factions)
			{
				if (Spawn(world, position, faction, name))
					Print("TBC_SpawnPoints: " + name + " (" + faction + ") at " + position.ToString());
				else
					Print("TBC_SpawnPoints: line " + lineNumber + ": spawning the " + faction + " point failed");
			}
			created++;
		}
		file.Close();
		Print("TBC_SpawnPoints: " + created + " position(s) for " + factions.Count() + " playable faction(s)");
	}

	protected static array<string> PlayableFactions()
	{
		array<string> keys = {};
		FactionManager manager = GetGame().GetFactionManager();
		if (!manager)
			return keys;
		array<Faction> factions = {};
		manager.GetFactionsList(factions);
		foreach (Faction faction : factions)
		{
			SCR_Faction scripted = SCR_Faction.Cast(faction);
			if (scripted && scripted.IsPlayable())
				keys.Insert(faction.GetFactionKey());
		}
		return keys;
	}

	protected static bool Spawn(BaseWorld world, vector position, string faction, string name)
	{
		ResourceName prefabName = PREFAB_BASE;
		if (faction == "US")
			prefabName = PREFAB_US;
		else if (faction == "USSR")
			prefabName = PREFAB_USSR;
		else if (faction == "FIA")
			prefabName = PREFAB_FIA;
		Resource prefab = Resource.Load(prefabName);
		if (!prefab || !prefab.IsValid())
			return false;

		EntitySpawnParams params = new EntitySpawnParams();
		params.TransformMode = ETransformMode.WORLD;
		params.Transform[3] = position;
		SCR_SpawnPoint point = SCR_SpawnPoint.Cast(GetGame().SpawnEntityPrefab(prefab, world, params));
		if (!point)
			return false;
		point.SetFactionKey(faction);
		point.SetSpawnPointName(name);
		return true;
	}
}
