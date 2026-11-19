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

**In the hideout zone** (identifier `VGB_HO_...`) and at traders:

* `Read messages` opens a window with your mail: senders on the left (everything you sent to the hideout is
  listed under "Hideout delivery"), the items with their pictures on the right. You take single items or all
  of them into your inventory. What does not fit stays in the mail.

**Only in the hideout zone:**

* `Send items to hideout` opens a window with what you carry (Backpack, Vest, Pockets, Secure Container).
  Sent items arrive as mail from the BTR driver. You can take them back during the raid with
  `Read messages`, or collect them from the hideout mailbox.
* `Leave to hideout` leaves the raid like the normal extraction.

Taking and sending items gives no Looting/Searching XP either.

## Components

* `server/` -> `SPT_Runtime/user/mods/VagabondOpenworld/VagabondOpenworld.Server.dll`
  Routes under `/vagabond-openworld/...`: `map`, `unlock`, `mail/list`, `mail/taken`, `mail/send`,
  `quest/started`, `trade`.
* `client/` -> `BepInEx/plugins/VagabondOpenworld.Client.dll`

Copy only the two `VagabondOpenworld.*.dll` files. The `.pdb` and `.deps.json` files are not needed.

## Building

Vagabond must be installed; its DLLs are referenced:

    dotnet build server -c Release -p:TarkovDir="C:\SPT\" -p:VagabondServerDir="C:\SPT\SPT_Runtime\user\mods\<Vagabond folder>\"
    dotnet build client -c Release -p:TarkovDir="C:\SPT\" -p:VagabondClientDir="C:\SPT\BepInEx\plugins\<Vagabond folder>\"

Output: `server/bin/Release/`, `client/bin/Release/`. When routes change, rebuild both parts.

## Settings

`BepInEx/config/dev.local.vagabond-openworld.cfg`

* `Trader Zone Interaction`: menu in trader zones.
* `Hideout Zone Menu`: menu in hideout zones.
* `Forward Unity Errors` (Debug): write Unity errors to the BepInEx log.

## Notes

* Close the trader window before leaving the raid, otherwise the XP guard is not ended.
* Whether Vagabond leaves sent mail untouched at the end of the raid shows in the hideout mailbox.
* A trader level-up counts on the server at once; the trader view inside the raid may only know about it
  after the raid.

## Credits and license

Parts of the trading, mail and window style are adapted from
[ZeroMenuTime](https://github.com/r2rka1/ZeroMenuTime) (MIT, Copyright (c) 2026 r2rka1). The attribution is
in the affected files. OpenBarters and HudBreakout are only addressed at runtime and are not bundled.
