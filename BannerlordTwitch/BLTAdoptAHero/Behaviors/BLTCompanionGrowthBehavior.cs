using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Requested by Maku: companions should get stronger with every battle, the way adopted heroes
    /// do.
    ///
    /// An adopted hero earns from the channel and from their own kills, so they climb steadily. A
    /// companion promoted out of a viewer's retinue has neither of those, so they arrive at
    /// whatever level the troop was and stay there forever while the hero they follow pulls away.
    /// This pays them for turning up to a fight, which is the one thing a companion reliably does.
    ///
    /// Only companions belonging to an adopted hero's clan are paid, and only when they were
    /// actually in the battle - a companion sitting in a town learns nothing.
    /// </summary>
    public class BLTCompanionGrowthBehavior : CampaignBehaviorBase
    {
        public static BLTCompanionGrowthBehavior Current { get; private set; }

        public BLTCompanionGrowthBehavior() { Current = this; }

        public override void RegisterEvents()
        {
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            var cfg = BLTAdoptAHeroModule.CommonConfig;
            int xp = cfg?.CompanionBattleXp ?? 0;
            if (xp <= 0 || mapEvent == null) return;

            try
            {
                // Both sides: a companion on the losing side still fought, and a battle survived
                // teaches at least as much as one won.
                foreach (var party in InvolvedParties(mapEvent))
                {
                    var roster = party?.MobileParty?.MemberRoster;
                    if (roster == null) continue;

                    foreach (var hero in HeroesIn(roster))
                    {
                        if (!IsCompanionOfAdoptedHero(hero)) continue;
                        GiveBattleXp(hero, xp, cfg.CompanionBattleXpWinBonus,
                            mapEvent.WinningSide == party.Side);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTCompanionGrowthBehavior)}.{nameof(OnMapEventEnded)}", ex);
            }
        }

        private static IEnumerable<PartyBase> InvolvedParties(MapEvent mapEvent)
        {
            foreach (var side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                var parties = mapEvent.PartiesOnSide(side);
                if (parties == null) continue;

                foreach (var p in parties.ToList())
                {
                    if (p?.Party != null) yield return p.Party;
                }
            }
        }

        private static IEnumerable<Hero> HeroesIn(TroopRoster roster)
        {
            for (int i = 0; i < roster.Count; i++)
            {
                var hero = roster.GetCharacterAtIndex(i)?.HeroObject;
                if (hero != null && !hero.IsDead) yield return hero;
            }
        }

        /// <summary>
        /// A companion of a clan whose leader is an adopted hero. Adopted heroes themselves are
        /// excluded - they already earn through the channel and their own kills, and paying them
        /// here as well would quietly double their progression.
        /// </summary>
        private static bool IsCompanionOfAdoptedHero(Hero hero)
        {
            if (hero?.CompanionOf == null) return false;
            if (hero.IsAdopted()) return false;

            var leader = hero.CompanionOf.Leader;
            return leader != null && leader.IsAdopted();
        }

        private static void GiveBattleXp(Hero hero, int xp, float winBonus, bool won)
        {
            int amount = won ? (int)(xp * Math.Max(1f, winBonus)) : xp;

            // Into the skill behind a weapon they actually carry, so a companion improves at the
            // way they personally fight rather than drifting towards some average.
            var weaponSkills = hero.BattleEquipment
                .YieldFilledWeaponSlots()
                .SelectMany(slot => slot.element.Item.Weapons?.Select(w => w.RelevantSkill)
                                    ?? Enumerable.Empty<SkillObject>())
                .Where(s => s != null)
                .Distinct()
                .ToList();

            hero.AddSkillXp(
                weaponSkills.Any() ? weaponSkills.GetRandomElement() : DefaultSkills.Athletics,
                amount);

            // Plus a smaller share into one of the things a veteran picks up around a battle
            // without training for it.
            var supportSkills = new[]
            {
                DefaultSkills.Athletics,
                DefaultSkills.Riding,
                DefaultSkills.Tactics,
                DefaultSkills.Medicine,
                DefaultSkills.Leadership,
                DefaultSkills.Scouting,
            };

            hero.AddSkillXp(supportSkills.GetRandomElement(), Math.Max(1, amount / 4));
        }
    }
}
