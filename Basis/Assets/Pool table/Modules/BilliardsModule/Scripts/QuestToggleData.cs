using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[System.Serializable]
public class mat_replacement
{
   [SerializeField] public string name;
   [SerializeField] public bool shown;

   [SerializeField] public Shader shader_default;
   [SerializeField] public Shader shader_quest;

   [SerializeField] public List<Material> materials;
}

[System.Serializable]
public class obj_toggly
{
   [SerializeField] public string name;
   [SerializeField] public bool shown;

   [SerializeField] public bool pc;
   [SerializeField] public bool quest;

   [SerializeField] public List<GameObject> objs;
}

public enum EQuestStuffUI
{
   k_EQuestStuffUI_noaction,
   k_EQuestStuffUI_set_pc,
   k_EQuestStuffUI_set_quest
};

[System.Serializable]
public struct quest_stuff_data
{
   [SerializeField] public List<mat_replacement> replacements;
   [SerializeField] public List<obj_toggly> objs;
}
