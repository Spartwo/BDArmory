using UnityEngine;

namespace BDArmory.Utils
{
    /// <summary>
    /// Helpers for scaling pooled effect prefabs (e.g., missile exhaust prefabs).
    /// </summary>
    public static class FXUtils
    {
        /// <summary>
        /// Remembers the scale that has been applied to a pooled effect so that re-scaling is relative and never compounds.
        /// </summary>
        class ScaledEffect : MonoBehaviour
        {
            public float appliedScale = 1f;
        }

        /// <summary>
        /// Scale an effect prefab (and everything under it) to an absolute uniform scale relative to how the prefab was authored.
        /// Legacy KSPParticleEmitters ignore transform scale, so their particle sizes and emitter shapes are scaled explicitly (as ModuleWeapon does for rescaled parts).
        /// Safe to call repeatedly on pooled objects: it tracks what was previously applied.
        /// </summary>
        /// <param name="effect">The instantiated effect.</param>
        /// <param name="scale">The desired absolute scale (1 = as authored).</param>
        public static void ScaleEffect(GameObject effect, float scale)
        {
            if (effect == null) return;
            scale = Mathf.Max(scale, 0.001f);
            var tracker = effect.GetComponent<ScaledEffect>();
            if (tracker == null)
            {
                if (Mathf.Approximately(scale, 1f)) return; // Nothing to do for an untouched prefab.
                tracker = effect.AddComponent<ScaledEffect>();
            }
            float ratio = scale / tracker.appliedScale;
            if (Mathf.Approximately(ratio, 1f)) return;
            tracker.appliedScale = scale;

            effect.transform.localScale = Vector3.one * scale; // Meshes, Shuriken systems (with hierarchy scaling) and child offsets.

            foreach (var pe in effect.GetComponentsInChildren<KSPParticleEmitter>(true))
            {
                if (pe == null) continue;
                pe.minSize *= ratio;
                pe.maxSize *= ratio;
                pe.shape1D *= ratio;
                pe.shape2D *= ratio;
                pe.shape3D *= ratio;
            }
            foreach (var ps in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null) continue;
                var main = ps.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            foreach (var light in effect.GetComponentsInChildren<Light>(true))
            {
                if (light == null) continue;
                light.range *= ratio; // Transform scale doesn't affect light range.
            }
        }
    }
}
