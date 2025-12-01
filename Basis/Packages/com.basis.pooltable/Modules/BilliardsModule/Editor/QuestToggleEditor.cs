using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(quest_stuff))]
public class quest_stuff_inspector : Editor
{
   public override void OnInspectorGUI()
   {
      quest_stuff qst = (quest_stuff)target;

      quest_stuff.DrawQuestStuffGUI(ref qst.data);

      if (GUI.changed)
      {
         serializedObject.ApplyModifiedProperties();
         EditorUtility.SetDirty(qst);
      }

   }
}
