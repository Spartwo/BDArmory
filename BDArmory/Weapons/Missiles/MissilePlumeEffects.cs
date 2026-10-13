using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using BDArmory.FX;
using BDArmory.Settings;

namespace BDArmory.Weapons.Missiles
{
    /// <summary>The missile's data for its plume effects.</summary>
    public struct PlumeEffectSettings
    {
        /// <summary>Names of EFFECTS entries (see MissilePlumeEffects).</summary>
        public string boostEffect, cruiseEffect, smokeTrailEffect;
        /// <summary>After burnout the cruise plume and the trail fade out over this long (seconds).</summary>
        public float fadeTime;
        /// <summary>Smoke trail power = throttle * (air density / sea level density)^this. 0 = ignore the atmosphere.</summary>
        public float smokeAtmosphereResponse;
    }

    /// <summary>
    /// A missile's plume, driven by the part's stock EFFECTS block instead of emitters baked into the model or exhaust prefabs. MissileLauncher owns one
    /// and supplies its data (PlumeEffectSettings); the legacy plume (exhaustPrefabPath, boostExhaustPrefabPath, baked emitters, plumeScale) is used
    /// whenever none of these effects is named.
    ///
    ///   EFFECTS { missile_cruise { MODEL_MULTI_PARTICLE { modelName = ...  transformName = exhaustTransform  emission = ...  localScale = 1, 1, 1.5 } } }
    ///   MODULE { name = MissileLauncher  boostEffectName = missile_boost  plumeEffectName = missile_cruise  smokeTrailEffectName = missile_trail ... }
    ///
    /// As with a stock engine, an effect's power follows the throttle (part.Effect(name, power), 0 = off), so its own curves (emission, energy, speed, ...)
    /// decide what the throttle looks like. Position, rotation and scale (xyz) are the effect's own transformName, localOffset, localRotation, localScale.
    ///   boostEffectName      : the boost plume. On while boosting.
    ///   plumeEffectName      : the cruise plume. On while cruising, then fades out over plumeFadeTime after burnout (the legacy plume lingers for 5 s).
    ///   smokeTrailEffectName : the smoke trail, during boost and cruise. Its power also follows the air density (smokeAtmosphereResponse), so it thins out
    ///                          at altitude and is off in a vacuum. It's detached when the missile detonates, so the trail lingers (as the legacy gapless one does).
    /// Lights in the effects' models come on with the boost and cruise plumes (if the LightFX setting is on) and never with the smoke trail.
    /// </summary>
    public class MissilePlumeEffects
    {
        class Channel
        {
            public string effect; // Null if not used.
            public List<string> transforms = new List<string>();
            public bool flashLights; // Lights in the effect's model come on with it.
            public float power;
            public bool activated; // Has been powered: the start-up sweep leaves it alone from then on.
            public bool lightsOn;
            public readonly List<KSPParticleEmitter> emitters = new List<KSPParticleEmitter>();
            public readonly List<Light> lights = new List<Light>();
        }

        readonly PartModule owner;
        readonly Part part;
        readonly PlumeEffectSettings settings;
        readonly Channel boost, cruise, trail;
        Coroutine fadeRoutine;
        float atmosphereFactor = 1f, lastAtmosphereTime = -1f;

        /// <summary>True if any plume effect is in use (the legacy plume is then not).</summary>
        public bool Active => boost.effect != null || cruise.effect != null || trail.effect != null;

        public MissilePlumeEffects(PartModule owner, PlumeEffectSettings settings)
        {
            this.owner = owner;
            part = owner.part;
            this.settings = settings;
            var effects = FireEffectController.ReadEffects(part);
            boost = MakeChannel(settings.boostEffect, effects, true, "boost");
            cruise = MakeChannel(settings.cruiseEffect, effects, true, "cruise");
            trail = MakeChannel(settings.smokeTrailEffect, effects, false, "smoke trail");
            if (!Active) return;
            // Nothing should run until the throttle says so. Effects can be instantiated after this, so sweep for a while as they appear.
            StopAll();
            if (owner != null && owner.isActiveAndEnabled) owner.StartCoroutine(SweepAtStart());
        }

