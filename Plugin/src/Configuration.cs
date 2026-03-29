using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace RetrieverUnit.Configuration
{
    public class PluginConfig
    {
        public ConfigEntry<int> SpawnWeight;
        public ConfigEntry<int> ShopPrice;
        public ConfigEntry<float> MoveSpeed;
        public ConfigEntry<float> TurnSpeed;

        public PluginConfig(ConfigFile cfg)
        {
            ShopPrice = cfg.Bind(
                "Shop",
                "Price",
                60,
                "Price of Retriever Unit in the shop"
            );

            MoveSpeed = cfg.Bind(
                "Unit",
                "MoveSpeed",
                10f,
                "Movement speed of the Retriever Unit"
            );
        }
    }
}