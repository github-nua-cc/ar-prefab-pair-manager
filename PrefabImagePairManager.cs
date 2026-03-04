// PrefabImagePairManager.cs (Unity 6 / AR Foundation 6 ready)
// Author: rewritten from the user's original script for modern ARF 6 usage.
// Notes:
//  - Uses ARTrackedImageManager events for added/updated/removed
//  - Spawns one prefab per XRReferenceImage GUID and keeps it parented to the tracked image transform
//  - Toggles active state based on TrackingState to avoid destroy/recreate churn
//  - Preserves public API: GetPrefabForReferenceImage / SetPrefabForReferenceImage
//  - Keeps a serialized backing list for the GUID→Prefab map for inspector persistence

using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace UnityEngine.XR.ARFoundation.Samples
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ARTrackedImageManager))]
    public class PrefabImagePairManager : MonoBehaviour, ISerializationCallbackReceiver
    {
        // ---------- Serialized data for inspector persistence ----------

        [Serializable]
        struct NamedPrefab
        {
            // Keep Guid as string for Unity serialization
            public string imageGuid;
            public GameObject imagePrefab;

            public NamedPrefab(Guid guid, GameObject prefab)
            {
                imageGuid = guid.ToString();
                imagePrefab = prefab;
            }
        }

        // Backing list for Unity serialization
        [SerializeField, HideInInspector]
        private List<NamedPrefab> m_PrefabsList = new List<NamedPrefab>();

        // Runtime map: XRReferenceImage.Guid → Prefab
        private Dictionary<Guid, GameObject> m_PrefabsByGuid = new Dictionary<Guid, GameObject>();

        // Runtime instances: XRReferenceImage.Guid → Spawned instance
        private readonly Dictionary<Guid, GameObject> m_InstancesByGuid = new Dictionary<Guid, GameObject>();

        // ---------- Dependencies ----------

        private ARTrackedImageManager m_TrackedImageManager;

        [SerializeField, Tooltip("Reference Image Library used by ARTrackedImageManager")]
        private XRReferenceImageLibrary m_ImageLibrary;

        /// <summary>
        /// Reference to the XRReferenceImageLibrary (exposed in case you set it at runtime).
        /// </summary>
        public XRReferenceImageLibrary imageLibrary
        {
            get => m_ImageLibrary;
            set => m_ImageLibrary = value;
        }

        // ---------- Unity lifecycle ----------

        private void Awake()
        {
            m_TrackedImageManager = GetComponent<ARTrackedImageManager>();
        }

        private void OnEnable()
        {
            if (m_TrackedImageManager == null)
            {
                m_TrackedImageManager = GetComponent<ARTrackedImageManager>();
            }

            if (m_ImageLibrary != null && m_TrackedImageManager != null)
            {
                // Ensure the manager is pointing at the configured library
                m_TrackedImageManager.referenceLibrary = m_ImageLibrary;
            }

            m_TrackedImageManager.trackedImagesChanged += OnTrackedImagesChanged;
        }

        private void OnDisable()
        {
            if (m_TrackedImageManager != null)
            {
                m_TrackedImageManager.trackedImagesChanged -= OnTrackedImagesChanged;
            }
        }

        // ---------- Serialization bridging ----------

        public void OnBeforeSerialize()
        {
            // Push dictionary → list for serialization
            m_PrefabsList.Clear();
            foreach (var kv in m_PrefabsByGuid)
            {
                m_PrefabsList.Add(new NamedPrefab(kv.Key, kv.Value));
            }
        }

        public void OnAfterDeserialize()
        {
            // Pull list → dictionary for runtime use
            m_PrefabsByGuid = new Dictionary<Guid, GameObject>(m_PrefabsList.Count);
            foreach (var entry in m_PrefabsList)
            {
                if (!string.IsNullOrEmpty(entry.imageGuid) && Guid.TryParse(entry.imageGuid, out var guid))
                {
                    if (!m_PrefabsByGuid.ContainsKey(guid))
                        m_PrefabsByGuid.Add(guid, entry.imagePrefab);
                }
            }
        }

        // ---------- Public API retained ----------

        public GameObject GetPrefabForReferenceImage(in XRReferenceImage referenceImage) =>
            m_PrefabsByGuid.TryGetValue(referenceImage.guid, out var prefab) ? prefab : null;

        public void SetPrefabForReferenceImage(in XRReferenceImage referenceImage, GameObject alternativePrefab)
        {
            m_PrefabsByGuid[referenceImage.guid] = alternativePrefab;

            if (m_InstancesByGuid.TryGetValue(referenceImage.guid, out var existing))
            {
                // Swap the live instance without losing the parent/pose
                var parent = existing.transform.parent;
                var pose = existing.transform.localToWorldMatrix;
                Destroy(existing);

                var newInstance = InstantiateSafe(alternativePrefab, parent);
                if (newInstance != null)
                {
                    newInstance.transform.SetPositionAndRotation(parent.position, parent.rotation);
                    m_InstancesByGuid[referenceImage.guid] = newInstance;
                }
            }
        }

        // ---------- Event handling ----------

        private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs args)
        {
            // Added: create instances and bind to the tracked image transform
            foreach (var added in args.added)
            {
                EnsureInstanceFor(added);
                UpdateVisibility(added);
                UpdateTransform(added);
            }

            // Updated: toggle visibility & keep alignment
            foreach (var updated in args.updated)
            {
                EnsureInstanceFor(updated); // in case late-spawn after dictionary changes
                UpdateVisibility(updated);
                UpdateTransform(updated);
            }

            // Removed: clean up instances (optional: pool instead of destroy)
            foreach (var removed in args.removed)
            {
                DestroyInstanceFor(removed);
            }
        }

        // ---------- Helpers ----------

        private void EnsureInstanceFor(ARTrackedImage trackedImage)
        {
            var guid = trackedImage.referenceImage.guid;

            // Already created?
            if (m_InstancesByGuid.ContainsKey(guid) && m_InstancesByGuid[guid] != null)
                return;

            // Find prefab to spawn
            if (!m_PrefabsByGuid.TryGetValue(guid, out var prefab) || prefab == null)
                return;

            // Parent to tracked image so pose updates for free
            var instance = InstantiateSafe(prefab, trackedImage.transform);
            if (instance == null)
                return;

            // Optional: set an initial uniform scale relative to the detected image size
            // var minScalar = Mathf.Min(trackedImage.size.x, trackedImage.size.y) * 0.5f;
            // trackedImage.transform.localScale = Vector3.one * minScalar;

            m_InstancesByGuid[guid] = instance;
        }

        private void UpdateTransform(ARTrackedImage trackedImage)
        {
            // Because the instance is a child of the tracked image, normal pose updates
            // are handled automatically by ARFoundation. Still, you could enforce
            // alignment logic here if needed (e.g., offset, rotation lock).
            // Example:
            // var t = trackedImage.transform;
            // t.localPosition = Vector3.zero;
            // t.localRotation = Quaternion.identity;
        }

        private void UpdateVisibility(ARTrackedImage trackedImage)
        {
            var guid = trackedImage.referenceImage.guid;
            if (!m_InstancesByGuid.TryGetValue(guid, out var instance) || instance == null)
                return;

            // Toggle active state based on tracking quality
            var visible = trackedImage.trackingState == TrackingState.Tracking;
            if (instance.activeSelf != visible)
                instance.SetActive(visible);
        }

        private void DestroyInstanceFor(ARTrackedImage trackedImage)
        {
            var guid = trackedImage.referenceImage.guid;
            if (m_InstancesByGuid.TryGetValue(guid, out var instance) && instance != null)
            {
                Destroy(instance);
            }
            m_InstancesByGuid.Remove(guid);
        }

        private static GameObject InstantiateSafe(GameObject prefab, Transform parent)
        {
            if (prefab == null) return null;
            var go = Instantiate(prefab, parent);
            go.name = $"{prefab.name} (ImageInstance)";
            return go;
        }

