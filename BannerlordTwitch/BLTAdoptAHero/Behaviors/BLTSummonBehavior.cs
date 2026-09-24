using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Util;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using NavalDLC.Missions.MissionLogics;

namespace BLTAdoptAHero
{
    internal class BLTSummonBehavior : AutoMissionBehavior<BLTSummonBehavior>
    {
        public class RetinueState
        {
            public CharacterObject Troop;
            public Agent Agent;
            // We must record this separately, as the Agent.State is undefined once the Agent is deleted (the internal handle gets reused by the engine)
            public AgentState State;
            public bool Died;
        }

        private readonly Dictionary<Agent, bool> _retinueResolution = new();

        public class HeroSummonState
        {
            public Hero Hero;
            public bool WasPlayerSide;
            public bool SpawnWithRetinue;
            public PartyBase Party;
            public AgentState State;
            public Agent CurrentAgent;
            public float SummonTime;
            public int TimesSummoned = 0;
            public List<RetinueState> Retinue { get; set; } = new();
            public List<RetinueState> Retinue2 { get; set; } = new();
            // Companions brought in with this hero, with where they were before, so they can be put
            // back after the battle instead of staying in the party they were spawned from.
            public List<(Hero Hero, MobileParty From, Settlement At)> Companions { get; set; } = new();

            // Everyone who has already been brought into THIS battle, alive or dead. A companion
            // who has fallen stays fallen: the hero may be summoned again, their companions are
            // not, or dying would simply hand the viewer a fresh set of them.
            public HashSet<Hero> CompanionsSpawned { get; } = new();

            // Which party each companion was actually spawned from, so the right roster is tidied
            // afterwards - it is not always this hero's own party any more.
            public Dictionary<Hero, PartyBase> CompanionSpawnParties { get; } = new();

            public int ActiveRetinue => Retinue.Count(r => r.State == AgentState.Active);
            public int DeadRetinue => Retinue.Count(r => r.Died);

            public int ActiveRetinue2 => Retinue2.Count(r => r.State == AgentState.Active);
            public int DeadRetinue2 => Retinue2.Count(r => r.Died);

            private float CooldownTime => BLTAdoptAHeroModule.CommonConfig.CooldownEnabled
                ? BLTAdoptAHeroModule.CommonConfig.GetCooldownTime(TimesSummoned) : 0;

            public bool InCooldown => BLTAdoptAHeroModule.CommonConfig.CooldownEnabled && SummonTime + CooldownTime > CampaignHelpers.GetTotalMissionTime();
            public float CooldownRemaining => !BLTAdoptAHeroModule.CommonConfig.CooldownEnabled ? 0 : Math.Max(0, SummonTime + CooldownTime - CampaignHelpers.GetTotalMissionTime());
            public float CoolDownFraction => !BLTAdoptAHeroModule.CommonConfig.CooldownEnabled ? 1 : 1f - CooldownRemaining / CooldownTime;
        }

        private readonly List<HeroSummonState> heroSummonStates = new();
        private readonly List<Action> onTickActions = new();

        public HeroSummonState GetHeroSummonState(Hero hero)
            => heroSummonStates.FirstOrDefault(h => h.Hero == hero);

