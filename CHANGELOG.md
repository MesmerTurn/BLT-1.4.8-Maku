# Changelog

Bannerlord Twitch, built for Bannerlord 1.4.8, maintained for Maku's stream.

Every entry says what changed and why it mattered. Where a crash was fixed, the exception and
the path it came through are written down, because that is the part a crash report does not
survive long enough to tell you twice.

---

## v2.15

### Heroes actually assault the walls

When a siege assault begins, the attacking formations hand out the ladders, the towers and the
ram, and every unit present is given its part **at that moment**. A hero summoned into the middle
of that fight joins a formation whose work has already been divided up, receives no share of it,
and stands at the spawn with genuinely nothing to do. That is why BLT heroes were not going to
the walls.

Rather than rewriting the siege AI, a hero who has actually stopped moving is detached and sent
at the walls — or the gate, when no way up can be found — through the same routines that
`!formation walls` already uses.

Deliberately narrow: sieges only, attacking side only, adopted heroes only, and only those who
have genuinely stopped. Anyone fighting, climbing or advancing is left alone. The point is to
rescue the heroes the assault forgot, not to take the battle away from people already in it.

- `Send Idle Heroes To The Walls` (Campaign Features, off by default)
- `Seconds Idle Before Sending` (12) — too low interrupts someone queuing at a ladder, too high
  leaves them at the spawn for the whole assault

### Mythic bosses drop a whole set

Everything it wore, or everything it carried, decided per kill. Never both at once: that would
hand a viewer a finished character in a single fight and leave nothing to want from the next one.

The custom item limit is honoured piece by piece rather than checked once, so a viewer who runs
out of room keeps what fits instead of losing the lot, and is told that they could not carry the
rest.

- `Mythic Drops A Whole Set` (on), `Mythic Drop Chance` (100%), `Mythic Drop Power` (5)

### `!formation engage`

Accepted as another word for `charge`. Asking a viewer to remember which of two perfectly natural
words the mod happens to use is a poor trade.

---

## v2.14 — the formation command, reworked

Four real bugs, all of which could bite a viewer mid-battle:

- A **bare `!formation`** could take the handler down: the argument string was split before
  anything checked whether it existed.
- **Keywords were case-sensitive.** `Detach` did nothing, `FRONT` did nothing — and the viewer
  got no error, because it fell through and printed the formation list instead, so it looked like
  the command had simply ignored them.
- **Ordering a personal move while still in the line** (charge, hold, follow, gate, walls)
  reported success and did nothing at all. It now says to detach first.
- The **filtered and unfiltered code paths were near-identical copies**, so every fix had to be
  made twice, and eventually one of them would not have been.

Output was rewritten to be read once, quickly, in a scrolling chat window:

```
before: Infantry 2/3 40 | 1:25[Charge-Line], 2:40[Advance-Wall-Target:Ranged-32],
after:  You are in formation 2 of 3 (40 men). 1: 25 men, charging/line | 2: 40 men,
        advancing/shield wall vs archers 32m | 3: 12 men, holding/loose
```

Replies name what happened instead of `ok`. `!formation help` exists. The settings, which shipped
labelled `FormationCommand` and described as `TESTING` in the config UI, are now `Respect Class`
and `Allow Detachments` with real explanations.

Nothing about how the command works changed, so viewers who knew the old commands keep using them.

---

## v2.13 — the Mythic boss tier

A fourth rarity, rolled before every other one, so it is the rarest thing that can appear.

**Every power from several classes at once.** The other tiers are one class with bigger numbers
behind it. A Mythic takes *every* power, active and passive, from several configured classes —
not the first few. Its own class is always among them, so its gear and its powers still belong
together, and a power shared by two classes is granted once. That is what makes the tier
different in kind rather than just in size: no single build a viewer brings is the answer to it.

**The shockwave.** A landed melee hit knocks everyone hostile within the radius off their feet.
Three decisions are what keep it from feeling cheap:

- it hangs off a **landed hit**, not a timer, so it reads as the force of the blow
- the damage is **blunt**, so it breaks a line rather than deleting one — men get back up
- there is a **cooldown**, which matters more than it looks: without one, a Mythic swinging into
  a crowd re-floors the same soldiers every swing and they never stand up at all, which stops
  being a fight and becomes a cutscene

Defaults: 1% per boss slot, health ×40, armour ×4, size 1.9, deep red bar. Radius 6m, damage 25,
cooldown 4s. Setting the radius to 0 leaves a Mythic an ordinary, if very strong, fighter.

---

## v2.12

### Crash — childbirth

`NullReferenceException` in `EquipmentHelper.AssignHeroEquipmentFromEquipment`, from
`HeroCreator.DeliverOffSpring` through `PregnancyCampaignBehavior.CheckOffspringToDeliver`.

