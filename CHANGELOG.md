# Changelog

## 1.1.0

* Fast travel at the traders (transits with price and time), keep raid time, skip the raid settings screen.
* Delivery service at the traders: send carried items to the hideout for a price per grid cell; the trader answers
  with a message.
* `Read messages` can take items into the inventory or the stash (switch).
* Stations window (generator, water collector, bitcoin farm), build, craft, stash and collect windows in the raid.
* Quest hand-in works in the raid (merged duplicates, partial stacks, windows above the menu); the found-in-raid
  condition can be dropped (server option); quest reward items arrive as a trader message.
* Hideout rules in the server config (instant build and crafts, ignored requirements, crafts costing generator fuel).
* Fuel from standing cars with a silicone tube (hold F), time per unit, random amount, empty cars.
* Wishlist Extended is told to re-read the stash after changes that go through the server.
* Log files carry the start time in their name; the debug settings are listed last in the BepInEx menu
  (section `Z Debug`, old values are taken over).

## 1.0.0

First release.

* Open the real trader screen from a trader zone during a raid; trade with the character inventory only.
* No Looting/Searching XP and no pop-ups for trades, mail pickups and sending items.
* Trades count for sales total and loyalty.
* `Read messages` window (senders left, items with pictures right); quest starting items are mailed when a
  quest is accepted in the raid.
* `Send items to hideout` window; sent items can be taken back in the raid.
* `Leave to hideout` in the hideout zone.
* OpenBarters support; the trader screen is shown above the HUD (HudBreakout hidden while open).
