# Testing

Co-op can be tested alone with two game instances on one machine. The scenarios below are the
situations most likely to break sync. Each one says what should happen; anything else is a bug.

## Setup

1. Build the solution. Both `MSCCoop.dll` and `MSCCoop.Recon.dll` are copied into `Mods`.
2. Host: start the game from Steam and type `coop host lan` in the main menu console.
3. Client: start a second instance from PowerShell:

   ```powershell
   & "<game dir>\mysummercar.exe" -mscloader-sipatch -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -logFile "<game dir>\output_log_client.txt" -coop-lan-join 127.0.0.1
   ```

4. Host clicks Continue; the client follows into the host's save.
5. On both: `coop log on`.

After each scenario run `coop dump` on both instances and keep both game logs
(`output_log.txt` and `output_log_client.txt`). A bug report names the scenario number and
what was seen on each side.

To test under real network conditions, run `coop sim 150 30 3` on the client
(150 ms latency, 30 ms jitter, 3 % loss) and repeat the scenarios marked **[lag]**.

## Test shortcuts

The Recon developer mod adds a `test` command:

| Command | Effect |
| --- | --- |
| `test money 5000` | Adds money (also exercises the shared wallet) |
| `test needs` | Resets hunger, thirst, fatigue, stress, urine and dirt |
| `test keys` | Gives every vehicle key (keys are per player, run it on both) |
| `test tp player` | Teleports to the other player |
| `test tp satsuma` | Teleports next to a vehicle; any scene root name prefix works (`hayosiko`, `ferndale`, `kekmet`, `jonnez`, `boat`) |
| `test mark NAME`, `test tp NAME` | Saves the current position, teleports back to it later |
| `test bring hayosiko` | Drops a vehicle in front of you |

The van (`hayosiko`) drives out of the box, so vehicle scenarios do not need a built Satsuma.

## Scenarios

### Session

1. Client joins while the host is in the menu. Both end up in the same save at the same time of day.
2. Client runs `coop leave`, returns to the menu and joins again. Its avatar disappears on the host and comes back.
3. Client window is closed with the X button. The host drops the client within 15 seconds and its avatar disappears.
4. Host runs `coop leave` while the client is in game. The client is told the session ended and its own save is untouched.

### Players

5. **[lag]** Walk, run, crouch and look up and down in front of each other. Avatars follow smoothly and look where the player looks.
6. Client runs `test tp player`. Its avatar jumps to the host instead of sliding across the map.

### Objects

7. **[lag]** Both players grab the same item at the same moment. Exactly one ends up holding it.
8. **[lag]** One player throws an item, the other catches it mid-air.
9. Drop ten items in a pile, wait until they stop. `coop dump` shows the same poses on both.
10. Client eats a sausage and drinks a beer. Both disappear on the host too.
11. Client pushes the engine hoist into a garage door, then opens and closes the door by hand. The door swings the same on both and stays on its hinges on the host.

### Engine building

12. Client attaches a part, host bolts it. Bolts and the attached state match on both.
13. **[lag]** Both players tighten the same part's bolts at once. Final tightness matches.
14. Remove a part that has other parts attached (cylinder head with its parts). The whole chain comes off on both.
15. Connect wires and turn the carburettor and valve adjusting screws. Values match in the dump.

### Vehicles

16. **[lag]** Host drives the van, client watches from outside. Movement is smooth, engine sound follows rpm and gears.
17. Same with the client driving and the host watching.
18. While one player drives, the other tries to take the driver's seat. It is refused.
19. **[lag]** While the host drives, the client gets in on the other side and presses the driving mode key (Enter) when "PASSENGER" shows. The client rides along without sliding, can look around, and gets out with the same key. On the host the client's avatar sits in the passenger seat.
20. Crash the Satsuma into a wall hard enough for loose parts to fall off. The same parts fall off on both.
21. **[lag]** Drive the Satsuma with bumpers, fenders, doors, hood and seats fitted, fast past the other player. On the other side every part stays on the car, nothing shakes or flies off. The driver opens and closes a door while seated: the other side sees it swing.
22. Refuel a vehicle on one side and drive it on the other for a few minutes. Fuel level matches in the dump and drops at the normal rate.
23. Drive one player far away (another village) while the other stays home, then come back. Nothing has moved or reset at either place. Then the other way round: drive the van more than 300 m away from the other player, park it and walk off. On both sides it stays where it was parked.
24. Start the van's engine, let it idle, switch it off. The other side hears the start, the idle and the stop. Then hand the running van over: the driver gets out with the engine running, the other player gets in and drives off without starting it again.
25. With the engine running, switch the lights, wipers and hazard lights through every position, crank a window, use the turn signals and hold the horn. The other side sees and hears each one. **Known gap:** with the key at ACC and the engine off nothing that needs power is synced yet.

26. **[lag]** One player jacks up the Satsuma with the floor jack, then the car jack, while the other watches. On both sides the car rises on the jack, stays on it and comes back down when it is lowered.

### World

27. Host sleeps. The client's clock jumps with the host's.
28. Wait for the weather to change on the host. The client gets the same weather.
29. Both run `test money 100` at the same moment. Both wallets go up by 200.

### Store and saving

30. Client buys a few things at the store, some big (a beer case, a battery) and some that go in the bag. The host sees the same items and the same bag. The host opens the bag: the items that come out appear on the client too, and the bag empties on both. **Known gap:** the shelves are not synced, the client's shelf stays full on the host.
31. Host saves and quits, then hosts again with the client. Parts the client attached and bolted are still there. Saving ends the session for everyone; the client must not use the save toilet.
32. After a session the client's own save files (`AppData\LocalLow\Amistech\My Summer Car`) have the same modification time as before.
33. Host clicks Continue in a session. The console prints where the save was backed up, under `Mods\Config\Mod Settings\MSCCoop\save-backups`; only the last ten are kept.
