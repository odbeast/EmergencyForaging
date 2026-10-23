using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EmergencyForaging
{
    public sealed class Settings : ModSettings
    {
        public float radius = 30f;
        public float hunger = 0.20f;
        public override void ExposeData()
        {
            Scribe_Values.Look(ref radius, "radius", 30f);
            Scribe_Values.Look(ref hunger, "hunger", 0.20f);
            radius = Mathf.Clamp(radius, 5f, 100f);
            hunger = Mathf.Clamp(hunger, 0.05f, 0.40f);
        }
    }

    public sealed class ForagingMod : Mod
    {
        public static Settings Config;
        public ForagingMod(ModContentPack content) : base(content)
        {
            Config = GetSettings<Settings>();
            new Harmony("odbeast.emergencyforaging").PatchAll();
        }
        public override string SettingsCategory() => "Emergency Foraging";
        public override void DoSettingsWindowContents(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);
            list.Label("Search radius: " + Config.radius.ToString("F0") + " tiles");
            Config.radius = Mathf.Round(list.Slider(Config.radius, 5, 100));
            list.Label("Food level threshold: " + Config.hunger.ToString("P0"));
            Config.hunger = list.Slider(Config.hunger, 0.05f, 0.40f);
            list.Label("Only fully grown plants. Raw crops are allowed. Food restrictions are respected.");
            list.End();
        }
    }

    internal static class Search
    {
        // Keyed by actual Pawn objects; no cross-save static cache.
        public static bool Eligible(Pawn p) => p != null && p.Spawned && p.IsColonistPlayerControlled
            && !p.Drafted && !p.Downed && !p.Dead && !p.InMentalState
            && p.needs.food != null && p.needs.food.CurLevelPercentage <= ForagingMod.Config.hunger
            && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);

        public static bool EdibleCrop(Pawn p, ThingDef d)
        {
            if (d == null || !d.IsNutritionGivingIngestible || d.IsDrug || d.IsCorpse || d.IsMeat)
                return false;
            // Require a plant food category: excludes eggs, milk, meat and modded animal outputs.
            if ((d.ingestible.foodType & FoodTypeFlags.VegetableOrFruit) == 0)
                return false;
            // Use the game decision (including Harmony patches from food-policy mods).
            // Reading CurrentFoodRestriction.filter directly can bypass those patches.
            return p.RaceProps.CanEverEat(d) && p.WillEat_NewTemp(d);
        }

        public static bool ValidPlant(Pawn p, Plant plant)
        {
            if (plant == null || !plant.Spawned || plant.Destroyed || plant.LifeStage != PlantLifeStage.Mature
                || !plant.HarvestableNow || plant.YieldNow() <= 0 || plant.IsBurning()
                || !EdibleCrop(p, plant.def.plant.harvestedThingDef)) return false;
            if (plant.IsForbidden(p)) return false;
            var area = p.playerSettings == null ? null : p.playerSettings.AreaRestriction;
            return area == null || area[plant.Position];
        }

        public static bool Candidate(Pawn p, Plant plant)
        {
            return plant != null && plant.Position.DistanceToSquared(p.Position) <= ForagingMod.Config.radius * ForagingMod.Config.radius
                && ValidPlant(p, plant)
                && p.CanReserveAndReach(plant, PathEndMode.Touch, Danger.Some);
        }

        public static Plant FindPlant(Pawn p)
        {
            Plant best = null;
            float distance = float.MaxValue;
            foreach (Thing t in p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
            {
                var plant = t as Plant;
                if (plant == null) continue;
                float d = plant.Position.DistanceToSquared(p.Position);
                if (d >= distance || !Candidate(p, plant)) continue;
                best = plant; distance = d;
            }
            return best;
        }

        public static string Diagnose(Pawn p)
        {
            var nearby = p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
                .OfType<Plant>()
                .Where(t => t.Spawned && t.def.plant.harvestedThingDef != null
                    && t.def.plant.harvestedThingDef.IsNutritionGivingIngestible
                    && t.Position.DistanceToSquared(p.Position) <= ForagingMod.Config.radius * ForagingMod.Config.radius)
                .OrderBy(t => t.Position.DistanceToSquared(p.Position)).Take(3).ToList();
            if (nearby.Count == 0) return "  No food-producing plants within the search radius.";
            var lines = new List<string>();
            foreach (Plant plant in nearby)
            {
                ThingDef product = plant.def.plant.harvestedThingDef;
                var reasons = new List<string>();
                if (plant.LifeStage != PlantLifeStage.Mature) reasons.Add("not fully grown");
                if (!plant.HarvestableNow || plant.YieldNow() <= 0) reasons.Add("no harvest available");
                if (plant.IsBurning()) reasons.Add("burning");
                if (product.IsDrug || product.IsCorpse || product.IsMeat
                    || (product.ingestible.foodType & FoodTypeFlags.VegetableOrFruit) == 0)
                    reasons.Add("not eligible plant food");
                if (!p.RaceProps.CanEverEat(product))
                    reasons.Add("not edible for this pawn species");
                else if (!p.WillEat_NewTemp(product))
                    reasons.Add("game food-acceptance check rejected this food (policy, title or another mod)");
                var area = p.playerSettings == null ? null : p.playerSettings.AreaRestriction;
                if (area != null && !area[plant.Position]) reasons.Add("outside allowed area");
                if (plant.IsForbidden(p)) reasons.Add("forbidden to pawn");
                if (!p.CanReserveAndReach(plant, PathEndMode.Touch, Danger.Some))
                    reasons.Add("unreachable, unsafe or reserved");
                lines.Add("  " + plant.LabelCap + " (" + plant.Growth.ToString("P2")
                    + ", " + Math.Sqrt(plant.Position.DistanceToSquared(p.Position)).ToString("F0")
                    + " tiles): " + (reasons.Count == 0 ? "available; waiting for next food check" : string.Join(", ", reasons)));
            }
            return string.Join("\n", lines);
        }

        public static bool ReadyFood(Pawn p)
        {
            Thing food;
            ThingDef foodDef;
            // Use the game's food search, including inventory and paste dispensers.
            return FoodUtility.TryFindBestFoodSourceFor_NewTemp(p, p,
                p.needs.food.CurCategory == HungerCategory.Starving,
                out food, out foodDef, canRefillDispenser: true,
                allowForbidden: false, allowCorpse: false, allowHarvest: false)
                && !(food is Plant);
        }
    }

    public sealed class ForagingState : MapComponent
    {
        internal readonly HashSet<Pawn> Missing = new HashSet<Pawn>();
        public ForagingState(Map map) : base(map) { }
        // Transient diagnostic state: refreshed by food job requests, not saved.
        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            foreach (Pawn p in Missing.ToList())
                if (!Search.Eligible(p) || p.Map != map
                    || Search.ReadyFood(p) || Search.FindPlant(p) != null)
                    Missing.Remove(p);
        }
    }

    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    internal static class FoodPatch
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!Search.Eligible(pawn)) return;
            var state = pawn.Map.GetComponent<ForagingState>();
            // Normal food jobs win. Do not alter global meat/meal diet rules.
            // A plant-target job is replaced by the stricter mature-plant harvesting path.
            if (__result != null && !(__result.targetA.Thing is Plant))
            { state.Missing.Remove(pawn); return; }
            Plant plant = Search.FindPlant(pawn);
            if (plant == null)
            {
                __result = null;
                state.Missing.Add(pawn);
                return;
            }
            state.Missing.Remove(pawn);
            __result = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("EF_Forage"), plant);
        }
    }

    // Reuse vanilla harvesting: work, XP, yield/failure, sounds, records and quest hooks.
    public sealed class JobDriver_Forage : JobDriver_PlantHarvest
    {
        private ThingDef harvestProduct;
        private bool mealQueued;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref harvestProduct, "harvestProduct");
            Scribe_Values.Look(ref mealQueued, "mealQueued", false);
        }

        protected override void Init()
        {
            base.Init();
            if (harvestProduct == null && job.targetA.Thing is Plant plant)
                harvestProduct = plant.def.plant.harvestedThingDef;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => pawn.Drafted || pawn.Downed || pawn.InMentalState
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation));
            foreach (Toil toil in base.MakeNewToils())
            {
                // Validate before vanilla harvest work, not after it destroys/resets the plant.
                // Vanilla owns navigation, reservations and its own target-queue loop.
                if (toil.tickAction != null)
                {
                    Action vanillaTick = toil.tickAction;
                    toil.tickAction = () =>
                    {
                        if (!(job.targetA.Thing is Plant plant) || !Search.ValidPlant(pawn, plant))
                        { EndJobWith(JobCondition.Incompletable); return; }
                        vanillaTick();
                    };
                }
                yield return toil;
            }
        }

        protected override Toil PlantWorkDoneToil()
        {
            Toil toil = base.PlantWorkDoneToil();
            Action vanillaDone = toil.initAction;
            toil.initAction = () =>
            {
                vanillaDone?.Invoke();
                if (mealQueued || harvestProduct == null || !Search.EdibleCrop(pawn, harvestProduct)) return;
                // Vanilla places the yield near the worker, possibly merging into an existing stack.
                Thing food = GenRadial.RadialDistinctThingsAround(pawn.Position, Map, 3f, true)
                    .Where(t => t.def == harvestProduct && t.IngestibleNow && !t.IsForbidden(pawn)
                        && (pawn.playerSettings.AreaRestriction == null || pawn.playerSettings.AreaRestriction[t.Position])
                        && pawn.CanReserveAndReach(t, PathEndMode.Touch, Danger.Some))
                    .OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).FirstOrDefault();
                if (food == null) return; // Failed harvest: let the next hunger check try another plant.
                float nutrition = FoodUtility.GetNutrition(pawn, food, food.def);
                Job eat = JobMaker.MakeJob(JobDefOf.Ingest, food);
                eat.count = Math.Min(food.stackCount, Math.Max(1,
                    FoodUtility.WillIngestStackCountOf(pawn, food.def, nutrition)));
                pawn.jobs.jobQueue.EnqueueFirst(eat);
                mealQueued = true;
            };
            return toil;
        }
    }

    public sealed class Alert_NoForage : Alert
    {
        public Alert_NoForage()
        {
            defaultLabel = "No forageable food nearby";
            defaultPriority = AlertPriority.High;
        }
        private List<Pawn> Affected() => Find.Maps
            .SelectMany(m => m.GetComponent<ForagingState>().Missing)
            .Where(Search.Eligible).ToList();
        public override TaggedString GetExplanation()
        {
            return "These hungry colonists could not find suitable, reachable, fully grown food plants within "
                + ForagingMod.Config.radius.ToString("F0") + " tiles. Check allowed areas, reservations and food restrictions.\n\n"
                + string.Join("\n", Affected().Select(p => p.LabelShortCap.ToString() + "\n" + Search.Diagnose(p)));
        }
        public override AlertReport GetReport() => AlertReport.CulpritsAre(Affected().Cast<Thing>().ToList());
    }
}
