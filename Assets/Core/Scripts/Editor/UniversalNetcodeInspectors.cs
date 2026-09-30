using System;
using UnityEditor;
using System.Linq;
using UnityEngine;
using System.Reflection;
using System.Collections.Generic;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Shared utility logic for the custom inspectors in this assembly (CoreMovementEditor, CorePlayerManagerEditor).
    /// Draws properties organised by inheritance hierarchy with collapsible sections, plus a small live
    /// network-state header for Photon Fusion behaviours.
    ///
    /// This file used to also hold "universal" inspectors for every Netcode for GameObjects NetworkBehaviour,
    /// NetworkTransform and NetworkAnimator. Those were removed with the move to Photon Fusion - Fusion ships
    /// its own NetworkBehaviour inspector, which also shows [Networked] state in play mode.
    /// </summary>
    public static class UniversalEditorSharedLogic
    {
        #region Fields & Properties

        private static GUIContent s_NetworkSettingsIcon;
        private static GUIContent s_ComponentSettingsIcon;
        private static GUIContent s_AdvancedSettingsIcon;

        #endregion

        #region Public Methods

        /// <summary>
        /// Draws a help box with the target's type name and, while playing, its Fusion network state
        /// (spawned, object id, state authority).
        /// </summary>
        public static void DrawNetworkHeader(UnityEngine.Object target)
        {
            EditorGUILayout.BeginVertical("helpBox");
            EditorGUILayout.LabelField(target.GetType().Name, EditorStyles.boldLabel);

            if (Application.isPlaying && target is CoreNetworkBehaviour behaviour && behaviour.IsSpawned && behaviour.Object != null)
            {
                EditorGUILayout.LabelField($"Network Object ID: {behaviour.Object.Id}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"State Authority: Player {behaviour.OwnerClientId}", EditorStyles.miniLabel);

                if (behaviour.IsOwner)
                {
                    EditorGUILayout.LabelField("Has State Authority: Yes", EditorStyles.miniLabel);
                }

                var runner = behaviour.Runner;
                if (runner != null && runner.IsSharedModeMasterClient)
                {
                    EditorGUILayout.LabelField("Is Master Client (Host): Yes", EditorStyles.miniLabel);
                }
            }
            else
            {
                EditorGUILayout.LabelField("Network Object ID: Not Spawned", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        /// <summary>
        /// Draws properties for each class in the inheritance hierarchy between the most derived type
        /// and the given base type (exclusive), organised in collapsible sections.
        /// </summary>
        /// <param name="serializedObject">The SerializedObject to draw properties from.</param>
        /// <param name="mostDerivedType">The most derived type in the hierarchy.</param>
        /// <param name="stopAtBaseType">The base type at which to stop (its fields are not drawn).</param>
        public static void DrawDerivedProperties(SerializedObject serializedObject, Type mostDerivedType,
            Type stopAtBaseType)
        {
            InitializeIcons();

            // Build list of types in inheritance chain, excluding the base type
            var typesToDrawInHierarchy = new List<Type>();
            Type currentTypeIterator = mostDerivedType;
            while (currentTypeIterator != null && currentTypeIterator != stopAtBaseType &&
                   stopAtBaseType.IsAssignableFrom(currentTypeIterator.BaseType))
            {
                typesToDrawInHierarchy.Add(currentTypeIterator);
                currentTypeIterator = currentTypeIterator.BaseType;
            }

            // Reverse to display base classes first, then derived classes
            typesToDrawInHierarchy.Reverse();

            foreach (Type typeToDraw in typesToDrawInHierarchy)
            {
                if (!HasDrawableFields(serializedObject, typeToDraw))
                {
                    continue;
                }

                // Persist foldout state across Unity sessions using SessionState
                string foldoutKey = $"UniversalEditor_{mostDerivedType.FullName}_{typeToDraw.Name}_Foldout";
                bool isExpanded = SessionState.GetBool(foldoutKey, true);

                GUIContent headerContent = GetHeaderContentForType(typeToDraw);
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                bool newIsExpanded = EditorGUILayout.Foldout(isExpanded, headerContent, true, EditorStyles.foldoutHeader);
                EditorGUILayout.EndHorizontal();

                if (newIsExpanded)
                {
                    EditorGUILayout.Space(2);
                    EditorGUI.indentLevel++;
                    EditorHelper.DrawDeclaredProperties(serializedObject, typeToDraw);
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space(2);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);

                if (newIsExpanded != isExpanded)
                {
                    SessionState.SetBool(foldoutKey, newIsExpanded);
                }
            }
        }

        /// <summary>Draws the (read-only) "Script" field.</summary>
        public static void DrawScriptField(SerializedObject serializedObject)
        {
            SerializedProperty scriptProp = serializedObject.FindProperty("m_Script");
            if (scriptProp == null) return;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(scriptProp);
            }
        }

        #endregion

        #region Private Methods

        private static void InitializeIcons()
        {
            if (s_NetworkSettingsIcon == null)
            {
                s_NetworkSettingsIcon = EditorGUIUtility.IconContent("NetworkAnimator Icon");
                s_ComponentSettingsIcon = EditorGUIUtility.IconContent("FilterByType");
                s_AdvancedSettingsIcon = EditorGUIUtility.IconContent("Settings");
            }
        }

        private static bool HasDrawableFields(SerializedObject serializedObject, Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Instance | BindingFlags.DeclaredOnly);

            return fields.Any(field =>
                (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null) &&
                field.GetCustomAttribute<NonSerializedAttribute>() == null &&
                field.GetCustomAttribute<HideInInspector>() == null &&
                field.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() == null &&
                serializedObject.FindProperty(field.Name) != null
            );
        }

        private static GUIContent GetHeaderContentForType(Type type)
        {
            // Choose icon based on type name to visually categorize different component types
            Texture icon = type.Name.Contains("Network") ? s_NetworkSettingsIcon?.image
                : type.Name.Contains("Manager") ? s_AdvancedSettingsIcon?.image
                : s_ComponentSettingsIcon?.image;

            return new GUIContent($" {type.Name} Settings", icon);
        }

        #endregion
    }
}