        public HeroSummonState GetHeroSummonStateForRetinue(Agent retinueAgent)
            => heroSummonStates.FirstOrDefault(h => h.Retinue.Any(r => r.Agent == retinueAgent));
        public HeroSummonState GetHeroSummonStateForRetinue2(Agent retinue2Agent)
            => heroSummonStates.FirstOrDefault(h => h.Retinue2.Any(r => r.Agent == retinue2Agent));
        public readonly Dictionary<Hero, (Agent killer, KillingBlow blow)> HeroDeathSpecifics = new();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="hero"></param>
        /// <param name="playerSide"></param>
        /// <param name="party"></param>
        /// <param name="forced">Whether the player chose to summon, or was part of the battle without choosing it. This affects what statistics will be updated, so streaks etc. aren't broken</param>
        /// <returns></returns>
        public HeroSummonState AddHeroSummonState(Hero hero, bool playerSide, PartyBase party, bool forced, bool withRetinue)
        {
            var heroSummonState = new HeroSummonState
            {
                Hero = hero,
                WasPlayerSide = playerSide,
                Party = party,
                SummonTime = CampaignHelpers.GetTotalMissionTime(),
                SpawnWithRetinue = withRetinue,
            };
            heroSummonStates.Add(heroSummonState);

            BLTAdoptAHeroCampaignBehavior.Current.IncreaseParticipationCount(hero, playerSide, forced);

            return heroSummonState;
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            SafeCall(() =>
            {
                // We only use this for heroes in battle
                if (CampaignMission.Current.Location != null)
                    return;

                var adoptedHero = agent.GetAdoptedHero();
                if (adoptedHero == null)
                    return;

                var heroSummonState = GetHeroSummonState(adoptedHero)
                                   ?? AddHeroSummonState(adoptedHero,
                                       Mission != null
                                       && agent.Team != null
                                       && Mission.PlayerTeam?.IsValid == true
                                       && agent.Team.IsFriendOf(Mission.PlayerTeam),
                                       adoptedHero.GetMapEventParty(),
                                       forced: true,
                                       withRetinue: true);

                // First spawn, so spawn retinue also
                if (heroSummonState.TimesSummoned == 0 && heroSummonState.SpawnWithRetinue && RetinueAllowed())
                {
                    var formationClass = agent.Formation.FormationIndex;
                    SpawnRetinue(adoptedHero, ShouldBeMounted(formationClass), formationClass,
                        heroSummonState, heroSummonState.WasPlayerSide);
                }

                // Companions come in on EVERY summon, and whether or not retinue is allowed here.
                // They used to be tied to the retinue's one-off spawn, which meant a viewer who
                // was killed and summoned again fought the rest of the battle alone, and a battle
                // where retinue is not allowed never showed them at all. That is what !comp was
                // really working around. SpawnCompanions skips anyone already on the field, so
                // calling it again costs nothing.
                {
                    var formationClass = agent.Formation.FormationIndex;
                    SpawnCompanions(adoptedHero, ShouldBeMounted(formationClass), formationClass,
                        heroSummonState, heroSummonState.WasPlayerSide);
                }

                heroSummonState.CurrentAgent = agent;
                heroSummonState.State = AgentState.Active;
                heroSummonState.TimesSummoned++;
                heroSummonState.SummonTime = CampaignHelpers.GetTotalMissionTime();
                // If hero isn't registered yet then this must be a hero that is part of one of the involved parties
                // already
                HeroDeathSpecifics.Remove(adoptedHero);

            });
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            SafeCall(() =>
            {
                var heroSummonState = heroSummonStates.FirstOrDefault(h => h.CurrentAgent == affectedAgent);
                if (heroSummonState != null)
                {
                    heroSummonState.State = agentState;
                }
                var adoptedHero = affectedAgent.GetAdoptedHero();
                if (adoptedHero != null && (affectedAgent.State == AgentState.Unconscious || affectedAgent.State == AgentState.Killed))
                {
                    HeroDeathSpecifics[adoptedHero] = (affectorAgent, blow);
                }

                // Set the final retinue states
                var (retinueOwner, retinueState) = heroSummonStates
                    .Select(h
                        => (state: h, retinue: h.Retinue.FirstOrDefault(r => r.Agent == affectedAgent)))
                    .FirstOrDefault(h => h.retinue != null);
                var (retinue2Owner, retinue2State) = heroSummonStates
                    .Select(h
                        => (state: h, retinue2: h.Retinue2.FirstOrDefault(r => r.Agent == affectedAgent)))
                    .FirstOrDefault(h => h.retinue2 != null);

                bool inRetinue1 = retinueOwner != null;
                bool inRetinue2 = retinue2Owner != null;

                bool runRetinue1 = false;
                bool runRetinue2 = false;

                if (inRetinue1 && inRetinue2)
                {
                    if (!_retinueResolution.TryGetValue(affectedAgent, out var useRetinue1))
                    {
                        useRetinue1 = MBRandom.RandomInt(0, 2) == 0;
                        _retinueResolution[affectedAgent] = useRetinue1;
                    }

                    runRetinue1 = useRetinue1;
                    runRetinue2 = !useRetinue1;
                }
                else
                {
                    runRetinue1 = inRetinue1;
                    runRetinue2 = inRetinue2;
                }


                if (runRetinue1 && retinueState != null && BLTAdoptAHeroModule.CommonConfig.RetinueDeathChance != 0)
                {
                    if (retinueState.Died)
                        return;

                    if (BLTAdoptAHeroModule.CommonConfig.RetinueDeathChance != 0f &&
                        agentState == AgentState.Killed &&
                        MBRandom.RandomFloat < BLTAdoptAHeroModule.CommonConfig.RetinueDeathChance)
                    {
                        retinueState.Died = true;
                        BLTAdoptAHeroCampaignBehavior.Current.KillRetinue(
                            retinueOwner.Hero,
                            affectedAgent.Character);
                        if (retinueOwner.Hero.FirstName != null)
                        {
                            Log.LogFeedResponse(
                                retinueOwner.Hero.FirstName.ToString(),
                                $"Your {affectedAgent.Character} was killed in battle!");
                        }
                    }
                    retinueState.State = agentState;  // Always update state
                }

                if (runRetinue2 && retinue2State != null && BLTAdoptAHeroModule.CommonConfig.Retinue2DeathChance != 0)
                {
                    if (retinue2State.Died)
                        return;

                    if (BLTAdoptAHeroModule.CommonConfig.Retinue2DeathChance != 0f &&
                        agentState == AgentState.Killed &&
                        MBRandom.RandomFloat < BLTAdoptAHeroModule.CommonConfig.Retinue2DeathChance)
                    {
                        retinue2State.Died = true;
                        BLTAdoptAHeroCampaignBehavior.Current.KillRetinue2(
                            retinue2Owner.Hero,
                            affectedAgent.Character);
                        if (retinue2Owner.Hero.FirstName != null)
                        {
                            Log.LogFeedResponse(
                                retinue2Owner.Hero.FirstName.ToString(),
                                $"Your {affectedAgent.Character} was killed in battle!");
                        }
                    }
                    retinue2State.State = agentState;  // Always update state
                }

                if ((retinue2State != null && retinue2State.Died) || (retinueState != null && retinueState.Died))
                {
                    _retinueResolution.Remove(affectedAgent);
                }

            });
        }

