using HarmonyLib;
using UnityEngine;
using System.Collections.Generic;

namespace RetrieverUnit
{
    [HarmonyPatch(typeof(ItemDropship))]
    public static class DropshipMusicPatch
    {
        [HarmonyPatch("LandShipClientRpc")]
        [HarmonyPostfix]
        static void LandShipClientRpc_Postfix(ItemDropship __instance)
        {
            var field = typeof(ItemDropship).GetField(
                "itemsToDeliver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );
            if (field == null) return;

            var itemsToDeliver = field.GetValue(__instance) as List<int>;
            if (itemsToDeliver == null || itemsToDeliver.Count == 0) return;

            Terminal? terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (terminal == null) return;

            bool hasRetriever = false;
            foreach (int index in itemsToDeliver)
            {
                if (index < 0 || index >= terminal.buyableItemsList.Length) continue;
                if (terminal.buyableItemsList[index]?.itemName == Plugin.RETRIEVER_ITEM_NAME)
                { hasRetriever = true; break; }
            }

            if (!hasRetriever) return;

            // Ищем музыку прямо здесь
            if (Plugin.RetrieverDeliveryMusic == null)
            {
                AudioClip[] allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
                foreach (var clip in allClips)
                {
                    if (clip.name == "IcecreamTruckV2VehicleDeliveryVer")
                    {
                        Plugin.RetrieverDeliveryMusic = clip;
                        Plugin.Logger.LogInfo($"Found cruiser music: {clip.name}");
                        break;
                    }
                }
            }

            if (Plugin.RetrieverDeliveryMusic == null)
            {
                // Дебаг — выводим похожие клипы чтобы найти правильное имя
                AudioClip[] allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
                Plugin.Logger.LogWarning($"Music not found! Searching similar ({allClips.Length} total):");
                foreach (var clip in allClips)
                    if (clip.name.ToLower().Contains("truck") ||
                        clip.name.ToLower().Contains("cruiser") ||
                        clip.name.ToLower().Contains("icecream"))
                        Plugin.Logger.LogWarning($"  → {clip.name}");
                return;
            }

            // Логируем все AudioSource для дебага
            AudioSource[] sources = __instance.GetComponentsInChildren<AudioSource>(true);
            Plugin.Logger.LogInfo($"Dropship AudioSources ({sources.Length}):");
            foreach (var src in sources)
                Plugin.Logger.LogInfo($"  {src.name} | loop={src.loop} playing={src.isPlaying} clip={src.clip?.name}");

            // Заменяем музыку на лупящем источнике
            foreach (var src in sources)
            {
                if (src.isPlaying && src.loop)
                {
                    src.Stop();
                    src.clip = Plugin.RetrieverDeliveryMusic;
                    src.loop = true;
                    src.volume = 0.8f;
                    src.Play();
                    Plugin.Logger.LogInfo("Music replaced successfully!");
                    return;
                }
            }

            // Запасной вариант — PlayOneShot на первом источнике
            if (sources.Length > 0)
            {
                sources[0].PlayOneShot(Plugin.RetrieverDeliveryMusic, 0.8f);
                Plugin.Logger.LogInfo("Music via PlayOneShot.");
            }
        }
    }
}