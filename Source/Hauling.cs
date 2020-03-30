﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace JobsOfOpportunity
{
    partial class JobsOfOpportunity
    {
        static class Hauling
        {
            public enum HaulProximities { Ignored, Either, Both, EitherThenIgnored, BothThenEither, BothThenEitherThenIgnored }

            static readonly Dictionary<Thing, ProximityStage> thingProximityStage = new Dictionary<Thing, ProximityStage>();
            static readonly Dictionary<Thing, IntVec3> cachedStoreCell = new Dictionary<Thing, IntVec3>();

            public static Job TryHaul(Pawn pawn, IntVec3 jobCell) {
                thingProximityStage.Clear();
                cachedStoreCell.Clear();

                switch (haulProximities.Value) {
                    case HaulProximities.Ignored:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Ignored);
                    case HaulProximities.Either:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Either);
                    case HaulProximities.Both:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Both);
                    case HaulProximities.EitherThenIgnored:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Either) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Ignored);
                    case HaulProximities.BothThenEither:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either);
                    case HaulProximities.BothThenEitherThenIgnored:
                        return TryHaulStage(pawn, jobCell, ProximityCheck.Both) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Either) ?? TryHaulStage(pawn, jobCell, ProximityCheck.Ignored);
                    default:
                        return null;
                }
            }

            static ProximityStage CanHaul(ProximityStage proximityStage, Pawn pawn, Thing thing, IntVec3 jobCell, ProximityCheck proximityCheck, out IntVec3 storeCell) {
                storeCell = IntVec3.Invalid;
                var pawnToJob = pawn.Position.DistanceTo(jobCell);

                var pawnToThing = pawn.Position.DistanceTo(thing.Position);
                if (proximityStage < ProximityStage.StoreToJob) {
                    var atMax = maxStartToThing.Value > 0 && pawnToThing > maxStartToThing.Value;
                    var atMaxPct = maxStartToThingPctOrigTrip.Value > 0 && pawnToThing > pawnToJob * maxStartToThingPctOrigTrip.Value;
                    var pawnToThingFail = atMax || atMaxPct;
                    switch (proximityCheck) {
                        case ProximityCheck.Both when pawnToThingFail: return ProximityStage.PawnToThing;
                    }

                    var thingToJob = thing.Position.DistanceTo(jobCell);
                    // if this one exceeds the maximum the next maxTotalTripPctOrigTrip check certainly will
                    if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) return ProximityStage.Fail;
                    if (pawn.Map.reservationManager.FirstRespectedReserver(thing, pawn) != null) return ProximityStage.Fail;
                    if (thing.IsForbidden(pawn)) return ProximityStage.Fail;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false)) return ProximityStage.Fail;
                }

                // we need storeCell everywhere, so cache it
                if (!cachedStoreCell.TryGetValue(thing, out storeCell)) {
                    var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out storeCell)) return ProximityStage.Fail;
                }

                cachedStoreCell.SetOrAdd(thing, storeCell);

                var storeToJob = storeCell.DistanceTo(jobCell);
                var atMax2 = maxStoreToJob.Value > 0 && storeToJob > maxStoreToJob.Value;
                var atMaxPct2 = maxStoreToJobPctOrigTrip.Value > 0 && storeToJob > pawnToJob * maxStoreToJobPctOrigTrip.Value;
                var storeToJobFail = atMax2 || atMaxPct2;
                switch (proximityCheck) {
                    case ProximityCheck.Both when storeToJobFail: return ProximityStage.StoreToJob;
                    case ProximityCheck.Either when proximityStage == ProximityStage.PawnToThing && storeToJobFail: return ProximityStage.StoreToJob;
                }

                var thingToStore = thing.Position.DistanceTo(storeCell);
                if (maxTotalTripPctOrigTrip.Value > 0 && pawnToThing + thingToStore + storeToJob > pawnToJob * maxTotalTripPctOrigTrip.Value) return ProximityStage.Fail;
                if (maxNewLegsPctOrigTrip.Value > 0 && pawnToThing + storeToJob > pawnToJob * maxNewLegsPctOrigTrip.Value) return ProximityStage.Fail;
                if (!pawn.Position.WithinRegions(thing.Position, pawn.Map, 25, TraverseParms.For(pawn))) return ProximityStage.Fail;
                if (!storeCell.WithinRegions(jobCell, pawn.Map, 25, TraverseParms.For(pawn))) return ProximityStage.Fail;
                return ProximityStage.Success;
            }

            static Job TryHaulStage(Pawn pawn, IntVec3 jobCell, ProximityCheck proximityCheck) {
                foreach (var thing in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()) {
                    if (thingProximityStage.TryGetValue(thing, out var proximityStage) && proximityStage == ProximityStage.Fail)
                        continue;

                    var newProximityStage = CanHaul(proximityStage, pawn, thing, jobCell, proximityCheck, out var storeCell);
                    Debug.WriteLine($"{pawn} for {thing} proximity stage: {proximityStage} -> {newProximityStage}");
                    thingProximityStage.SetOrAdd(thing, newProximityStage);
                    if (newProximityStage != ProximityStage.Success) continue;

                    if (DebugViewSettings.drawOpportunisticJobs) {
                        Log.Message("Opportunistic job spawned");
                        pawn.Map.debugDrawer.FlashLine(pawn.Position, thing.Position, 600, SimpleColor.Red);
                        pawn.Map.debugDrawer.FlashLine(thing.Position, storeCell, 600, SimpleColor.Green);
                        pawn.Map.debugDrawer.FlashLine(storeCell, jobCell, 600, SimpleColor.Blue);
                    }

                    Job puahJob = null;
                    if (haulToInventory.Value && puahWorkGiver != null) {
                        if (AccessTools.Method(PuahWorkGiver_HaulToInventory_Type, "JobOnThing") is MethodInfo method)
                            puahJob = (Job) method.Invoke(puahWorkGiver, new object[] {pawn, thing, false});
                    }

                    return puahJob ?? HaulAIUtility.HaulToCellStorageJob(pawn, thing, storeCell, false);
                }

                return null;
            }

            enum ProximityCheck { Both, Either, Ignored }

            enum ProximityStage { Initial, PawnToThing, StoreToJob, Fail, Success }
        }
    }
}