        public void DoNextTick(Action action)
        {
            onTickActions.Add(action);
        }

        public override void OnMissionTick(float dt)
        {
            SafeCall(() =>
            {
                var actionsToDo = onTickActions.ToList();
                onTickActions.Clear();
                foreach (var action in actionsToDo)
                {
                    action();
                }
            });
        }

        protected override void OnEndMission()
        {
            SafeCall(() =>
            {
                // Remove still living retinue troops from their parties
                foreach (var h in heroSummonStates)
                {
                    foreach (var r in h.Retinue.Where(r => r.State != AgentState.Killed))
                    {
                        RemoveOne(h.Party?.MemberRoster, r.Troop);
                    }

                    // Companions too. Only retinue used to be taken back out, so every companion
                    // summoned stayed in the party they were spawned from - usually the streamer's -
                    // as a named hero that could be dismissed like a troop but used for nothing.
                    foreach (var c in h.Companions)
                    {
                        try
                        {
                            var spawnedFrom = h.CompanionSpawnParties.TryGetValue(c.Hero, out var p)
                                ? p : h.Party;
                            ReturnCompanion(c.Hero, spawnedFrom, c.From, c.At);
                        }
                        catch (Exception ex) { Log.Exception($"{nameof(BLTSummonBehavior)}.{nameof(ReturnCompanion)}", ex); }
                    }
                }
            });
        }

        /// <summary>
        /// Takes one of a troop back out of a roster, and only if it is actually in there.
        ///
        /// This used to be AddToCounts(troop, -1) with no check at all. Subtracting a troop a
        /// party no longer has drives that entry's count negative, and a TroopRoster with a
        /// negative count is quietly corrupt: its own index tables no longer agree with what it
        /// holds. The game then crashes later, in whatever innocent code walks that roster next -
        /// in Maku's reports, the party morale calculation during the AI's hourly tick, with BLT
        /// nowhere in the callstack.
        /// </summary>
        private static void RemoveOne(TroopRoster roster, CharacterObject troop)
        {
            if (roster == null || troop == null) return;

            try
            {
                int index = roster.FindIndexOfTroop(troop);
                if (index < 0) return;
                if (roster.GetElementNumber(index) <= 0) return;

                roster.AddToCounts(troop, -1);
            }
            catch (Exception ex)
            {
                Log.Exception($"{nameof(BLTSummonBehavior)}.{nameof(RemoveOne)}", ex);
            }
        }

