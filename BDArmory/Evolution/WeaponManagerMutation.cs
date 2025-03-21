﻿using System;
using System.Collections.Generic;
using UnityEngine;

namespace BDArmory.Evolution
{
    public class WeaponManagerMutation : VariantMutation
    {
        const string moduleName = "MissileFire";

        public string paramName;
        public float value;
        public string key;
        public int direction;
        private List<MutatedPart> mutatedParts = new List<MutatedPart>();
        public WeaponManagerMutation(string paramName, float value, string key, int direction)
        {
            this.paramName = paramName;
            this.value = value;
            this.key = key;
            this.direction = direction;
        }

        public ConfigNode Apply(ConfigNode craft, VariantEngine engine)
        {
            ConfigNode mutatedCraft = craft.CreateCopy();
            List<ConfigNode> matchingNodes = engine.FindModuleNodes(mutatedCraft, moduleName);
            if( matchingNodes.Count == 1 )
            {
                var node = matchingNodes[0];
                float existingValue;
                float.TryParse(node.GetValue(paramName), out existingValue);
                if( engine.MutateNode(node, paramName, value) )
                {
                    ConfigNode partNode = engine.FindParentPart(mutatedCraft, node);
                    string partName = partNode.GetValue("part");
                    mutatedParts.Add(new MutatedPart(partName, moduleName, paramName, existingValue, value));
                }
            }
            else
            {
                Debug.Log("[BDArmory.WeaponManagerMutation]: Evolution WeaponManagerMutation wrong number of weapon managers");
            }
            return mutatedCraft;
        }

        public Variant GetVariant(string id, string name)
        {
            return new Variant(id, name, mutatedParts, key, direction);
        }
    }
}