        Channel MakeChannel(string effect, Dictionary<string, List<string>> effects, bool flashLights, string kind)
        {
            var channel = new Channel { flashLights = flashLights };
            if (string.IsNullOrEmpty(effect)) return channel;
            if (!effects.TryGetValue(effect, out var transforms))
            {
                Debug.LogError($"[BDArmory.MissilePlume]: {part.partInfo?.name}: {kind} effect '{effect}' isn't in the part's EFFECTS block.");
                return channel;
            }
            channel.effect = effect;
            channel.transforms = transforms;
            return channel;
        }

        /// <summary>
        /// The names of the transforms the plume effects use, from the part's config (so it's valid before the effects exist). Anything beneath them is
        /// the effects', not baked plume emitters for MissileLauncher to drive.
        /// </summary>
        public static HashSet<string> GetTransformNames(Part part, PlumeEffectSettings settings)
        {
            var names = new HashSet<string>();
            var effects = FireEffectController.ReadEffects(part);
            foreach (var effect in new[] { settings.boostEffect, settings.cruiseEffect, settings.smokeTrailEffect })
                if (!string.IsNullOrEmpty(effect) && effects.TryGetValue(effect, out var transforms))
                    foreach (var transform in transforms) names.Add(transform);
            return names;
        }

        #region Driving the effects
        /// <summary>Boost plume power (the throttle while boosting, else 0).</summary>
        public void SetBoost(float power) => Set(boost, power);

        /// <summary>Cruise plume power (the throttle while cruising, else 0).</summary>
        public void SetCruise(float power) => Set(cruise, power);

        /// <summary>Smoke trail power: the throttle, reduced in thin air and 0 in a vacuum.</summary>
        public void SetTrail(float throttle) => Set(trail, throttle > 0f ? throttle * AtmosphereFactor() : 0f);

        void Set(Channel channel, float power)
        {
            if (channel.effect == null) return;
            if (power < 0f) power = 0f;
            if (Mathf.Abs(power - channel.power) < 0.002f && (power > 0f) == (channel.power > 0f)) return; // Don't re-send an unchanged power every tick.
            if (power > 0f && !channel.activated)
            {
                channel.activated = true;
                RefreshObjects(); // So its lights are known.
            }
            channel.power = power;
            part.Effect(channel.effect, power);
            if ((power > 0f) != channel.lightsOn) SetLights(channel, power > 0f);
        }

        /// <summary>Burnout: the cruise plume and the trail fade out (the boost plume is ended with the boost).</summary>
        public void BeginFadeOut()
        {
            if (fadeRoutine != null) return;
            if (settings.fadeTime <= 0f || owner == null || !owner.isActiveAndEnabled)
            {
                Set(boost, 0f); Set(cruise, 0f); Set(trail, 0f);
                return;
            }
            fadeRoutine = owner.StartCoroutine(FadeOut());
        }

        IEnumerator FadeOut()
        {
            float start = Time.time, boost0 = boost.power, cruise0 = cruise.power, trail0 = trail.power;
            while (part != null && Time.time - start < settings.fadeTime)
            {
                float remaining = 1f - (Time.time - start) / settings.fadeTime;
                Set(boost, boost0 * remaining);
                Set(cruise, cruise0 * remaining);
                Set(trail, trail0 * remaining);
                yield return null;
            }
            if (part != null) { Set(boost, 0f); Set(cruise, 0f); Set(trail, 0f); }
            fadeRoutine = null;
        }

        /// <summary>
        /// The missile is being destroyed: leave the world-space smoke trail behind to fade away on its own, as the legacy gapless emitters do
        /// (otherwise it would vanish with the part).
        /// </summary>
        public void DetachTrail()
        {
            if (trail.effect == null) return;
            RefreshObjects();
            foreach (var emitter in trail.emitters)
            {
                if (emitter == null || !emitter.useWorldSpace || emitter.GetComponent<BDAParticleSelfDestruct>() != null) continue;
                emitter.gameObject.AddComponent<BDAParticleSelfDestruct>();
                emitter.transform.parent = null;
            }
        }
        #endregion

