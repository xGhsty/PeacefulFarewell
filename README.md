# Peaceful Farewell

A RimWorld mod (1.6 / 1.7) that lets colonists **peacefully leave the colony** instead of dying, vanishing without a trace or breaking down mentally. Written in C# using [Harmony](https://github.com/pardeike/HarmonyRimWorld).

No DLC required. Requires Harmony.

## What it's about

In vanilla RimWorld colonists practically never leave on their own: the only ways out are death, kidnapping or a mental break. Peaceful Farewell adds an alternative: if a colonist has a strong bond with someone outside the colony (family, a partner, a close friend), or simply feels unwanted or restless in the colony, they can **ask to leave** instead of going through vanilla's negative mechanics. The player always gets a letter with the request and decides: accept or refuse.

## Main mechanics

###  Joining a visiting faction
When a friendly caravan or group of visitors arrives (including guests from the Hospitality mod), a colonist with a close bond (family, spouse/partner, close friend) to someone in that group doesn't decide on a whim. During the visit they go over to talk with that person several times: they reminisce, ask about life outside the colony, the visitor tells stories, sometimes urges "come with us", and sometimes brushes the colonist off. The talks show up in the social log, and the player can interrupt them (e.g. by drafting the colonist).

After a set number of talks the colonist decides, based on the strength of the bond, how the talks went, their real opinion of the visitor (a snub or an insult lowers it), their mood and the friends they would leave behind in the colony. If they want to leave, the player gets a letter with the request. Once accepted, the colonist joins the visitors' faction and leaves the map with them. If they stay, they get a positive "good to see them again" thought.

###  Loneliness ("I don't feel welcome here")
A colonist whose **average opinion** among everyone else is very low, while at the same time **no single colonist** likes them enough (no individual opinion exceeds the threshold), may ask to leave the colony out of a feeling of rejection. Both thresholds are configurable in the mod settings.

###  Wanderlust
Regardless of social relations, a colonist may simply feel the urge to see the world and ask to set out for a while, without abandoning the colony for good (see below: returning wanderers).

###  Letters from afar
Colonists who left may eventually send a letter letting their loved ones in the colony know they are doing well, addressed first to a partner, then family, then friends.

###  Returning wanderers
Colonists who left out of loneliness or wanderlust don't disappear forever. They are tracked in the world (`PF_WandererTracker`), and after some time (a random, configurable range of days) their fate is decided: they may **come back to the colony** or join a hostile faction (the worst, deliberately vague outcome: the player only gets a terse "no word" letter).

- On return, the colonist gets their old **work priorities and schedule** back (restored from a saved snapshot).
- The wanderer's clothing is refreshed cosmetically (some worn items may be swapped for new ones of the same type, simulating wear on the road).
- If the colonist joins a hostile faction, the loved ones left behind (partner/family/friends) get a negative thought about their uncertain fate, scaled by closeness (partner > family > friend).

###  Bonded animals
If a departing colonist has an animal they are bonded with, the animal **follows them** and leaves the colony together with its owner. This applies to all three ways of leaving (joining a faction, loneliness, wanderlust).

###  Colony reactions
A departure doesn't go unnoticed:
- Colonists who disliked the departing colonist feel **relief** (positive thought).
- Those who liked them feel **sorrow** (negative thought).
- Before leaving, the colonist visits their own room/bed for 1-3 in-game hours to say goodbye before heading off the map.

###  Localization
Fully translated into **English and Polish** (`Common/Languages/English`, `Common/Languages/Polish`), including gendered wording where it matters.

## Mod settings

All thresholds and chances are configurable in the mod settings window.

| Section | What it controls |
|---|---|
| Joining visitors | Number of talks with the visitor before the decision, minimum relation importance required |
| Loneliness | Check chance, average opinion threshold, single-opinion threshold that blocks the event |
| Wanderers | Min/max days until a wanderer's fate is decided, toggle for the "joined a hostile faction" letter |
| Wanderlust | Toggle the mechanic, check chance |
| Letters from afar | Toggle |
| Debug | Debug mode, file logging, diagnostic report generation, overview of all active wanderers |

Each refused request has its own cooldown per colonist and event type. Refusing one request (e.g. joining visitors) doesn't block the same colonist from other request types (loneliness, wanderlust), and doesn't affect other colonists.

## Project structure

```
About/                      mod metadata (About.xml)
LoadFolders.xml             1.6 -> 1.6 + Common, 1.7 -> 1.7 + Common
Common/                     content shared by all game versions
  Defs/                     definitions (jobs, thoughts, letters, interactions)
  Languages/                EN/PL translations (Keyed + DefInjected)
1.6/
  Assemblies/               DLL for 1.6 (generated by the build, not in the repo)
1.7/
  Assemblies/               DLL for 1.7 (generated by the build, not in the repo)
Source/PeacefulFarewell/
  Core/                     mechanics logic, scanners, settings, logging, report generator
  DefOf/                    references to the mod's Defs (thoughts, jobs, letters, interactions)
  Harmony/                  Harmony patches
  JobDrivers/               custom jobs (leaving, talks with visitors, animal following, etc.)
  Letters/                  request letters (accept/refuse), shared base ChoiceLetter_PF_Request
  FarewellUtility.cs        shared helper functions
```

## Building

By default, `PeacefulFarewell.csproj` compiles the 1.7 version against the local game install and copies the DLL to `1.7/Assemblies`:

```
dotnet build
```

The 1.6 version is compiled against the `Krafs.Rimworld.Ref` reference package and goes to `1.6/Assemblies`:

```
dotnet build -p:GameVersion=1.6
```

After the build, the whole mod folder is synced to `RimWorld/Mods/PeacefulFarewell`. The game path defaults to `H:\SteamLibrary\steamapps\common\RimWorld`; override it with `-p:RimWorldPath="..."`.

## Requirements

- RimWorld 1.6 or 1.7
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) (loaded before this mod)

## Author

Ghost, with the use of an AI tool (Claude Code)