#if UNITY_EDITOR
        // -------- Inspector: keeps mapping in sync with the selected library --------

        [CustomEditor(typeof(PrefabImagePairManager))]
        class PrefabImagePairManagerInspector : Editor
        {
            private List<XRReferenceImage> m_ReferenceImages = new();
            private bool m_ListExpanded = true;

            public override void OnInspectorGUI()
            {
                var behaviour = (PrefabImagePairManager)serializedObject.targetObject;
                serializedObject.Update();

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour(behaviour), typeof(MonoScript), false);
                }

                // Image Library field
                var libraryProp = serializedObject.FindProperty("m_ImageLibrary");
                EditorGUILayout.PropertyField(libraryProp);
                var library = libraryProp.objectReferenceValue as XRReferenceImageLibrary;

                // When library changes, refresh the dictionary with existing values where possible
                if (LibraryChanged(library))
                {
                    var newMap = new Dictionary<Guid, GameObject>();
                    if (library != null)
                    {
                        foreach (var ri in library)
                        {
                            newMap[ri.guid] = behaviour.GetPrefabForReferenceImage(ri);
                        }
                    }
                    behaviour.m_PrefabsByGuid = newMap;
                }

                // Cache current library contents
                m_ReferenceImages.Clear();
                if (library != null)
                {
                    foreach (var ri in library)
                        m_ReferenceImages.Add(ri);
                }

                // Prefab list UI
                if (library != null)
                {
                    m_ListExpanded = EditorGUILayout.Foldout(m_ListExpanded, "Prefab List");
                    if (m_ListExpanded)
                    {
                        using (new EditorGUI.IndentLevelScope())
                        {
                            EditorGUI.BeginChangeCheck();
                            var temp = new Dictionary<Guid, GameObject>(library.count);
                            foreach (var ri in library)
                            {
                                var current = behaviour.m_PrefabsByGuid.TryGetValue(ri.guid, out var v) ? v : null;
                                var next = (GameObject)EditorGUILayout.ObjectField(ri.name, current, typeof(GameObject), false);
                                temp[ri.guid] = next;
                            }

                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RecordObject(target, "Update Prefab Map");
                                behaviour.m_PrefabsByGuid = temp;
                                EditorUtility.SetDirty(target);
                            }
                        }
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Assign an XRReferenceImageLibrary to map images to prefabs.", MessageType.Info);
                }

                serializedObject.ApplyModifiedProperties();
            }

            private bool LibraryChanged(XRReferenceImageLibrary library)
            {
                if (library == null) return m_ReferenceImages.Count != 0;
                if (m_ReferenceImages.Count != library.count) return true;
                for (int i = 0; i < library.count; i++)
                {
                    if (m_ReferenceImages[i] != library[i])
                        return true;
                }
                return false;
            }
        }
#endif
    }
}