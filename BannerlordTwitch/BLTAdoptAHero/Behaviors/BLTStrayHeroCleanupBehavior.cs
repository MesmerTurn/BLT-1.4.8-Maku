using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Reported by Maku: after battles, strangers with names kept turning up in his party as
    /// troops, wearing ordinary soldier gear. They are soldiers turned into heroes - most likely by
    /// troop ascension, when one of the streamer's own men killed an adopted hero and the new lord
    /// was left with no party, so the battle's aftermath filed them back into the unit they
    /// fought for.
    ///
    /// So this sweeps the rosters once a battle is over: an ascended lord sitting in another
    /// clan's party is sent home, and a hero with no clan who is nobody's companion (a boss, or a
    /// promotion that stopped halfway) is taken out. Real companions and lords are never touched.
    /// </summary>
    public class BLTStrayHeroCleanupBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, _ => Sweep());
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Sweep);
            // Also clears whatever is already sitting in a party from before this fix.
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, _ => Sweep());
        }

        public override void SyncData(IDataStore dataStore) { }

        private static void Sweep()
        {
            try
            {
                foreach (var party in MobileParty.All.ToList())
                {
                    var roster = party?.MemberRoster;
                    if (roster == null) continue;

                    var strays = new List<Hero>();
                    for (int i = 0; i < roster.Count; i++)
                    {
                        var hero = roster.GetCharacterAtIndex(i)?.HeroObject;
                        if (IsStrayBoss(hero, party)) strays.Add(hero);
                    }

                    foreach (var hero in strays)
                    {
                        if (IsForeignAscendedLord(hero, party))
                        {
                            // A real lord of their own clan: send them home, do not retire them.
                            BLTTroopAscension.PlaceInHome(hero);
                            Log.Trace($"[StrayHero] Sent ascended lord {hero.Name} home from {party.Name}");
                            continue;
                        }

                        roster.RemoveTroop(hero.CharacterObject);
                        if (hero.IsAlive) hero.ChangeState(Hero.CharacterStates.Disabled);
                        Log.Trace($"[StrayHero] Removed {hero.Name} from {party.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTStrayHeroCleanupBehavior)}.{nameof(Sweep)}", ex);
            }
        }

        private static bool IsStrayBoss(Hero hero, MobileParty party)
        {
            if (hero == null || hero == Hero.MainHero) return false;
            if (hero == party.LeaderHero) return false;
            if (IsForeignAscendedLord(hero, party)) return true;
            if (hero.Clan != null || hero.CompanionOf != null) return false;
            if (hero.IsAdopted()) return false;
            // No clan and nobody's companion: a hero in a party roster like that belongs to no
            // one - a boss, or a troop whose promotion stopped halfway. Genuine wanderers only
            // ever join a party by being hired, which makes them a companion.
            return true;
        }

        /// <summary>
        /// A lord made from a troop (ascension or !promote) sitting as a member in a party that
        /// belongs to a different clan - the soldier's old unit, usually the player's.
        /// </summary>
        private static bool IsForeignAscendedLord(Hero hero, MobileParty party)
        {
            if (hero?.Clan == null || hero.Clan.Leader != hero) return false;
            if (party.ActualClan == hero.Clan) return false;
            return BLTAdoptAHeroCampaignBehavior.Current?.IsAscended(hero) == true;
        }
    }
}
