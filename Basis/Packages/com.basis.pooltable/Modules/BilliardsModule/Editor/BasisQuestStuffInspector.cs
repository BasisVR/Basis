using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(quest_stuff))]
public class BasisQuestStuffInspector : Editor
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
