#region using directives

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeoCoordinatePortable;
using PoGo.NecroBot.Logic.Common;
using PoGo.NecroBot.Logic.Event;
using PoGo.NecroBot.Logic.Logging;
using PoGo.NecroBot.Logic.State;
using PoGo.NecroBot.Logic.Utils;
using PokemonGo.RocketAPI.Extensions;
using POGOProtos.Map.Fort;
using POGOProtos.Networking.Responses;
using POGOProtos.Data;

#endregion

namespace PoGo.NecroBot.Logic.Tasks
{
    public static class FarmPokeGymsTask
    {
        public static int TimesZeroXPawarded;
        private static bool teamSettingRequested;
        public static async Task Execute(ISession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var distanceFromStart = LocationUtils.CalculateDistanceInMeters(
                session.Settings.DefaultLatitude, session.Settings.DefaultLongitude,
                session.Client.CurrentLatitude, session.Client.CurrentLongitude);

            if (session.Profile.PlayerData.Team == POGOProtos.Enums.TeamColor.Neutral && session.LogicSettings.TeamColor != POGOProtos.Enums.TeamColor.Neutral && !teamSettingRequested)
            {
                var response = await session.Client.Player.SetPlayerTeam(session.LogicSettings.TeamColor);
                teamSettingRequested = true;
            }

            await HealPokemons(session);

            // Edge case for when the client somehow ends up outside the defined radius
            if (session.LogicSettings.MaxTravelDistanceInMeters != 0 &&
                distanceFromStart > session.LogicSettings.MaxTravelDistanceInMeters)
            {
                Logger.Write(
                    session.Translation.GetTranslation(TranslationString.FarmPokestopsOutsideRadius, distanceFromStart),
                    LogLevel.Warning);

                await session.Navigation.Move(
                    new GeoCoordinate(session.Settings.DefaultLatitude, session.Settings.DefaultLongitude),
                    session.LogicSettings.WalkingSpeedInKilometerPerHour, null, cancellationToken, session.LogicSettings.DisableHumanWalking);
            }

            var pokeGymsList = await GetPokeGyms(session);
            var eggWalker = new EggWalker(1000, session);

            if (pokeGymsList.Count <= 0)
            {
                session.EventDispatcher.Send(new WarnEvent
                {
                    Message = session.Translation.GetTranslation(TranslationString.FarmPokestopsNoUsableFound)
                });
            }

            session.EventDispatcher.Send(new PokeStopListEvent { Forts = pokeGymsList });

            //TODO : CATCH FREE GYMS
            foreach (var pokeGym in pokeGymsList.Where(gym => gym.Enabled && gym.OwnedByTeam == 0))
            {
                var distance = LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude, pokeGym.Latitude, pokeGym.Longitude);
                session.EventDispatcher.Send(new FortTargetEvent { Name = "[---[ FREE POKEGYM ]---]", Distance = distance });
                await session.Navigation.Move(new GeoCoordinate(pokeGym.Latitude, pokeGym.Longitude),
                    session.LogicSettings.WalkingSpeedInKilometerPerHour, null, cancellationToken, session.LogicSettings.DisableHumanWalking);
                var pokemonToDeploy = await GetDeployablePokemon(session);
                if (pokemonToDeploy != null)
                {
                    var response = await session.Client.Fort.FortDeployPokemon(pokeGym.Id, pokemonToDeploy.Id);
                    if (response.Result == FortDeployPokemonResponse.Types.Result.Success)
                    {
                        session.EventDispatcher.Send(new NoticeEvent() { Message = string.Format("SUCCESSFULLY DEPLOYED POKEMON '{0}(CP:{1})' IN GYM '{2}'", pokemonToDeploy.PokemonId, pokemonToDeploy.Cp, pokeGym.Id) });
                    }
                    else
                    {
                        // TODO : FAILED TO DEPLOY POKEMON IN THE GYM
                        session.EventDispatcher.Send(new ErrorEvent() { Message = string.Format("FAILED TO DEPLOY POKEMON '{0}(CP:{1})' IN GYM '{2}' [{3}]", pokemonToDeploy.PokemonId, pokemonToDeploy.Cp, pokeGym.Id, response.Result) });
                    }
                }
                else
                {
                    session.EventDispatcher.Send(new WarnEvent() { Message = "NO DEPLOYABLE POKEMON FOUND !" });
                }
            }

            // TODO : FIGHT OTHER GYMS
            foreach (var pokeGym in pokeGymsList.Where(gym => gym.Enabled && gym.OwnedByTeam != 0).OrderBy(
                        i =>
                            LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                                session.Client.CurrentLongitude, i.Latitude, i.Longitude)))
            {
                var distance = LocationUtils.CalculateDistanceInMeters(session.Client.CurrentLatitude,
                    session.Client.CurrentLongitude, pokeGym.Latitude, pokeGym.Longitude);
                var pokeGymInfo = await session.Client.Fort.GetFort(pokeGym.Id, pokeGym.Latitude, pokeGym.Longitude);
                session.EventDispatcher.Send(new FortTargetEvent { Name = pokeGymInfo.Name, Distance = distance });
                if (ShouldAttackGym(session, pokeGymInfo))
                {
                    await session.Navigation.Move(new GeoCoordinate(pokeGym.Latitude, pokeGym.Longitude),
                    session.LogicSettings.WalkingSpeedInKilometerPerHour, null, cancellationToken, session.LogicSettings.DisableHumanWalking);
                    await AttackGym(session, pokeGymInfo, 10); // TODO: MAKE ATTACK TIMES CONFIGURABLE 
                }
                else
                {
                    session.EventDispatcher.Send(new WarnEvent() { Message = string.Format("GYM '{0}' SKIPPED", pokeGymInfo.Name) });
                }
            }