        /// <summary>
        /// Takes a summoned companion back out of the party they were spawned from and returns
        /// them to where they were: their own party, or the settlement they were waiting in.
        /// </summary>
        public static void ReturnCompanion(Hero hero, PartyBase spawnedFrom, MobileParty from, Settlement at)
        {
            if (hero == null || hero.IsDead) return;

            var now = hero.PartyBelongedTo;
            if (now != null && now == from) return;               // they came from this party
            if (spawnedFrom?.MobileParty != null && spawnedFrom.MobileParty == from) return;

            // Same rule as everywhere else here: never take out of a roster something it does not
            // hold, because that is what leaves the roster corrupt.
            if (now != null && now.LeaderHero != hero && now.MemberRoster.Contains(hero.CharacterObject))
                now.MemberRoster.RemoveTroop(hero.CharacterObject);
            else if (spawnedFrom?.MemberRoster != null && spawnedFrom.MemberRoster.Contains(hero.CharacterObject))
                spawnedFrom.MemberRoster.RemoveTroop(hero.CharacterObject);

            if (from != null && from.IsActive)
            {
                if (!from.MemberRoster.Contains(hero.CharacterObject))
                    AddHeroToPartyAction.Apply(hero, from);
                return;
            }

            var home = at
                       ?? hero.CompanionOf?.Leader?.CurrentSettlement
                       ?? hero.CompanionOf?.HomeSettlement
                       ?? Settlement.All.FirstOrDefault(s => s.IsTown);
            if (home != null && hero.CurrentSettlement == null)
                EnterSettlementAction.ApplyForCharacterOnly(hero, home);
        }

