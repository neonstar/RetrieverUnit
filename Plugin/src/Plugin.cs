using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LethalLib.Modules;
using RetrieverUnit.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RetrieverUnit
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger = null!;
        internal static PluginConfig BoundConfig { get; private set; } = null!;
        public static AssetBundle? ModAssets;
        public static AudioClip? RetrieverDeliveryMusic;
        public const string RETRIEVER_ITEM_NAME = "RetrieverUnit";


        private void Awake()
        {
            Logger = base.Logger;

            BoundConfig = new PluginConfig(base.Config);

            LoadAssetBundle();

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();  // ← PatchAll() без аргументов подхватит ВСЕ классы с [HarmonyPatch] в сборке
            if (ModAssets == null)
            {
                Logger.LogError("AssetBundle failed to load.");
                return;
            }

            InitializeNetworkBehaviours();

            // =========================
            // LOAD ENEMY TYPE
            // =========================
            EnemyType retrieverEnemy = ModAssets.LoadAsset<EnemyType>("RetrieverUnit");

            if (retrieverEnemy == null)
            {
                Logger.LogError("RetrieverEnemy NOT FOUND in AssetBundle!");
                return;
            }

            if (retrieverEnemy.enemyPrefab == null)
            {
                Logger.LogError("Enemy prefab is NULL!");
                return;
            }

            // =========================
            // REGISTER NETWORK PREFAB
            // =========================
            NetworkPrefabs.RegisterNetworkPrefab(retrieverEnemy.enemyPrefab);

            Logger.LogInfo("Enemy prefab registered.");

            // =========================
            // REGISTER ENEMY
            // =========================
            var levelRarities = new Dictionary<Levels.LevelTypes, int>
            {
                { Levels.LevelTypes.All, 0 }
            };

            Enemies.RegisterEnemy(
                retrieverEnemy,
                60,
                Levels.LevelTypes.All,        // явно указываем все уровни
                Enemies.SpawnType.Outside,    // ← ВАЖНО: спавн снаружи
                null,
                null
            );

            Logger.LogInfo("Retriever Enemy successfully registered!");

            // =========================
            // (ОПЦИОНАЛЬНО) регистрация предмета
            // =========================
            Item retrieverItem = ModAssets.LoadAsset<Item>("RetrieverItem");

            if (retrieverItem != null && retrieverItem.spawnPrefab != null)
            {
                NetworkPrefabs.RegisterNetworkPrefab(retrieverItem.spawnPrefab);

                TerminalNode buyNode = ModAssets.LoadAsset<TerminalNode>("BuyRetriever");

                if (buyNode != null)
                {
                    Items.RegisterShopItem(retrieverItem, null, null, buyNode, BoundConfig.ShopPrice.Value);

                    Logger.LogInfo("Shop item registered.");
                }
            }
        }

        private void LoadAssetBundle()
        {
            try
            {
                string bundleName = "modassets";
                string path = Path.Combine(Path.GetDirectoryName(Info.Location)!, bundleName);

                if (!File.Exists(path))
                {
                    Logger.LogError($"Bundle not found: {path}");
                    return;
                }

                ModAssets = AssetBundle.LoadFromFile(path);

                Logger.LogInfo("AssetBundle loaded.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.ToString());
            }
        }

        private static void InitializeNetworkBehaviours()
        {
            var types = Assembly.GetExecutingAssembly().GetTypes();

            foreach (var type in types)
            {
                var methods = type.GetMethods(
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static
                );

                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(
                        typeof(RuntimeInitializeOnLoadMethodAttribute),
                        false
                    );

                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }
        }
    }
}