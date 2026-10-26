# Arma Reforger: always day, invincible players (task)

> Status: `to test` (API read from the online docs only; nothing built or run yet) ·
> Written 2026-10-26 · Back to [arma-reforger.md](./arma-reforger.md)
> To be tested on a Windows PC with the game 1.6.0.68, a local command-line server and Arma Reforger Tools (Workbench).

## 1. Request

On the test server (Combat Ops Everon, AWS command-line server):

1. **No night:** the time of day stays fixed in daylight.
2. **Invincible players:** player characters take no damage and do not die.

The command-line server has no `gameProperties`, and vanilla has no option for either, so both go into `TbrainCapture`,
server side, like the spawn points ([arma-reforger-spawn-points.md](./arma-reforger-spawn-points.md) §3).

**Open with the client:** deaths (`OnPlayerKilled` with instigator) are part of the combat interactions in
[arma-reforger.md](./arma-reforger.md) §4. Invincibility is therefore off unless the settings file turns it on (§3): for
tests, or for real rounds only once the client agrees.

## 2. API (online Script API docs, to check on 1.6.0.68)

| Need | API | Note |
|---|---|---|
| Set the time | `BaseWeatherManagerEntity.SetHoursMinutesSeconds(int h, int m, int s, bool immediateChange = false)` | Authority only, broadcast to clients |
| Freeze the clock | `BaseWeatherManagerEntity.SetIsDayAutoAdvanced(bool)` | Authority only, broadcast to clients |
| Get the manager | `ChimeraWorld.GetTimeAndWeatherManager()` → `TimeAndWeatherManagerEntity` (derives from `BaseWeatherManagerEntity`) | `ChimeraWorld.CastFrom(GetGame().GetWorld())` |
| No damage | `DamageManagerComponent.EnableDamageHandling(bool)` on the character's `SCR_CharacterDamageManagerComponent` | Per the Damage System wiki, checked before hit zones and health are touched |
| Hook on spawn | `SCR_BaseGameMode.OnPlayerSpawned(int playerId, IEntity controlledEntity)` | Name and signature to check in the 1.6.0.68 sources (`data002.pak`); newer builds also have `OnPlayerSpawnFinalize_S` |