        /// <summary>
        /// Brings a viewer's companions into the battle alongside them.
        ///
        /// Asked for by Maku, who suspected companions were not actually turning up - and they
        /// were not. Promoting retinue to a companion made a real hero and attached them to the
        /// viewer's clan, but nothing ever spawned them into a mission: only retinue troops were.
        /// So a companion existed on paper, cost gold, and then sat out every battle the viewer
        /// fought. The battle XP added for companions was landing on people who were never there.
        ///
        /// Only the clan leader brings the clan's companions. Several viewers can share a clan,
        /// and without that rule each of them would summon the same companions again, duplicating
        /// them across the field.
        /// </summary>
        private static void SpawnCompanions(Hero adoptedHero, bool ownerIsMounted,
            FormationClass ownerFormationClass, HeroSummonState existingHero, bool onPlayerSide)
        {
            try
            {
                var cfg = BLTAdoptAHeroModule.CommonConfig;
                if (cfg?.SummonCompanions != true) return;

                var campaign = BLTAdoptAHeroCampaignBehavior.Current;
                var clan = adoptedHero.Clan;

                // Everyone this viewer has hired comes with them, always. The clan's own
                // companions come only for its leader, because several viewers can share a clan
                // and each of them would otherwise summon the same men over again.
                //
                // Requiring clan leadership for ALL of it was the bug Maku kept hitting: a viewer
                // who is a member rather than the leader - which is most of them - brought nobody,
                // including companions they had paid for themselves.
                var companions = (campaign?.GetHiredCompanions(adoptedHero) ?? Enumerable.Empty<Hero>())
                    .Concat(clan?.Leader == adoptedHero
                        ? clan.Companions ?? Enumerable.Empty<Hero>()
                        : Enumerable.Empty<Hero>())
                    .Distinct()
                    .Where(c => c != null && !c.IsDead && c != adoptedHero)
                    // Nobody comes in twice in one battle, whatever happened to them the first time.
                    .Where(c => !existingHero.CompanionsSpawned.Contains(c))
                    // Deliberately no test for where they are on the map. The viewer's own hero is
                    // summoned out of thin air; holding their companions to a stricter rule than
                    // that is how they ended up never appearing.
                    .ToList();

                if (companions.Count == 0)
                {
                    Log.Trace($"[Summon] {adoptedHero.FirstName}: no companions to bring in.");
                    return;
                }

                int limit = cfg.MaxCompanionsSummoned;
                if (limit > 0 && companions.Count > limit) companions = companions.Take(limit).ToList();

                bool mounted = Mission.Current.Mode != MissionMode.Stealth
                               && !MissionHelpers.InSiegeMission()
                               && ownerIsMounted;

                bool deploymentFlag = Mission.Current.Mode is MissionMode.Deployment;
                var arrived = new List<string>();

                foreach (var companion in companions)
                {
                    var troop = companion.CharacterObject;
                    if (troop == null) continue;

                    if (onPlayerSide && cfg.RetinueUseHeroesFormation)
                        Campaign.Current.SetPlayerFormationPreference(troop, ownerFormationClass);

                    // Which party they fight from decides who can order them about. RTS Camera and
                    // the game's own order UI only reach formations of the PLAYER's party, so a
                    // companion spawned from a viewer's own party is on the field but beyond
                    // anyone's control. Asked for by Maku so he can command them with RTS.
                    var spawnParty = onPlayerSide && cfg.CompanionsJoinPlayerParty
                        ? PartyBase.MainParty ?? existingHero.Party
                        : existingHero.Party;

                    existingHero.CompanionsSpawned.Add(companion);
                    existingHero.Companions.Add((companion, companion.PartyBelongedTo, companion.CurrentSettlement));
                    existingHero.CompanionSpawnParties[companion] = spawnParty;

                    // Only put them in the roster if they are not already in it. A companion who
                    // already travels with this party would otherwise be added a second time, and
                    // a TroopRoster holding the same hero twice has broken index tables - the
                    // crash then happens later, in whatever walks that roster next.
                    if (spawnParty?.MemberRoster != null && !spawnParty.MemberRoster.Contains(troop))
                        spawnParty.MemberRoster.AddToCounts(troop, 1);

                    var agent = SpawnAgent(onPlayerSide, troop, spawnParty,
                        troop.IsMounted && mounted, false, !deploymentFlag);

                    if (agent == null) continue;

                    // Show whose companion this is, the same way retinue is labelled. Asked for by
                    // Maku: companions were on the field but indistinguishable from any other
                    // soldier, so nobody could tell they had turned up at all.
                    try
                    {
                        AccessTools.Field(typeof(Agent), "_name")?.SetValue(agent,
                            new TextObject($"{companion.FirstName} ({adoptedHero.FirstName})"));
                    }
                    catch (Exception ex)
                    {
                        Log.Trace($"[Summon] Could not name companion agent: {ex.Message}");
                    }

                    // Kills by a companion pay their viewer, the same way a retinue kill does -
                    // the companion is theirs, and it is their gold that bought them.
                    BLTAdoptAHeroCustomMissionBehavior.Current.AddListeners(agent,
                        onGotAKill: (killer, killed, state) =>
                        {
                            BLTAdoptAHeroCommonMissionBehavior.Current.ApplyKillEffects(
                                adoptedHero, killer, killed, state,
                                cfg.RetinueGoldPerKill,
                                cfg.RetinueHealPerKill,
                                0, 1,
                                cfg.RelativeLevelScaling,
                                cfg.LevelScalingCap,
                                cfg.MinimumGoldPerKill);
                        });

                    arrived.Add(companion.FirstName.ToString());
                    Log.Trace($"[Summon] Brought companion {companion.Name} in with {adoptedHero.FirstName}.");
                }

                // Say so on screen. Asked for by Maku: with a battle already full of men, there
                // was no way to tell whether companions had turned up or quietly failed to - and
                // "nothing visible happened" is exactly how the last two bugs hid themselves.
                if (arrived.Count > 0 && cfg.AnnounceCompanionArrival)
                {
                    Log.LogFeedEvent(arrived.Count == 1
                        ? "{=}{Companion} joins the battle with {Name}"
                            .Translate(("Companion", arrived[0]), ("Name", adoptedHero.FirstName.ToString()))
                        : "{=}{Count} companions join the battle with {Name}: {List}"
                            .Translate(("Count", arrived.Count),
                                ("Name", adoptedHero.FirstName.ToString()),
                                ("List", string.Join(", ", arrived))));
                }
            }
            catch (Exception ex)
            {
                // A companion that fails to arrive must not cost the viewer their own summon.
                Log.Exception($"{nameof(BLTSummonBehavior)}.{nameof(SpawnCompanions)}", ex);
            }
        }

