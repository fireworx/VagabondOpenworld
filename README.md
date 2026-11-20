# VagabondOpenworld

An add-on for [Vagabond](https://github.com/MrEliasen/SPT-Vagabond) (SPT 4.1.x). It changes **no** Vagabond
file and only uses Vagabond's public interfaces. The raid keeps running while you are at a trader or in the
hideout zone.

## What it does

**In a trader's zone** (an extraction point that Vagabond assigns to a trader):

* `Open <trader>` opens the real in-raid trader screen with trading, tasks and services. The raid is not
  ended.
* Trading uses your character inventory only, not the stash.
* Tasks can be accepted in the raid. If a quest gives starting items, they arrive in your messages.
* Purchases and sales count towards the trader's sales total and loyalty.
* Trading gives no Looting XP and no Searching XP. The pop-ups in the bottom right are suppressed only while
  you are at the trader; quest notices stay visible.
* The trader screen is drawn above the HUD (HudBreakout is hidden while it is open).
* With [OpenBarters](https://github.com/egbog/OpenBarters), substitute barters also work and are paid from
  the character inventory.
* Quest hand-ins work in the raid (see [Quests](#quests)).
* `Fast travel` and `Delivery service` (see [Fast travel](#fast-travel) and [Delivery service](#delivery-service)).

**In the hideout zone** (identifier `VGB_HO_...`) and at traders:

* `Read messages` opens a window with your mail: senders on the left (everything you sent to the hideout is
  listed under "Hideout delivery"), the items with their pictures on the right. You take single items or all
  of them. The first group, `Take items to`, has a switch between your **inventory** and your **stash**; what does
  not fit into the inventory stays in the mail.

**Only in the hideout zone:**

* `Send items to hideout` opens a window with what you carry (Backpack, Vest, Pockets, Secure Container).
  Sent items go straight into your stash (take them back with `Open stash`); if the stash has no room they
  arrive as mail from the BTR driver instead (`Read messages`, or the hideout mailbox).
* `Craft` lists the recipes your station levels allow. Ingredients come from what you carry first, then the
  stash. The craft runs in the hideout.
* `Stations` shows the built stations that take items: the generator (fuel tanks, a switch to turn it on and off, how long the
  fuel lasts in real time), the water collector (water filters) and the bitcoin farm (graphics cards, collecting bitcoins).
  Items from the stash or from your pockets go into free slots, and every slot can be unloaded into the stash.
* `Open stash` lists your stash by category. `TAKE` moves an item (with its contents) out of the stash and
  straight into your inventory; if it does not fit it stays in the stash.
* `Collect crafts` lists running and finished crafts. A finished one is collected into your stash (take it
  with `Open stash`); if the stash has no room it arrives in your messages instead.
* `Build hideout` lists every station with what its next level needs. Materials come from what you carry
  first, the rest is taken from your stash. Other requirements (stations, skills, trader levels) are
  checked by the server.
* `Exit and save` leaves the raid like the normal extraction.

Taking and sending items gives no Looting/Searching XP either.

## Fast travel

At a trader, `Fast travel` lists the maps you can reach with transits from the current map: the number of
transits, the price and the time. The raid goes on on the new map with your gear, no menu in between.

* The price is `Rubles Per Slot` x the grid cells of the tactical vest and the backpack you wear x the number of
  transits. It is paid from the roubles you carry.
* Every transit moves the raid clock on by `Hours Per Transit`.
* `Transit Network` decides which maps are linked (every link works both ways).
* With `Keep Raid Time` the next raid starts at the time of day the last one ended with, instead of a random time.
* `Skip Raid Settings Screen` goes from the map selection straight into the raid with the server's defaults
  (PMC and Scav). Do not run RaidSettingsSkipper at the same time.

## Delivery service

At a trader, `Delivery service` sends items to the hideout without leaving the raid. Your inventory is on the left,
the outbox on the right. The price (`Rubles Per Slot` x the grid cells in the outbox) is shown at the bottom, the send
button works when you carry enough roubles. The trader answers with a message that holds the items; you take it in
the hideout zone with `Read messages`.

## Quests

* **Hand-in in the raid.** The TURN IN button of the quest screen works with the items you carry: duplicate
  stacks are merged, from a big stack only what the quest asks for is used (the rest is given back), and the
  selection window is drawn above the trader menu. The same goes for the dialogs of the quest screen (for example
  replacing a quest).
* **Found in raid.** With the server option `HandoverIgnoresFoundInRaid` (default on) every hand-over objective takes
  items that are not found in raid too.
* **Rewards.** When you finish a quest in the raid, the reward items arrive as a message from the trader instead of
  in the inventory (`Read messages`). Starting items of a quest are mailed the same way.

## Fuel from standing cars

Hold `F` (the game's interact key, changeable) while you look at a car, a truck or a wreck within about 3.5 m.
You need a **silicone tube** and a fuel tank (**Expeditionary fuel tank** or **Metal fuel tank**) with room left in what you carry.

* Every car has a random amount of 1 to 15 units for the raid; some cars (`Empty Car Chance`) have none, which the
  prompt shows at once.
* 1 unit takes 1 second (`Seconds Per Unit`), so the time depends on the amount that is taken. The bar fills while you hold the key; releasing it or
  walking away cancels it and nothing is taken.
* The fullest tank that is not full is filled first, then the next, until the car is empty or all tanks are full. What is left stays in the car.
* With the [Plant Time Modifier](https://sp-mod.com/mod/1965/plant-time-modifier-updated-by-crocodilejonesy) mod
  installed, the time is multiplied by its `Repair objective Time Multiplier` (switch `Use Plant Time Modifier`).
* Cars are found by what the game calls them: everything under `VEHICLE/OFF`, door handles (`CarLock_Hand`) and car
  bodies (`car_housing_lod0`). A car that is not found can be reported with the scan key (see Settings).

## Compatibility

* **Wishlist Extended** ([danx91](https://github.com/danx91/WishlistExtended)): its item counts are cached and are not
  updated by changes that go through the server (mail, stash window, stations, crafts, delivery). After such a change this
  add-on tells it to re-read the stash. Nothing is referenced at build time, without the mod nothing happens.
* **Plant Time Modifier**: see above.
* **OpenBarters**, **HudBreakout**: see above.

## Components

* `server/` -> `SPT_Runtime/user/mods/VagabondOpenworld/VagabondOpenworld.Server.dll`
  Routes under `/vagabond-openworld/...` (map, unlock, mail, trade, stash, hideout, stations, generator, quests, log).
* `client/` -> `BepInEx/plugins/VagabondOpenworld.Client.dll`

Copy only the two `VagabondOpenworld.*.dll` files. The `.pdb` and `.deps.json` files are not needed.

## Log files for bug reports

Both are off by default.

* Client: set `Write Log File` to true under `[Z Debug]` in the BepInEx config of this plugin and restart the
  game. The file `VagabondOpenworld.client.<date>_<time>.log` appears (a new file for every game start, old ones stay) next to the plugin.
* Server: with the client log on, the server log is switched on automatically when the first raid starts, and the
  client log names the folder it writes to (`Server log: ...`). Without the client log: create an empty file named `VagabondOpenworld.debug` in the server mod folder (no restart needed).
  The file `VagabondOpenworld.server.<date>_<time>.log` appears (a new file for every server start, old ones stay) next to it. Delete the `.debug` file to switch it off.

## Building

Vagabond must be installed; its DLLs are referenced:

    dotnet build server -c Release -p:TarkovDir="C:\SPT\" -p:VagabondServerDir="C:\SPT\SPT_Runtime\user\mods\<Vagabond folder>\"
    dotnet build client -c Release -p:TarkovDir="C:\SPT\" -p:VagabondClientDir="C:\SPT\BepInEx\plugins\<Vagabond folder>\"

Output: `server/bin/Release/`, `client/bin/Release/`. When routes change, rebuild both parts; a change to one part only needs that part.

## Settings

`BepInEx/config/dev.local.vagabond-openworld.cfg` (the menu lists the sections by name, the debug ones last)

**General**

* `Trader Zone Interaction`: menu in trader zones.
* `Hideout Zone Menu`: menu in hideout zones.
* `Extraction Time`: seconds of the countdown when leaving through the hideout exfil (default 20).
* `Prompt Offset`: how far the zone menu is moved down on the screen (0 = where the game puts it).
* `Hideout Functions At Traders`: also offer the hideout functions (build, craft, collect, stash, send items) at the
  trader exfils. Default off.
* `Skip Raid Settings Screen`, `Keep Raid Time`: see [Fast travel](#fast-travel).

**Hideout**

* `Pure Water Seconds`, `Bitcoin Seconds`: see the end of this file.

**Fast Travel**

* `Fast Travel At Traders`, `Delivery Service At Traders`: switch the two services.
* `Rubles Per Slot`: price per grid cell (transit and delivery).
* `Hours Per Transit`: raid clock hours per transit.
* `Transit Network`: which maps are linked, `map:neighbour,neighbour;map:...`.

**Fuel**

* `Siphon Fuel From Cars`, `Siphon Key` (default F), `Seconds Per Unit` (1), `Fuel Per Car Min/Max` (1/15),
  `Empty Car Chance` (25 %), `Use Plant Time Modifier`.

**Z Debug**

* `Write Log File`: see above.
* `Forward Unity Errors`: write Unity errors to the BepInEx log.
* `Vehicle Scan Key` (F8): writes the car-like objects within 40 m and the carried fuel tanks and silicone tube into
  the log (a finding aid when a car is not recognised).

## Notes

* Close the trader window before leaving the raid, otherwise the XP guard is not ended.
* Whether Vagabond leaves sent mail untouched at the end of the raid shows in the hideout mailbox.
* A trader level-up counts on the server at once; the trader view inside the raid may only know about it
  after the raid.

## Credits and license

Parts of the trading, mail and window style are adapted from
[ZeroMenuTime](https://github.com/r2rka1/ZeroMenuTime) (MIT, Copyright (c) 2026 r2rka1). The attribution is
in the affected files. OpenBarters and HudBreakout are only addressed at runtime and are not bundled.

## Hideout rules (server config)

On its first start the server writes `VagabondOpenworld.config.json` next to `VagabondOpenworld.Server.dll`.
Everything is off by default, except `HandoverIgnoresFoundInRaid`. The rules apply to the whole hideout, also outside the raid:

| Option | Effect |
| --- | --- |
| `InstantBuild`, `BuildSeconds` | Station levels are built at once (or after `BuildSeconds`). |
| `IgnoreTraderRequirements` | A station level no longer asks for a trader loyalty level. |
| `IgnoreStationRequirements` | A station level no longer asks for the level of another station. |
| `KeepRequirementsFor` | Station numbers that stay a requirement anyway. Default `[22, 24]`: the defective wall (gear rack, gym and trophies still need it) and the first weapon stand (the second one needs it). |
| `InstantCrafts` | Crafts are done at once (endless ones such as the bitcoin farm or water collector are untouched). |
| `HandoverIgnoresFoundInRaid` | Default on. Quest hand-over objectives take any item, found in raid or not (the server removes the found-in-raid condition when it starts). |
| `CraftsCostFuel` | A craft started from the raid menu costs generator fuel (original craft time times the generator's fuel use) and is refused when the generator is off or the fuel is not enough. Crafts started in the normal hideout screen are not charged. |

Remove other mods that change construction or craft times, they would fight over the same data.

## Production times of the endless stations

In the BepInEx menu (section `Hideout`) `Pure Water Seconds` and `Bitcoin Seconds` set how long the water collector needs for one
pure water and the bitcoin farm for one bitcoin with one graphics card. `0` (the default) keeps the server's own value.
The client sends the values to the server at the start of a raid and whenever the stations window opens.