            // TODO: CATCH FREE GYMS IF ANY

        }

        private static async Task AttackGym(ISession session, FortDetailsResponse gymInfos, int maxTimes)
        {
            for (int i = 0; i < maxTimes; i++)
            {
                var attackResponse = await session.Client.Fort.StartGymBattle(gymInfos.FortId, 0, (await session.Inventory.GetHighestsCp(6)).Select(p => p.Id));
                if (attackResponse.Result == StartGymBattleResponse.Types.Result.Success)
                {
                    session.EventDispatcher.Send(new NoticeEvent() { Message = string.Format("SUCCESSFULLY STARTED GYM BATTLE") });
                }
                else
                {
                    session.EventDispatcher.Send(new WarnEvent() { Message = string.Format("FAILED TO START GYM BATTLE [{0}]",attackResponse.Result)});
                }
            }
        }

        private static bool ShouldAttackGym(ISession session, FortDetailsResponse gymInfos)
        {
            // ELLABORATE THE CALCULATION
            return true;
        }

        private static async Task<PokemonData> GetDeployablePokemon(ISession session)
        {
            //TODO: ADD A MIN CP LIMIT IN THE SETTINGS 
            return (await session.Inventory.GetHighestsCp(1)).FirstOrDefault();
        }

        private static async Task<List<FortData>> GetPokeGyms(ISession session)
        {
            var mapObjects = await session.Client.Map.GetMapObjects();

            // Wasn't sure how to make this pretty. Edit as needed.
            var pokeStops = mapObjects.Item1.MapCells.SelectMany(i => i.Forts)
                .Where(
                    i =>
                        i.Type == FortType.Gym &&
                        ( // Make sure PokeStop is within max travel distance, unless it's set to 0.
                            LocationUtils.CalculateDistanceInMeters(
                                session.Settings.DefaultLatitude, session.Settings.DefaultLongitude,
                                i.Latitude, i.Longitude) < session.LogicSettings.MaxTravelDistanceInMeters ||
                        session.LogicSettings.MaxTravelDistanceInMeters == 0)
                );

            return pokeStops.ToList();
        }

        private static async Task HealPokemons(ISession session)
        {
            var pokemonsToRevive = await session.Inventory.GetPokemonsToRevive();
            foreach (var pokemon in pokemonsToRevive)
            {
                var revives = (await session.Inventory.GetRevives()).ToList();
                if (revives.Count > 0)
                {
                    var response = await session.Client.Inventory.UseItemRevive(revives.First().ItemId, pokemon.Id);
                    if (response.Result == UseItemEggIncubatorResponse.Types.Result.Success)
                    {
                        session.EventDispatcher.Send(new NoticeEvent() { Message = string.Format("SUCCESSFULLY REVIVED POKEMON '{0}(CP:{1})'", pokemon.PokemonId, pokemon.Cp) });
                    }
                    else
                    {
                        session.EventDispatcher.Send(new ErrorEvent() { Message = string.Format("FAILED TO REVIVE POKEMON '{0}(CP:{1})' [{2}]", pokemon.PokemonId, pokemon.Cp, response.Result) });
                    }
                }
                else
                {
                    session.EventDispatcher.Send(new WarnEvent() { Message = "NO MORE REVIVES !" });
                }
            }

            List<POGOProtos.Data.PokemonData> pokemonsToHeal;
            do
            {
                pokemonsToHeal = (await session.Inventory.GetPokemonsToHeal()).ToList();
                foreach (var pokemon in pokemonsToHeal)
                {
                    var potions = (await session.Inventory.GetPotions()).ToList();
                    if (potions.Count > 0)
                    {
                        var response = await session.Client.Inventory.UseItemPotion(potions.First().ItemId, pokemon.Id);
                        if (response.Result == UseItemPotionResponse.Types.Result.Success)
                        {
                            session.EventDispatcher.Send(new NoticeEvent() { Message = string.Format("SUCCESSFULLY HEALED POKEMON '{0}(CP:{1})'", pokemon.PokemonId, pokemon.Cp) });
                        }
                        else
                        {
                            session.EventDispatcher.Send(new ErrorEvent() { Message = string.Format("FAILED TO HEAL POKEMON '{0}(CP:{1})' [{2}]", pokemon.PokemonId, pokemon.Cp, response.Result) });
                        }
                    }
                    else
                    {
                        session.EventDispatcher.Send(new WarnEvent() { Message = "NO MORE POTIONS !" });
                    }
                }
            } while (pokemonsToHeal.Count > 0);
        }
    }
}