        #region Environment
        float AtmosphereFactor()
        {
            if (settings.smokeAtmosphereResponse <= 0f || part.vessel == null) return 1f;
            if (Time.time - lastAtmosphereTime < 0.25f) return atmosphereFactor; // Density changes slowly: don't recompute every tick.
            lastAtmosphereTime = Time.time;
            float ratio = Mathf.Clamp01((float)(part.vessel.atmDensity / 1.225)); // Relative to Kerbin sea level.
            atmosphereFactor = Mathf.Pow(ratio, settings.smokeAtmosphereResponse);
            if (atmosphereFactor < 0.02f) atmosphereFactor = 0f; // Too thin for a trail.
            return atmosphereFactor;
        }
        #endregion

        #region Things the effects create (emitters, lights)
        /// <summary>Find the emitters and lights the effect system has created under the effects' transforms (anything not in the part's prefab).</summary>
        void RefreshObjects()
        {
            foreach (var channel in new[] { boost, cruise, trail })
            {
                if (channel.effect == null) continue;
                foreach (var name in channel.transforms)
                    foreach (var attach in part.FindModelTransforms(name))
                    {
                        if (attach == null) continue;
                        foreach (var emitter in attach.GetComponentsInChildren<KSPParticleEmitter>(true))
                            if (emitter != null && !channel.emitters.Contains(emitter) && !FireEffectController.IsBaked(part, emitter.transform))
                                channel.emitters.Add(emitter);
                        foreach (var light in attach.GetComponentsInChildren<Light>(true))
                            if (light != null && !channel.lights.Contains(light) && !FireEffectController.IsBaked(part, light.transform))
                                channel.lights.Add(light);
                    }
            }
        }

        /// <summary>Lights come from the effects' models and nothing in the effect system switches them off. They're on with the plume only (and LightFX).</summary>
        void SetLights(Channel channel, bool on)
        {
            on = on && channel.flashLights && BDArmorySettings.LightFX;
            channel.lightsOn = on;
            foreach (var light in channel.lights)
                if (light != null) light.enabled = on;
        }

        void StopAll()
        {
            foreach (var channel in new[] { boost, cruise, trail })
                if (channel.effect != null) part.Effect(channel.effect, 0f);
            RefreshObjects();
            SwitchOffUnused(true);
        }

        /// <summary>Switch off the emitters and lights of the channels that haven't been powered yet.</summary>
        void SwitchOffUnused(bool clearParticles)
        {
            foreach (var channel in new[] { boost, cruise, trail })
            {
                if (channel.effect == null || channel.activated) continue;
                foreach (var emitter in channel.emitters)
                {
                    if (emitter == null) continue;
                    emitter.emit = false;
                    if (clearParticles && emitter.ps != null) emitter.ps.Clear();
                }
                foreach (var light in channel.lights)
                    if (light != null) light.enabled = false;
            }
        }

        /// <summary>Effects can emit (and show lights) as soon as they're created: keep them off, every frame for about a second, and clear what they emitted.</summary>
        IEnumerator SweepAtStart()
        {
            for (int frame = 0; frame < 60 && part != null; ++frame)
            {
                yield return null;
                if (part == null) yield break;
                if (frame < 10 || frame % 10 == 0) RefreshObjects();
                SwitchOffUnused(true);
            }
            if (part == null) yield break;
            Debug.Log($"[BDArmory.MissilePlume]: {part.partInfo?.name}: boost {boost.emitters.Count}, cruise {cruise.emitters.Count}, trail {trail.emitters.Count} effect emitter(s); {boost.lights.Count + cruise.lights.Count + trail.lights.Count} light(s).");
        }
        #endregion
    }
}
