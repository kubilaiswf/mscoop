# MSC Co-op

Co-op multiplayer for My Summer Car, for two to four players over Steam or the local network.
Everyone plays in the host's save: you build the Satsuma together, drive together and share one
wallet.

This is a beta. Things will go out of sync now and then; the host's save is backed up before
every co-op game, so nothing is lost for good.

## What is shared

- **Players**: you see each other walk, crouch, look around, give the finger, hitchhike, punch,
  push, smoke and pee, with a name tag over each head.
- **Items**: picking up, carrying and throwing anything, food and drinks being used up, store
  purchases and the shopping bag, what is taken off the store shelves.
- **Building the car**: fitting and removing parts, bolts, wiring, carburettor and valve
  adjustments, the floor jack, the car jack and the engine hoist.
- **Vehicles**: driving, engine sound, lights, wipers, turn signals, horn, doors and windows. A
  second player can sit in the passenger seat and ride along.
- **The world**: time of day, weather, sleeping, phone calls, garage doors, the wallet, job
  payouts (taken once) and paid bills.

## Limits of this version

- Joining works only while the host is in the main menu; nobody can join a game in progress.
- Only the host can save. Saving ends the session for everyone, like it ends the game.
- Traffic and the people of Peräjärvi are simulated on each player's machine separately.
- With the key at ACC and the engine off, nothing that runs on the battery is shared.
- Mods that add objects to the world must be the same for everyone. MOP does not work with
  co-op, turn it off.

## Install

Requires My Summer Car from Steam and [MSCLoader](https://github.com/piotrulos/MSCModLoader)
1.4.2 or newer. Every player needs the same version of this mod and of the game.

1. Download the zip from the releases.
2. Extract it into `My Summer Car\Mods`: `MSCCoop.dll` goes into `Mods`, `MSCCoop.Net.dll` into
   `Mods\References`.

## Playing

The main menu has an **MSC Co-op** panel on the right.

- **Host**: choose who can join (your Steam friends, everyone, or only the people you invite) and
  how many players, then click *Create lobby*. Invite friends from the panel or the Steam overlay.
  When everyone is in, click Continue or New Game; everyone follows into your save.
- **Join**: friends who are hosting and public lobbies are listed under *Join*. You can also accept
  a Steam invite. Wait in the main menu until the host starts.
- **Local network**: *Host on local network* on one machine, and the host's IP address under
  *Join* on the others.

While playing, the panel shows the players and their ping when the pause menu is open. Save
backups can be restored from the panel in the main menu.

The `coop` console command does the same from the MSCLoader console; `coop` alone lists its
options.

## Building

You need the .NET SDK 9 or newer and the game installed. If the game is not in Steam's default
folder, create `Directory.Build.local.props` next to `MSCCoop.slnx`:

```xml
<Project>
  <PropertyGroup>
    <GameDir>D:\SteamLibrary\steamapps\common\My Summer Car</GameDir>
  </PropertyGroup>
</Project>
```

`dotnet build -c Release` builds the mod and copies it into the game's `Mods` folder;
`dotnet test` runs the network tests. `tools\package.ps1` makes the release zip.

To test alone, `tools\test-lan.ps1` starts two game windows side by side and joins them. The
scenarios worth testing are in [TESTING.md](TESTING.md).

## Contributing and license

See [CONTRIBUTING.md](CONTRIBUTING.md). The project is licensed under
[CC BY-NC-SA 4.0](LICENSE).