        private static void SpawnRetinue(Hero adoptedHero, bool ownerIsMounted, FormationClass ownerFormationClass,
            HeroSummonState existingHero, bool onPlayerSide)
        {
            var retinueTroops = BLTAdoptAHeroCampaignBehavior.Current.GetRetinue(adoptedHero).ToList();
            var retinue2Troops = BLTAdoptAHeroCampaignBehavior.Current.GetRetinue2(adoptedHero).ToList();

            bool retinueMounted = Mission.Current.Mode != MissionMode.Stealth
                                  && !MissionHelpers.InSiegeMission()
                                  && (ownerIsMounted || !BLTAdoptAHeroModule.CommonConfig.RetinueUseHeroesFormation);
            var agent_name = AccessTools.Field(typeof(Agent), "_name");
            foreach (var retinueTroop in retinueTroops)
            {
                // Don't modify formation for non-player side spawn as we don't really care
                bool hasPrevFormation = Campaign.Current.PlayerFormationPreferences
                                            .TryGetValue(retinueTroop, out var prevFormation)
                                        && onPlayerSide
                                        && BLTAdoptAHeroModule.CommonConfig.RetinueUseHeroesFormation;

                if (onPlayerSide && BLTAdoptAHeroModule.CommonConfig.RetinueUseHeroesFormation)
                {
                    Campaign.Current.SetPlayerFormationPreference(retinueTroop, ownerFormationClass);
                }

                existingHero.Party.MemberRoster.AddToCounts(retinueTroop, 1);

                bool DeploymentFlag = Mission.Current.Mode is MissionMode.Deployment;
                var retinueAgent = SpawnAgent(onPlayerSide, retinueTroop, existingHero.Party,
                    retinueTroop.IsMounted && retinueMounted, false, !DeploymentFlag);

                existingHero.Retinue.Add(new()
                {
                    Troop = retinueTroop,
                    Agent = retinueAgent,
                    State = AgentState.Active,
                });

                agent_name.SetValue(retinueAgent, new TextObject($"{retinueAgent.Name} ({adoptedHero.FirstName})"));

                retinueAgent.BaseHealthLimit *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);
                retinueAgent.HealthLimit *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);
                retinueAgent.Health *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);

                BLTAdoptAHeroCustomMissionBehavior.Current.AddListeners(retinueAgent,
                    onGotAKill: (killer, killed, state) =>
                    {
                        Log.Trace($"[{nameof(SummonHero)}] {retinueAgent.Name} killed {killed?.Name ?? "unknown"}");
                        BLTAdoptAHeroCommonMissionBehavior.Current.ApplyKillEffects(
                            adoptedHero, killer, killed, state,
                            BLTAdoptAHeroModule.CommonConfig.RetinueGoldPerKill,
                            BLTAdoptAHeroModule.CommonConfig.RetinueHealPerKill,
                            0, 1,
                            BLTAdoptAHeroModule.CommonConfig.RelativeLevelScaling,
                            BLTAdoptAHeroModule.CommonConfig.LevelScalingCap,
                            BLTAdoptAHeroModule.CommonConfig.MinimumGoldPerKill
                        );
                    }
                );