The game dresses a newborn from an equipment template that is not there — which is what the
character templates behind promoted troops and created heroes leave behind. It sits on the daily
hero tick, so it repeats every day until that pregnancy resolves.

Swallowed at the dressing step rather than at the birth, so the child is still delivered and only
misses a starting outfit a newborn has no use for. Logs `[BirthGuard]` with the child, both
parents and the template.

### Bosses are no longer recruitable companions

Bosses are built from wanderer templates, which is what makes them a real hero the engine will
spawn as an agent — but a wanderer is also exactly what taverns offer as a companion for hire, so
fought bosses turned up as recruitable afterwards.

They now leave the wanderer occupation at creation, and any boss that survives is disabled when
the mission ends. Disabled rather than killed on purpose: a death would ripple through the
encyclopedia, relations and the kill feed for someone who was never really part of the world.

---

## v2.11 — banners on created clans

`Banner.CreateRandomBanner` draws from whatever banner icons are registered at that moment, and a
saved banner keeps the icon ids it was made with. A clan could end up pointing at an icon the
installed modules do not have: a blank or black banner at best, and at worst a native crash
inside `ApplyBannerTextureToMesh`, where the mesh lookup returns nothing and nothing checks it.

Both clan creation paths — promoting retinue to a lord, and the nemesis promotion — now ask for a
banner checked against the installed icon tables before it is handed over. A repair sweep on
load, session start and daily fixes clans already saved with a bad one, and a kingdom whose
ruling clan is repaired gets the new banner too.

The banner fix in the other builds would not have caught this: it only inspects clans led by an
**adopted hero**, and these clans are led by a **promoted troop**.

One detail worth recording: the first entry in a banner is its **background**, everything after
it is an **icon**, and the two are looked up in different tables — checking both against one set
throws away perfectly valid banners as broken.

---

## v2.10

### Crash — the settlement claim election

`NullReferenceException` in `DefaultSettlementValueModel.GeographicalAdvantageForFaction`, from
`SettlementClaimantCampaignBehavior.DailyTickSettlement`. The game scores how much a settlement is
worth to each faction that might claim it, and one faction was missing something that scoring
reads. It fires on a daily tick, so it returns the moment the day rolls over — the save cannot be
played past it.

**This was not caused by v2.9.** Nothing v2.9 added goes near settlement claims, so rolling back
would not have helped.

Guarded at two depths: the value scoring answers neutral for a faction it cannot score, letting
the election finish with the candidates it can handle, and the daily tick has a backstop so
anything else in that election costs one settlement one day rather than the campaign. Logs
`[ClaimGuard]` with the settlement and faction.

### Bosses only once per siege

A siege is fought in waves, can be reloaded, and can resume the next day — each of which starts a
fresh battle that rolled fresh bosses, so winning the boss fight meant being made to win it
again. The siege is now remembered by settlement and siege start time and kept in the save. A
later siege of the same settlement gets its own bosses. Marked only once bosses actually reached
the field, so a quiet first wave does not spend it.

### Promoted lords join the viewer's kingdom

Promoting retinue to a lord puts the new lord into whatever kingdom that viewer serves at that
moment, through `ChangeKingdomAction` so the kingdom's bookkeeping runs, and with no contract end
date — a sworn lord rather than a mercenary. A kingdom founded through BLT and a base-game
kingdom are the same kind of object here, so both work without special handling. A viewer with no
kingdom still produces an independent clan.

### Companions grow with each battle

An adopted hero earns from the channel and from their own kills; a promoted companion has neither
and stayed at the level of the troop they came from while the hero they follow pulled away.

Companions of adopted heroes now earn skill XP for each battle they were actually in — a
companion sitting in a town learns nothing — into a skill behind a weapon they genuinely carry,
plus a quarter into a supporting skill, with a multiplier when their side won. Losing still pays.
Adopted heroes are excluded so they do not earn twice.

---

## v2.9

### Siege-specific boss cap

A siege is a longer, denser fight with far more bodies on screen than a field battle, so the
number of bosses that feels right there is not the number that feels right in an open-field
skirmish. `Boss Max Per Siege`; 0 keeps using the general cap.

### Upgrade auto-buy queue

A viewer books an upgrade they cannot afford yet and it buys itself the moment their gold reaches
the price.

- `!upgrade queue fief <settlement> <id>`, `queue clan <id>`, `queue kingdom <id>`
- `!upgrade queue list`, `!upgrade queue clear`
- `!upgrade auto queue ...` also buys the prerequisite chain

Checked once per in-game hour and saved with the campaign, so it survives a reload. A booking
stays queued only while gold or influence is what is missing; any other refusal drops it and
tells the viewer why, so nobody waits on something that can never succeed.

Also fixed: two settings lines in the upgrade command ran outside their null check and could
throw on `!upgrade` before the upgrade system had finished loading.

---

## v2.8

Bosses hold their ground rather than fleeing, and the guard command was added.