Sources: [BaseWeatherManagerEntity](https://community.bistudio.com/wikidata/external-data/arma-reforger/EnfusionScriptAPIPublic/interfaceBaseWeatherManagerEntity.html),
[TimeAndWeatherManagerEntity members](https://community.bistudio.com/wikidata/external-data/arma-reforger/ArmaReforgerScriptAPIPublic/interfaceTimeAndWeatherManagerEntity-members.html),
[SCR_CharacterDamageManagerComponent members](https://community.bistudio.com/wikidata/external-data/arma-reforger/ArmaReforgerScriptAPIPublic/interfaceSCR__CharacterDamageManagerComponent-members.html),
[Damage System](https://community.bistudio.com/wiki/Arma_Reforger:Damage_System).

## 3. Design

New file `TbrainCapture/scripts/Game/TbrainServerSettings.c`, server only (`IsMaster()`).

**Settings file** `$profile:tbrain/settings.txt`, read once at game start; one `key value` per line, `#` comments:

```
# hours:minutes, the clock stays there; no line = the scenario's own time and day cycle
time_of_day 10:00
# 1 = player characters take no damage; no line or 0 = normal damage
invincible 1
```

No file or no line = vanilla behaviour, so real rounds are unchanged unless asked. Editing the file + restarting the server
changes the settings without a new addon (the clients' script checksum stays the same).

1. **Time:** in `OnGameStart()`, after `super`, set the time and turn the day cycle off. Combat Ops may pick its own start
   time (`SCR_TimeAndWeatherHandlerComponent`); if it sets the time after `OnGameStart()`, the clock will not stay at
   `time_of_day` (step 2 of §5 tells).
2. **Invincible:** in `OnPlayerSpawned`, `EnableDamageHandling(false)` on the new character. Only player characters; AI keeps
   normal damage.
3. Log each applied setting to `console.log` with a `TBC_Settings:` prefix, and malformed lines.

### Draft script (not compiled yet)

The `SCR_BaseGameMode` mod in `TbrainSpawnPoints.c` already overrides `OnGameStart()`; a second `modded class` in another
file chains to it through `super`, so both work.

```c
// Tbrain capture, server part: fixed time of day and invincible players for test servers
// (document/games/arma-reforger-daytime-invincible.md §3), from $profile:tbrain/settings.txt. No file = vanilla.
modded class SCR_BaseGameMode
{
	override protected void OnGameStart()
	{
		super.OnGameStart();
		if (IsMaster())
			TBC_Settings.Load();
		if (IsMaster() && TBC_Settings.s_bFixedTime)
			TBC_Settings.ApplyTime();
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
	static const string FILE = "$profile:tbrain/settings.txt";
	static bool s_bFixedTime;
	static int s_iHours;
	static int s_iMinutes;
	static bool s_bInvincible;

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
			if (parts.Count() == 2 && parts[0] == "time_of_day")
			{
				array<string> hm = {};
				parts[1].Split(":", hm, false);
				if (hm.Count() == 2)
				{
					s_iHours = hm[0].ToInt();
					s_iMinutes = hm[1].ToInt();
					s_bFixedTime = s_iHours >= 0 && s_iHours < 24 && s_iMinutes >= 0 && s_iMinutes < 60;
					if (s_bFixedTime)
						continue;
				}
			}
			else if (parts.Count() == 2 && parts[0] == "invincible")
			{
				s_bInvincible = parts[1] == "1";
				continue;
			}
			Print("TBC_Settings: line " + lineNumber + " not understood: " + line);
		}
		file.Close();
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
}
```

## 4. Inputs to check in the 1.6.0.68 sources (Workbench Script Editor, or `data002.pak`)

1. `SCR_BaseGameMode.OnPlayerSpawned`: exists, protected, same signature, runs on the server for every spawn and respawn.
2. `ChimeraWorld.GetTimeAndWeatherManager()` and `SetHoursMinutesSeconds` / `SetIsDayAutoAdvanced` with the signatures above.
3. Does Combat Ops Everon set its own start time or weather (`SCR_TimeAndWeatherHandlerComponent` on the game mode, random
   start time)? If it does after `OnGameStart()`, apply the time later (e.g. `GetGame().GetCallqueue().CallLater(..., 1000)`).
4. Anything that kills without going through damage handling: `Kill()`, drowning, falling, bleeding already started,
   unconsciousness (`SetPermitUnconsciousness`).

## 5. Steps and checks (on Windows)

1. Add the script to `TbrainCapture` (§3). → check: Workbench compiles the addon with no error.
2. **Time:** local command-line server (`-server {DFAC5FABD11F2390}Missions/26_CombatOpsEveron.conf … -addons TbrainCapture
   -profile …`) with `time_of_day 10:00` only, client `-client 127.0.0.1:2001`.
   → check: `TBC_Settings: time fixed at 10:0` in the server log; daylight when joining; still the same light after 10+
   minutes; a second client joining later also sees daylight (the setting reaches JIP clients).
3. Same with `time_of_day 22:00` once. → check: night, which proves the setting is what sets the time (not the scenario's
   own start time).
4. **Invincible:** add `invincible 1`. → check: `TBC_Settings: player <id> invincible` on each spawn and respawn; shot by AI,
   grenade at the feet, a fall from a roof, a run-over by a vehicle, swimming far out: no death, no bleeding, no unconscious
   state. Note anything that still kills.
5. **AI unchanged:** shoot an AI. → check: AI still dies; `OnPlayerKilled` still fires for AI kills.
6. **No file:** remove `settings.txt`. → check: `TBC_Settings: no …`, day cycle and damage are vanilla.
7. **Camera log:** record one short round with odx-app. → check: `TBC_CameraLog` still writes (the script change must not
   break it).
8. **AWS:** copy the addon to `/opt/reforger/addons` (lowercase `scripts/`), `settings.txt` to
   `/opt/reforger/profile/profile/tbrain/`, restart `reforger`; push to `main` so CI rebuilds TbrainReforger.exe, players
   reinstall it. → check: a Windows client joins without the checksum error; steps 2 and 4 hold on AWS.
9. Record the results here (what works, what still kills) and set the status; add the settings file to
   [arma-reforger.md](./arma-reforger.md) §5.

## 6. Results

(to fill in after the test)