                if (hasPrevFormation)
                {
                    Campaign.Current.SetPlayerFormationPreference(retinueTroop, prevFormation);
                }
            }
            foreach (var retinue2Troop in retinue2Troops)
            {
                // Don't modify formation for non-player side spawn as we don't really care
                bool hasPrevFormation = Campaign.Current.PlayerFormationPreferences
                                            .TryGetValue(retinue2Troop, out var prevFormation)
                                        && onPlayerSide
                                        && BLTAdoptAHeroModule.CommonConfig.RetinueUseHeroesFormation;

                if (onPlayerSide && BLTAdoptAHeroModule.CommonConfig.RetinueUseHeroesFormation)
                {
                    Campaign.Current.SetPlayerFormationPreference(retinue2Troop, ownerFormationClass);
                }

                existingHero.Party.MemberRoster.AddToCounts(retinue2Troop, 1);

                bool DeploymentFlag = Mission.Current.Mode is MissionMode.Deployment;
                var retinue2Agent = SpawnAgent(onPlayerSide, retinue2Troop, existingHero.Party,
                    retinue2Troop.IsMounted && retinueMounted, false, !DeploymentFlag);

                existingHero.Retinue.Add(new()
                {
                    Troop = retinue2Troop,
                    Agent = retinue2Agent,
                    State = AgentState.Active,
                });

                agent_name.SetValue(retinue2Agent, new TextObject($"{retinue2Agent.Name} ({adoptedHero.FirstName})"));

                retinue2Agent.BaseHealthLimit *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);
                retinue2Agent.HealthLimit *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);
                retinue2Agent.Health *= Math.Max(1, BLTAdoptAHeroModule.CommonConfig.StartRetinueHealthMultiplier);

                BLTAdoptAHeroCustomMissionBehavior.Current.AddListeners(retinue2Agent,
                    onGotAKill: (killer, killed, state) =>
                    {
                        Log.Trace($"[{nameof(SummonHero)}] {retinue2Agent.Name} killed {killed?.Name ?? "unknown"}");
                        BLTAdoptAHeroCommonMissionBehavior.Current.ApplyKillEffects(
                            adoptedHero, killer, killed, state,
                            BLTAdoptAHeroModule.CommonConfig.RetinueGoldPerKill,
                            BLTAdoptAHeroModule.CommonConfig.RetinueHealPerKill,
                            0, 1,
                            BLTAdoptAHeroModule.CommonConfig.RelativeLevelScaling,
                            BLTAdoptAHeroModule.CommonConfig.LevelScalingCap,
                            BLTAdoptAHeroModule.CommonConfig.MinimumGoldPerKill
                        );
                    }
                );

                if (hasPrevFormation)
                {
                    Campaign.Current.SetPlayerFormationPreference(retinue2Troop, prevFormation);
                }
            }
        }

        public static Agent SpawnAgent(bool onPlayerSide, CharacterObject troop, PartyBase party, bool spawnWithHorse, bool isReinforcement = false, bool isAlarmed = true)
        {
            var agent = Mission.Current.SpawnTroop(
                new PartyAgentOrigin(party, troop)
                , isPlayerSide: onPlayerSide
                , hasFormation: true
                , spawnWithHorse: spawnWithHorse
                , isReinforcement: isReinforcement
                , formationTroopCount: 1
                , formationTroopIndex: 0
                , isAlarmed: isAlarmed
                , wieldInitialWeapons: true
                // forceDismounted existed on SpawnTroop in 1.3.15 but was removed by 1.4.8
                , initialPosition: null
                , initialDirection: null
            );
            agent.MountAgent?.FadeIn();
            agent.FadeIn();
            return agent;
        }

        public static bool ShouldBeMounted(FormationClass formationClass)
        {
            return Mission.Current.Mode != MissionMode.Stealth
                   && !MissionHelpers.InSiegeMission()
                   && Mission.Current?.IsNavalBattle == false
                   && formationClass is
                       FormationClass.Cavalry or
                       FormationClass.LightCavalry or
                       FormationClass.HeavyCavalry or
                       FormationClass.HorseArcher;
        }

        public static bool RetinueAllowed() => MissionHelpers.InSiegeMission() || MissionHelpers.InFieldBattleMission();

        // Game 1.3.15 had a naval class literally called ShipAgentSpawnLogic; by 1.4.8 that name
        // is gone and the naval spawn logic lives in DefaultNavalMissionAgentSpawnLogic /
        // NavalRaidMissionAgentSpawnLogic instead. A [HarmonyPatch(typeof(...))] against a type
        // that no longer exists doesn't just skip - it throws "Undefined target method" out of
        // PatchAll, which aborts EVERY patch in this assembly and leaves the whole mod dead.
        //
        // So resolve the target by name at runtime across whichever naval spawn-logic types this
        // game version actually has, and let Prepare() cleanly skip the patch when none of them
        // declare the method (e.g. NavalDLC absent, or another rename in a future version).
        [HarmonyPatch]
        public static class Patch_IsAnyTeamsUnfilled
        {
            private static IEnumerable<MethodBase> FindTargets() =>
                new[] { "DefaultNavalMissionAgentSpawnLogic", "NavalRaidMissionAgentSpawnLogic", "ShipAgentSpawnLogic" }
                    .Select(AccessTools.TypeByName)
                    .Where(t => t != null)
                    .Select(t => AccessTools.DeclaredMethod(t, "IsAnyTeamsUnfilled"))
                    .Where(m => m != null)
                    .Cast<MethodBase>();

            static bool Prepare() => FindTargets().Any();

            static IEnumerable<MethodBase> TargetMethods() => FindTargets();

            static bool Prefix(ref bool __result)
            {
                // Always return true, ignoring original logic
                __result = true;
                return false; // skip original method
            }
        }
    }
}