using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BoplFixedMath;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Unity.Services.Authentication.Internal;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbilityRoundRobin
{
    [BepInPlugin("com.maxgamertyper1.abilityroundrobin", "Ability Round Robin", "1.0.0")]
    [BepInIncompatibility("com.unluckycrafter.abilitystorm")]
    public class AbilityRoundRobin : BaseUnityPlugin
    {
        internal static ConfigFile config;
        public void Log(string message)
        {
            Logger.LogInfo(message);
        }

        private void Awake()
        {
            // Plugin startup logic
            Patches.PluginReference = this;
            Log($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            config = ((BaseUnityPlugin)this).Config;
            Patches.StartingAbilityRotationIndex = config.Bind<int>("Universal Config", "Pickup Index", 0, "The slot where the first ability pickup will happen (0 is the left-most ability, 1 is the middle ability, 2 is the right-most ability)").Value;
            Patches.DoingRoundRobin = config.Bind<bool>("Round-Robin", "Round Robin Enabled?", true, "Whether or not the Round Robin is enabled, if not it will use the 'Pickup Index' as the slot; if so, it will use the pickup index as the starting index for the round robin").Value;

            Log("Round-Robin Enabled? "+Patches.DoingRoundRobin.ToString());
            Log("UnchangedStartingIndex? "+Patches.StartingAbilityRotationIndex.ToString());

            DoPatching();
        }

        private void DoPatching()
        {
            var harmony = new Harmony("com.maxgamertyper1.abilityroundrobin");

            Patch(harmony, typeof(GameSessionHandler), "StartSpawnPlayersRoutine", "StartMatch", true, false);
            Patch(harmony, typeof(SlimeController), "AddAdditionalAbility", "PlayerAdditionalAbilityPickup", false, true);
        }

        private void Patch(Harmony harmony, Type OriginalClass , string OriginalMethod, string PatchMethod, bool prefix, bool transpiler)
        {
            MethodInfo MethodToPatch = AccessTools.Method(OriginalClass, OriginalMethod);
            MethodInfo Patch = AccessTools.Method(typeof(Patches), PatchMethod);
            
            if (prefix)
            {
                harmony.Patch(MethodToPatch, new HarmonyMethod(Patch));
            }
            else
            {
                if (transpiler)
                {
                    harmony.Patch(MethodToPatch, null, null, new HarmonyMethod(Patch));
                } else
                {
                    harmony.Patch(MethodToPatch, null, new HarmonyMethod(Patch));
                }
            }
            Log($"Patched {OriginalMethod} in {OriginalClass.ToString()}");
        }
    }

    public class Patches
    {
        public static int StartingAbilityRotationIndex;
        public static bool DoingRoundRobin;
        public static Dictionary<int, int> PlayerAbilityIndex = new Dictionary<int, int>();
        public static AbilityRoundRobin PluginReference;
        public static readonly Dictionary<int, int>  LeftToRightHashMap3Abilities = new Dictionary<int,int> 
            {
                {0,0},
                {1,2},
                {2,1}
            };

        public static void StartMatch(GameSessionHandler __instance)
        {
            List<Player> Playercount = PlayerHandler.Get().PlayerList();
            PlayerAbilityIndex.Clear();
            foreach (Player player in Playercount) {
                PlayerAbilityIndex.Add(player.Id, StartingAbilityRotationIndex);
            }
        }

        public static int GetPlayerAbilityIndex(int PlayerId)
        {
            int PlayersCurrentAbilityIndex = StartingAbilityRotationIndex;
            PlayerAbilityIndex.TryGetValue(PlayerId, out PlayersCurrentAbilityIndex);
            return LeftToRightHashMap3Abilities[PlayersCurrentAbilityIndex];
        }

        public static void IncrementPlayerAbilityIndex(int PlayerId)
        {
            float PlayersCurrentAbilityIndex = StartingAbilityRotationIndex;
            if (PlayerAbilityIndex[PlayerId] >= 2)
            {
                PlayerAbilityIndex[PlayerId] = -1;
            }
            PlayerAbilityIndex[PlayerId]++;
        }

        public static IEnumerable<CodeInstruction> PlayerAdditionalAbilityPickup(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo playerNumberField = AccessTools.Field(typeof(SlimeController), "playerNumber");
            MethodInfo GetPlayerAbilityIndexMethod = AccessTools.Method(typeof(Patches), nameof(GetPlayerAbilityIndex));
            MethodInfo IncrementPlayerAbilityIndexMehtod = AccessTools.Method(typeof(Patches), nameof(IncrementPlayerAbilityIndex));
            FieldInfo abilitiesField = AccessTools.Field(typeof(SlimeController), "abilities");

            bool? abilitiesEqualsThreeBranch = null; // for when it is in ideal branch
            bool finished = false;

            foreach (var instr in instructions)
            {
                if (finished)
                {
                    yield return instr;
                    continue;
                }

                if ((instr.opcode == OpCodes.Bne_Un_S || instr.opcode == OpCodes.Bne_Un))
                {
                    abilitiesEqualsThreeBranch = true;
                }
                if (instr.opcode == OpCodes.Br)
                {
                    if (abilitiesEqualsThreeBranch == true)
                    {
                        if (DoingRoundRobin)
                        {
                            PluginReference.Log("Adding Ability Index Updating Function");
                            yield return new CodeInstruction(OpCodes.Ldarg_0); // load the SlimeController, stack index 0 since its a non-static function
                            yield return new CodeInstruction(OpCodes.Ldfld, playerNumberField); // load the field playernumber, which equals the playerId
                            yield return new CodeInstruction(OpCodes.Call, IncrementPlayerAbilityIndexMehtod); // call the method and increment the index, clears the stack
                        }
                        abilitiesEqualsThreeBranch = false;
                        finished = true;
                    }
                }


                if (abilitiesEqualsThreeBranch == true && instr.opcode == OpCodes.Ldc_I4_2) // Ability amount is 3 and the ILCode is loading the constatn 2
                {
                    
                    if (DoingRoundRobin)
                    {
                        PluginReference.Log("Overriding Constants With Function call in 3-ability-Branch");
                        /* load __instance onto stack index 1/ the SlimeController
                        *  get the player id by loading the field playernumber
                        * call my function
                        */
                        yield return new CodeInstruction(OpCodes.Ldarg_0); // load the SlimeController, stack index 0 since its a non-static function
                        yield return new CodeInstruction(OpCodes.Ldfld, playerNumberField); // load the field playernumber, which equals the playerId
                        yield return new CodeInstruction(OpCodes.Call, GetPlayerAbilityIndexMethod); // call the method and get the index which will then be pushed to the stack *pops the player number and true*
                    } else
                    {
                        PluginReference.Log("Overriding Constants With another constant in 3-ability-Branch");
                        yield return new CodeInstruction(OpCodes.Ldc_I4, LeftToRightHashMap3Abilities[StartingAbilityRotationIndex]); // replace constant with a load to the starting index
                    }

                        continue;
                }
                yield return instr;
            }
        }
    }
}
