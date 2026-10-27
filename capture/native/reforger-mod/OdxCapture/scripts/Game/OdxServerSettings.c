// Odx capture, server part: fixed time of day, invincible players and no AI for test servers
// (document/games/arma-reforger-daytime-invincible.md §3, arma-reforger-no-ai.md), from $profile:odx/settings.txt, one
// "key value" per line, "#" for comments: "time_of_day hh:mm" stops the clock there, "invincible 1" makes player
// characters take no damage, "ai 0" keeps the map free of AI characters.
// No file or no line = vanilla. Editing the file and restarting the server changes them without a new addon.
modded class SCR_BaseGameMode
{
	override protected void OnGameStart()
	{
		super.OnGameStart();
		if (!IsMaster())
			return;
		TBC_Settings.Load();
		// The scenario sets its own start time earlier, in SCR_TimeAndWeatherHandlerComponent.OnWorldPostProcess.
		if (TBC_Settings.s_bFixedTime)
			TBC_Settings.ApplyTime();
		if (TBC_Settings.s_bNoAI)
			TBC_Settings.DisableAI();
	}

	override protected void OnPlayerSpawned(int playerId, IEntity controlledEntity)
	{
		super.OnPlayerSpawned(playerId, controlledEntity);
		if (IsMaster() && TBC_Settings.s_bInvincible && controlledEntity)
			TBC_Settings.MakeInvincible(playerId, controlledEntity);
	}
}

class TBC_Settings
{
	static const string FILE = "$profile:odx/settings.txt";
	static bool s_bFixedTime;
	static int s_iHours;
	static int s_iMinutes;
	static bool s_bInvincible;
	static bool s_bNoAI;

	static void Load()
	{
		if (!FileIO.FileExists(FILE))
		{
			Print("TBC_Settings: no " + FILE + ", vanilla time and damage");
			return;
		}
		FileHandle file = FileIO.OpenFile(FILE, FileMode.READ);
		if (!file)
		{
			Print("TBC_Settings: cannot open " + FILE);
			return;
		}
		string line;
		int lineNumber = 0;
		while (file.ReadLine(line) >= 0)
		{
			lineNumber++;
			line = line.Trim();
			if (line == "" || line.StartsWith("#"))
				continue;
			array<string> parts = {};
			line.Split(" ", parts, true);
			if (parts.Count() == 2 && parts[0] == "time_of_day" && ParseTime(parts[1]))
				continue;
			if (parts.Count() == 2 && parts[0] == "invincible" && (parts[1] == "0" || parts[1] == "1"))
			{
				s_bInvincible = parts[1] == "1";
				Print("TBC_Settings: invincible players " + s_bInvincible);
				continue;
			}
			if (parts.Count() == 2 && parts[0] == "ai" && (parts[1] == "0" || parts[1] == "1"))
			{
				s_bNoAI = parts[1] == "0";
				Print("TBC_Settings: AI " + !s_bNoAI);
				continue;
			}
			Print("TBC_Settings: line " + lineNumber + " not understood: " + line);
		}
		file.Close();
	}

	protected static bool ParseTime(string text)
	{
		array<string> hm = {};
		text.Split(":", hm, false);
		if (hm.Count() != 2)
			return false;
		int parsedHours, parsedMinutes;
		int hours = hm[0].ToInt(parsed: parsedHours);
		int minutes = hm[1].ToInt(parsed: parsedMinutes);
		if (parsedHours != hm[0].Length() || parsedMinutes != hm[1].Length() || hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
			return false;
		s_iHours = hours;
		s_iMinutes = minutes;
		s_bFixedTime = true;
		return true;
	}

	static void ApplyTime()
	{
		ChimeraWorld world = ChimeraWorld.CastFrom(GetGame().GetWorld());
		TimeAndWeatherManagerEntity manager;
		if (world)
			manager = world.GetTimeAndWeatherManager();
		if (!manager)
		{
			Print("TBC_Settings: no time and weather manager, time not set");
			return;
		}
		manager.SetHoursMinutesSeconds(s_iHours, s_iMinutes, 0, true);
		manager.SetIsDayAutoAdvanced(false);
		Print("TBC_Settings: time fixed at " + s_iHours + ":" + s_iMinutes);
	}

	static void MakeInvincible(int playerId, IEntity character)
	{
		SCR_CharacterDamageManagerComponent damage = SCR_CharacterDamageManagerComponent.Cast(
			character.FindComponent(SCR_CharacterDamageManagerComponent));
		if (!damage)
		{
			Print("TBC_Settings: player " + playerId + " has no character damage manager");
			return;
		}
		damage.EnableDamageHandling(false);
		Print("TBC_Settings: player " + playerId + " invincible");
	}

	// The AI limit stops the spawners that ask the AI world first (-disableAI sets the same limit); the AI the scenario
	// spawned before game start, or by a spawner that does not ask, are removed every 2 s.
	static void DisableAI()
	{
		AIWorld aiWorld = GetGame().GetAIWorld();
		if (!aiWorld)
		{
			Print("TBC_Settings: no AI world, AI not disabled");
			return;
		}
		aiWorld.SetAILimit(0);
		Print("TBC_Settings: AI limit 0");
		RemoveAI();
		GetGame().GetCallqueue().CallLater(RemoveAI, 2000, true);
	}

	protected static void RemoveAI()
	{
		array<AIAgent> agents = {};
		GetGame().GetAIWorld().GetAIAgents(agents);
		PlayerManager players = GetGame().GetPlayerManager();
		int removed = 0;
		foreach (AIAgent agent : agents)
		{
			IEntity character = agent.GetControlledEntity();
			if (!character || players.GetPlayerIdFromControlledEntity(character) != 0)
				continue;
			SCR_EntityHelper.DeleteEntityAndChildren(character);
			removed++;
		}
		if (removed > 0)
			Print("TBC_Settings: " + removed + " AI removed");
	}
}
