using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.FX;
using BDArmory.Settings;
using BDArmory.Utils;

namespace BDArmory.Weapons
{
    /// <summary>Implemented by modules that have fire effects (ModuleWeapon, CMDropper, MultiMissileLauncher).</summary>
    public interface IFireEffectOwner
    {
        FireEffectSettings GetFireEffectSettings();
    }

    /// <summary>The parent module's data for its fire effects.</summary>
    public struct FireEffectSettings
    {
        /// <summary>Names of EFFECTS entries (see FireEffectController).</summary>
        public string fireEffect, smokeEffect, underwaterFireEffect, underwaterSmokeEffect;
        /// <summary>Underwater, and no underwater effect for a kind of effect: don't fire it.</summary>
        public bool suppressUnderwater;
        /// <summary>Smoke power = (air density / sea level density)^this. 0 = smoke ignores the atmosphere.</summary>
        public float smokeAtmosphereResponse;
        /// <summary>
        /// Legacy flash: transform names (muzzleTransformName / effectsTransformName) of emitters baked into the model. Only used if fireEffect isn't set.
        /// Nothing is guessed from the model: only these transforms are used.
        /// </summary>
        public string legacyTransformNames;
        public bool legacyOneShotWorldParticles, legacyScaleWithPart;
        /// <summary>
        /// The legacy emitters are the module's primary effect and keep firing even when fireEffect is set (which is then a secondary flash on top of it),
        /// instead of being replaced by it. To stop the legacy effect, don't list its transforms.
        /// </summary>
        public bool legacyAdditive;
    }

    /// <summary>
    /// Fire effects for a gun (ModuleWeapon), a countermeasure/smoke launcher (CMDropper) or a MultiMissileLauncher, driven by the part's stock EFFECTS block.
    /// The parent module owns one of these and supplies its data (FireEffectSettings); there is no separate module to add to parts.
    ///
    ///   EFFECTS { muzzleFlash { PREFAB_PARTICLE { prefabName = ...  transformName = muzzleTransform  localScale = 1, 1, 1.5 } AUDIO { ... } } }
    ///   MODULE { name = ModuleWeapon  fireEffectName = muzzleFlash  smokeEffectName = muzzleSmoke ... }
    ///
    /// The effect is fired with part.Effect(name, power) and set back to 0 shortly after the last shot, so continuous and one-shot effects both work.
    /// Position, rotation and scale (xyz) are the effect's own: transformName, localOffset, localRotation and localScale in its EFFECTS node.
    ///
    /// Flash and smoke are separate effects:
    ///   fireEffectName  : the flash. Fired at full power. If not set, the legacy fields name baked emitters (muzzleTransformName, effectsTransformName), which
    ///                     are treated as the flash and emitted directly (emit=false at start, rescaling, gapless world-space emitters).
    ///                     If fireEffectName IS set, emitters still baked into those legacy transforms are just silenced (emit=false), never fired.
    ///                     Exception: a smoke launcher (CMDropper) treats its legacy effect (effectsTransformName) as its primary effect, which keeps
    ///                     firing; fireEffectName is then a secondary flash fired in addition to it. To drop the legacy effect, don't list its transforms.
    /// Every effect is set to 0 power when the controller is created (and again shortly after), so nothing runs until it's fired.
    ///   smokeEffectName : smoke. Its power follows the atmosphere: (air density / sea level)^smokeAtmosphereResponse, so the effect's own curves
    ///                     (emission, size, ...) decide what thinner air looks like. Not fired at all below ~2%.
    /// Underwater (the effect's transform is below the ocean surface): underwaterFireEffectName / underwaterSmokeEffectName are fired instead.
    /// With none, the normal effect fires unless suppressEffectsUnderwater is set.
    ///
    /// Several barrels / tubes. Each effect field is a list (separated by ; or ,). The index is the barrel being fired (ripple fire) or the tube launched:
    ///   - A list with several entries: entry i is barrel/tube i.
    ///   - Numbered effects: with fireEffectName = launchFX, effects named launchFX1, launchFX.002, ... (number starting at 1, like the tubes) are
    ///     barrel/tube 1, 2, ...
    ///   - One effect whose transformName matches several transforms: transform i is barrel/tube i (modulo the number of transforms).
    ///   - One effect on one transform is fired every time.
    /// When every barrel/tube fires together (no ripple fire), all of them fire. Whatever the effect's transforms are children of, the effect system
    /// places it there: for VLS, keep the effect transforms out of the tube transforms, which are scaled to zero as they are launched.
    /// </summary>
    public class FireEffectController
    {
        const float holdTime = 0.1f; // An effect is set back to 0 power this long after it was last fired.

        #region Effect lookup
        class EffectSlots
        {
            public struct Slot
            {
                public string effect;
                public int index; // Barrel/tube this belongs to, or -1 for every one.
                public int transformCount;
                public List<string> transforms; // Transform names the effect is attached to.
            }
            public readonly List<Slot> slots = new List<Slot>();
            public Transform reference; // Where it fires from (for the underwater check).
            public readonly bool flash; // A flash (its lights flash with it), as opposed to smoke.
            public bool Any => slots.Count > 0;

            public EffectSlots(Part part, string nameList, Dictionary<string, List<string>> effects, string kind, bool flash)
            {
                this.flash = flash;
                var names = BDAcTools.ParseNames(nameList);
                for (int i = 0; i < names.Count; ++i)
                {
                    bool found = false;
                    foreach (var kv in effects)
                        if (NumberSuffix.TryParse(kv.Key, names[i], out int number))
                        {
                            Add(part, kv.Key, kv.Value, number - 1);
                            found = true;
                        }
                    if (effects.TryGetValue(names[i], out var transforms))
                    {
                        Add(part, names[i], transforms, names.Count > 1 ? i : -1);
                        found = true;
                    }
                    if (!found && HighLogic.LoadedSceneIsFlight)
                        Debug.LogError($"[BDArmory.FireEffects]: {part.partInfo?.name}: {kind} effect '{names[i]}' isn't in the part's EFFECTS block.");
                }
            }

            void Add(Part part, string effect, List<string> transformNames, int index)
            {
                int count = 0;
                foreach (var name in transformNames)
                {
                    var found = part.FindModelTransforms(name);
                    count += found.Length;
                    if (reference == null && found.Length > 0) reference = found[0];
                }
                slots.Add(new Slot { effect = effect, index = index, transformCount = count, transforms = transformNames });
            }
        }

        /// <summary>The effects in a part's EFFECTS block and the transform names each uses.</summary>
        internal static Dictionary<string, List<string>> ReadEffects(Part part)
        {
            var result = new Dictionary<string, List<string>>();
            var node = part.partInfo != null && part.partInfo.partConfig != null ? part.partInfo.partConfig.GetNode("EFFECTS") : null;
            if (node == null) return result;
            for (int i = 0; i < node.nodes.Count; ++i)
            {
                var effect = node.nodes[i];
                var transforms = new List<string>();
                for (int j = 0; j < effect.nodes.Count; ++j)
                    foreach (var name in effect.nodes[j].GetValues("transformName"))
                        if (!transforms.Contains(name)) transforms.Add(name);
                result[effect.name] = transforms;
            }
            return result;
        }

        /// <summary>
        /// Names of the transforms that a part's fire effects use (from every IFireEffectOwner on it). Other modules that adopt a part's emitters
        /// (e.g., MissileLauncher) should leave emitters on or beneath these alone.
        /// </summary>
        public static HashSet<string> GetManagedTransformNames(Part part)
        {
            var names = new HashSet<string>();
            if (part == null) return names;
            Dictionary<string, List<string>> effects = null;
            foreach (PartModule module in part.Modules)
            {
                if (!(module is IFireEffectOwner owner)) continue;
                var s = owner.GetFireEffectSettings();
                if (effects == null) effects = ReadEffects(part);
                foreach (var list in new[] { s.fireEffect, s.smokeEffect, s.underwaterFireEffect, s.underwaterSmokeEffect })
                    foreach (var baseName in BDAcTools.ParseNames(list))
                        foreach (var kv in effects)
                            if (kv.Key == baseName || NumberSuffix.TryParse(kv.Key, baseName, out _))
                                foreach (var transform in kv.Value) names.Add(transform);
                foreach (var transform in BDAcTools.ParseNames(s.legacyTransformNames)) names.Add(transform); // Legacy emitters are managed too (fired, or silenced).
            }
            return names;
        }

        /// <summary>True if the transform is, or is beneath, one of the managed transforms.</summary>
        public static bool IsManaged(HashSet<string> managedNames, Part part, Transform transform)
        {
            if (managedNames == null || managedNames.Count == 0) return false;
            for (var t = transform; t != null && t != part.transform; t = t.parent)
                if (managedNames.Contains(t.name)) return true;
            return false;
        }
        #endregion

        readonly Part part;
        readonly PartModule owner;
        readonly FireEffectSettings settings;
        readonly EffectSlots fire, smoke, underwaterFire, underwaterSmoke;

        public FireEffectController(PartModule owner, FireEffectSettings settings)
        {
            this.owner = owner;
            part = owner.part;
            this.settings = settings;
            var effects = ReadEffects(part);
            fire = new EffectSlots(part, settings.fireEffect, effects, "fire", true);
            smoke = new EffectSlots(part, settings.smokeEffect, effects, "smoke", false);
            underwaterFire = new EffectSlots(part, settings.underwaterFireEffect, effects, "underwater fire", true);
            underwaterSmoke = new EffectSlots(part, settings.underwaterSmokeEffect, effects, "underwater smoke", false);
            if (!fire.Any || settings.legacyAdditive) SetUpLegacyFlash();
            else SilenceLegacyEmitters(); // The effects replace the legacy flash.
            part.OnJustAboutToBeDestroyed += Dispose;
            // Nothing should be running until something fires. Effects can be instantiated after this runs, so stop them again a little later.
            StopEffects(); // Anything that exists yet; the rest as it appears.
            if (owner != null && owner.isActiveAndEnabled) owner.StartCoroutine(StopEffectsAtStart());
        }

        /// <summary>Set every effect this controller uses to 0 power, and switch off what those effects created (emitters, lights).</summary>
        void StopEffects(bool refresh = true, bool clearParticles = true)
        {
            foreach (var set in new[] { fire, smoke, underwaterFire, underwaterSmoke })
                foreach (var slot in set.slots)
                    part.Effect(slot.effect, 0f);
            if (refresh) RefreshEffectObjects();
            SwitchOffEffectObjects(clearParticles);
        }

        /// <summary>
        /// Effects can emit as soon as they're created (and flash their lights), which can be before this runs. Keep them off, every frame for about a
        /// second as they appear, and clear what they managed to emit, so there's no puff when the part loads or launches.
        /// </summary>
        IEnumerator StopEffectsAtStart()
        {
            for (int frame = 0; frame < 60 && part != null; ++frame)
            {
                yield return null;
                if (part == null || fired) yield break; // (A launcher that spawns a live missile can fire within the first second.)
                StopEffects(refresh: frame < 10 || frame % 10 == 0);
            }
            if (part == null) yield break;
            Debug.Log($"[BDArmory.FireEffects]: {part.partInfo?.name}: {effectEmitters.Count} effect emitter(s) and {effectLights.Count} light(s) switched off, {legacySilenced} legacy emitter(s) silenced, {legacyGroups.Count} legacy flash group(s).");
        }

        #region Emitters and lights created by the effects
        // Things that exist in the part's prefab are baked into the model; anything else under an effect's transform was created by the effect system.
        readonly List<KSPParticleEmitter> effectEmitters = new List<KSPParticleEmitter>();
        class EffectLight { public Light light; public string effect; public int ordinal; public bool flash; }
        readonly List<EffectLight> effectLights = new List<EffectLight>();

        /// <summary>True if the transform exists, at the same path, in the part's prefab (i.e., it's part of the model, not something added at runtime).</summary>
        bool IsBakedInModel(Transform transform) => IsBaked(part, transform);

        internal static bool IsBaked(Part part, Transform transform)
        {
            var prefab = part.partInfo != null ? part.partInfo.partPrefab : null;
            if (prefab == null) return true; // Can't tell.
            string path = null;
            for (var t = transform; t != null && t != part.transform; t = t.parent)
                path = path == null ? t.name : t.name + "/" + path;
            return path != null && prefab.transform.Find(path) != null;
        }

        /// <summary>Find the emitters and lights the effect system has created under this controller's effect transforms.</summary>
        void RefreshEffectObjects()
        {
            foreach (var set in new[] { fire, smoke, underwaterFire, underwaterSmoke })
                foreach (var slot in set.slots)
                {
                    int ordinal = 0; // Which of the effect's transforms (the transformIndex of part.Effect).
                    foreach (var name in slot.transforms)
                        foreach (var attach in part.FindModelTransforms(name))
                        {
                            if (attach != null)
                            {
                                foreach (var emitter in attach.GetComponentsInChildren<KSPParticleEmitter>(true))
                                    if (emitter != null && !effectEmitters.Contains(emitter) && !IsBakedInModel(emitter.transform))
                                        effectEmitters.Add(emitter);
                                foreach (var light in attach.GetComponentsInChildren<Light>(true))
                                    if (light != null && !IsBakedInModel(light.transform) && !effectLights.Exists(l => l.light == light))
                                        effectLights.Add(new EffectLight { light = light, effect = slot.effect, ordinal = ordinal, flash = set.flash });
                            }
                            ++ordinal;
                        }
                }
        }

        /// <summary>
        /// Stop the effects' emitters emitting and their lights shining (not relying on a 0 power event to do it). The effect system turns the
        /// emitters back on when it's fired, and the lights come on with the flash (see SetLights).
        /// </summary>
        void SwitchOffEffectObjects(bool clearParticles)
        {
            foreach (var emitter in effectEmitters)
            {
                if (emitter == null) continue;
                emitter.emit = false;
                if (clearParticles && emitter.ps != null) emitter.ps.Clear();
            }
            foreach (var l in effectLights)
                if (l.light != null) l.light.enabled = false;
        }

        /// <summary>
        /// Lights come from the effect's model (BDA's explosion models carry one; ExplosionFX removes them), and nothing in the effect system switches them
        /// off, so they'd stay on. They are on only while their flash effect is, and only with the LightFX setting. Smoke lights are never on.
        /// transformIndex &lt; 0: every transform of the effect.
        /// </summary>
        void SetLights(string effect, int transformIndex, bool on)
        {
            if (on && !BDArmorySettings.LightFX) return;
            foreach (var l in effectLights)
                if (l.light != null && l.effect == effect && (transformIndex < 0 || l.ordinal == transformIndex) && (!on || l.flash))
                    l.light.enabled = on;
        }
        #endregion

        /// <summary>
        /// The effects replace the legacy flash, but a part that's partly converted may still have emitters baked into the legacy transforms.
        /// Stop those from running by themselves (they're never fired).
        /// </summary>
        void SilenceLegacyEmitters()
        {
            foreach (var name in BDAcTools.ParseNames(settings.legacyTransformNames))
                foreach (var attach in part.FindModelTransforms(name))
                    if (attach != null)
                        foreach (var emitter in attach.GetComponentsInChildren<KSPParticleEmitter>(true))
                            if (emitter != null && IsBakedInModel(emitter.transform)) // Not the effects' own emitters, which may share these transforms.
                            {
                                emitter.emit = false;
                                ++legacySilenced;
                            }
        }
        int legacySilenced = 0;

        void Dispose()
        {
            part.OnJustAboutToBeDestroyed -= Dispose;
            foreach (var group in legacyGroups)
                foreach (var emitter in group.emitters)
                    if (emitter != null) EffectBehaviour.RemoveParticleEmitter(emitter);
            legacyGroups.Clear();
            foreach (var active in activeEffects) part.Effect(active.effect, 0f); // Stop anything still running.
            activeEffects.Clear();
        }

        #region Firing
        /// <summary>
        /// Fire the effects. index &lt; 0: every barrel/tube; otherwise just that one (the barrel being fired with ripple fire, or the tube launched).
        /// </summary>
        public void Fire(int index = -1)
        {
            fired = true; // Stops the start-up sweep (which clears emitters) from wiping a real shot.
            if (settings.legacyAdditive && legacyGroups.Count > 0) FireLegacyFlash(index); // The primary effect, in addition to any effects.
            FireKind(fire, underwaterFire, index, false);
            FireKind(smoke, underwaterSmoke, index, true);
        }

        void FireKind(EffectSlots normal, EffectSlots water, int index, bool isSmoke)
        {
            bool legacy = !settings.legacyAdditive && !isSmoke && !normal.Any && legacyGroups.Count > 0;
            if (!normal.Any && !water.Any && !legacy) return;
            Transform reference = normal.Any ? normal.reference : (legacy ? legacyGroups[0].reference : water.reference);
            if (IsUnderwater(reference))
            {
                if (water.Any) { Trigger(water, index, 1f); return; } // The underwater effect replaces the normal one.
                if (settings.suppressUnderwater) return;
            }
            if (legacy) { FireLegacyFlash(index); return; }
            if (!normal.Any) return;
            float power = 1f;
            if (isSmoke)
            {
                power = SmokeFactor();
                if (power < 0.02f) return; // Too thin for smoke.
            }
            Trigger(normal, index, power);
        }

        void Trigger(EffectSlots set, int index, float power)
        {
            foreach (var slot in set.slots)
            {
                if (slot.index >= 0 && index >= 0 && slot.index != index) continue; // Another barrel/tube's.
                // One effect over several transforms: the barrel/tube picks its transform.
                int transformIndex = slot.index < 0 && index >= 0 && slot.transformCount > 1 ? index % slot.transformCount : -1;
                Play(slot.effect, power, transformIndex);
            }
        }
        #endregion

        #region Playing effects
        class ActiveEffect { public string effect; public int transformIndex; public float lastFired; }
        readonly List<ActiveEffect> activeEffects = new List<ActiveEffect>();
        Coroutine holdRoutine;

        void Play(string effect, float power, int transformIndex)
        {
            if (transformIndex >= 0) part.Effect(effect, power, transformIndex);
            else part.Effect(effect, power);
            ActiveEffect active = null;
            foreach (var a in activeEffects) if (a.effect == effect && a.transformIndex == transformIndex) { active = a; break; }
            if (active == null) activeEffects.Add(active = new ActiveEffect { effect = effect, transformIndex = transformIndex });
            active.lastFired = Time.time;
            SetLights(effect, transformIndex, true);
            if (holdRoutine == null && owner != null && owner.isActiveAndEnabled) holdRoutine = owner.StartCoroutine(StopWhenIdle());
        }

        /// <summary>Set effects back to 0 power once they haven't been fired for a moment (rapid fire keeps them running).</summary>
        IEnumerator StopWhenIdle()
        {
            var wait = new WaitForSeconds(holdTime * 0.5f);
            while (activeEffects.Count > 0 && part != null)
            {
                yield return wait;
                for (int i = activeEffects.Count - 1; i >= 0; --i)
                {
                    var active = activeEffects[i];
                    if (Time.time - active.lastFired < holdTime) continue;
                    if (active.transformIndex >= 0) part.Effect(active.effect, 0f, active.transformIndex);
                    else part.Effect(active.effect, 0f);
                    SetLights(active.effect, active.transformIndex, false);
                    activeEffects.RemoveAt(i);
                }
                if (activeEffects.Count == 0) SwitchOffEffectObjects(false); // Don't rely on the 0 power event alone to stop emitting.
            }
            holdRoutine = null;
        }
        #endregion

        #region Environment
        float smokeFactor = 1f, lastEnvTime = -1f;
        bool fired = false;

        bool IsUnderwater(Transform reference)
        {
            var body = part.vessel != null ? part.vessel.mainBody : FlightGlobals.currentMainBody;
            if (body == null || !body.ocean) return false;
            return FlightGlobals.getAltitudeAtPos(reference != null ? reference.position : part.transform.position) < 0;
        }

        /// <summary>The power for smoke effects in the local atmosphere (1 at sea level density or above).</summary>
        float SmokeFactor()
        {
            if (settings.smokeAtmosphereResponse <= 0f || part.vessel == null) return 1f;
            if (Time.time - lastEnvTime < 0.25f) return smokeFactor; // Density changes slowly: don't recompute for every shot.
            lastEnvTime = Time.time;
            float ratio = Mathf.Clamp01((float)(part.vessel.atmDensity / 1.225)); // Relative to Kerbin sea level.
            smokeFactor = Mathf.Clamp01(Mathf.Pow(ratio, settings.smokeAtmosphereResponse));
            return smokeFactor;
        }
        #endregion

        #region Legacy flash (emitters baked into the model)
        class LegacyGroup
        {
            public readonly List<KSPParticleEmitter> emitters = new List<KSPParticleEmitter>();
            public readonly List<BDAGaplessParticleEmitter> gapless = new List<BDAGaplessParticleEmitter>();
            public Transform reference;
        }
        readonly List<LegacyGroup> legacyGroups = new List<LegacyGroup>();

        /// <summary>
        /// Take over the emitters on or beneath the transforms the legacy field lists (one group per name: for a gun, one per barrel).
        /// Nothing else in the model is touched.
        /// </summary>
        void SetUpLegacyFlash()
        {
            bool flight = HighLogic.LoadedSceneIsFlight;
            float sizeMultiplier = settings.legacyScaleWithPart ? part.rescaleFactor : 1f;
            var claimed = new HashSet<KSPParticleEmitter>();
            foreach (var name in BDAcTools.ParseNames(settings.legacyTransformNames))
            {
                var group = new LegacyGroup();
                legacyGroups.Add(group);
                foreach (var attach in part.FindModelTransforms(name))
                {
                    if (attach == null) continue;
                    if (group.reference == null) group.reference = attach;
                    var found = attach.GetComponentsInChildren<KSPParticleEmitter>(true);
                    if (found.Length == 0) // (Effects instantiated here count too, but then the transform is plainly an effect's, not a model error.)
                    {
                        Debug.LogError($"[BDArmory.FireEffects]: FX transform '{name}' on {part.partInfo?.name} has no KSPParticleEmitter component. Please fix your model.");
                        continue;
                    }
                    foreach (var emitter in found)
                    {
                        if (emitter == null || !IsBakedInModel(emitter.transform) || !claimed.Add(emitter)) continue; // Effects may share this transform: leave their emitters alone.
                        group.emitters.Add(emitter);
                        emitter.emit = false; // Nothing emits until it fires.
                        EffectBehaviour.AddParticleEmitter(emitter);
                        if (!flight) continue;
                        FXScaleState.Apply(emitter.gameObject, Vector3.one, sizeMultiplier, false); // Rescale with the part, as the weapon always did.
                        if (emitter.useWorldSpace && !settings.legacyOneShotWorldParticles) group.gapless.Add(GetGapless(emitter));
                    }
                }
            }
        }

        BDAGaplessParticleEmitter GetGapless(KSPParticleEmitter emitter)
        {
            var gpe = emitter.GetComponent<BDAGaplessParticleEmitter>();
            if (gpe == null) gpe = emitter.gameObject.AddComponent<BDAGaplessParticleEmitter>();
            gpe.part = part;
            gpe.pEmitter = emitter; // Don't wait for Start().
            return gpe;
        }

        void FireLegacyFlash(int index)
        {
            for (int i = 0; i < legacyGroups.Count; ++i)
            {
                if (index >= 0 && i != index) continue;
                var group = legacyGroups[i];
                foreach (var emitter in group.emitters)
                {
                    if (emitter == null) continue;
                    if (emitter.useWorldSpace && !settings.legacyOneShotWorldParticles) continue; // Emitted via its gapless emitter.
                    if (emitter.maxEnergy < 0.5f) // Make sure one-shot emitters live long enough to be seen.
                    {
                        float twoFrameTime = Mathf.Clamp(Time.deltaTime * 2f, 0.02f, 0.499f);
                        emitter.maxEnergy = twoFrameTime;
                        emitter.minEnergy = twoFrameTime / 3f;
                    }
                    emitter.Emit();
                }
                foreach (var gpe in group.gapless)
                    if (gpe != null) gpe.EmitParticles();
            }
        }
        #endregion
    }

    /// <summary>
    /// The number-suffix convention for per-tube things (tube transforms, effects, animations): a base name, an optional separator
    /// ('.', '_' or '-') and the tube number starting at 1: "rocket.001", "launchFX2", "tubeOpen_3".
    /// </summary>
    public static class NumberSuffix
    {
        /// <summary>The trailing number of a name ("rocket.001", "launchFX2" -> 1, 2), or -1.</summary>
        public static int TrailingNumber(string name)
        {
            int end = name.Length, start = end;
            while (start > 0 && char.IsDigit(name[start - 1])) --start;
            if (start == end) return -1;
            return int.TryParse(name.Substring(start), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1;
        }

        /// <summary>Optional separators then digits only ("1", ".001", "_2").</summary>
        public static bool IsSuffix(string rest)
        {
            int i = 0;
            while (i < rest.Length && (rest[i] == '.' || rest[i] == '_' || rest[i] == '-')) ++i;
            if (i == rest.Length) return false;
            for (; i < rest.Length; ++i) if (!char.IsDigit(rest[i])) return false;
            return true;
        }

        /// <summary>True if name is baseName followed by a tube number (1 or more), which is returned.</summary>
        public static bool TryParse(string name, string baseName, out int number)
        {
            number = -1;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseName) || !name.StartsWith(baseName, System.StringComparison.Ordinal)) return false;
            string rest = name.Substring(baseName.Length);
            if (!IsSuffix(rest)) return false;
            number = TrailingNumber(rest);
            return number >= 1;
        }
    }

    /// <summary>
    /// Applies an xyz scale to particle emitters, from their pristine values (so it can be applied repeatedly, and to pooled objects, without compounding).
    /// KSPParticleEmitters don't scale with their transform: sizes, shapes and velocities have to be scaled explicitly.
    /// Scale axes are the host's local axes: x/y = cross-section, z = length. A single emitter's own axes are used when the host is the emitter itself.
    /// - Particle size (and sizeGrow, shape1D): average of x and y, so stretching along z doesn't fatten the particles.
    /// - shape2D/shape3D/localVelocity: per emitter axis (mapped through the emitter's rotation relative to the host).
    /// - When the whole hierarchy is scaled, child transform positions are scaled about the host too.
    /// sizeMultiplier (e.g., part.rescaleFactor) is applied to sizes and shapes only.
    /// </summary>
    public class FXScaleState : MonoBehaviour
    {
        class EmitterBase
        {
            public KSPParticleEmitter emitter;
            public float minSize, maxSize, sizeGrow, shape1D;
            public Vector2 shape2D;
            public Vector3 shape3D, localVelocity;
        }
        class NodeBase
        {
            public Transform transform;
            public Vector3 localToHost;
        }

        bool captured = false;
        readonly List<EmitterBase> emitters = new List<EmitterBase>();
        readonly List<NodeBase> nodes = new List<NodeBase>();

        /// <summary>Scale the emitters on a host object (and, if wholeHierarchy, everything beneath it).</summary>
        public static FXScaleState Apply(GameObject host, Vector3 scale, float sizeMultiplier, bool wholeHierarchy)
        {
            var state = host.GetComponent<FXScaleState>();
            if (state == null) state = host.AddComponent<FXScaleState>();
            state.ApplyScale(scale, sizeMultiplier, wholeHierarchy);
            return state;
        }

        void Capture(bool wholeHierarchy)
        {
            captured = true;
            var found = wholeHierarchy ? GetComponentsInChildren<KSPParticleEmitter>(true) : GetComponents<KSPParticleEmitter>();
            foreach (var e in found)
            {
                if (e == null) continue;
                emitters.Add(new EmitterBase
                {
                    emitter = e,
                    minSize = e.minSize,
                    maxSize = e.maxSize,
                    sizeGrow = e.sizeGrow,
                    shape1D = e.shape1D,
                    shape2D = e.shape2D,
                    shape3D = e.shape3D,
                    localVelocity = e.localVelocity,
                });
            }
            if (wholeHierarchy)
                foreach (var t in GetComponentsInChildren<Transform>(true)) // Parents before children.
                    if (t != transform) nodes.Add(new NodeBase { transform = t, localToHost = transform.InverseTransformPoint(t.position) });
        }

        void ApplyScale(Vector3 scale, float sizeMultiplier, bool wholeHierarchy)
        {
            if (!captured) Capture(wholeHierarchy);
            scale = new Vector3(Mathf.Max(Mathf.Abs(scale.x), 1e-3f), Mathf.Max(Mathf.Abs(scale.y), 1e-3f), Mathf.Max(Mathf.Abs(scale.z), 1e-3f));
            float width = (scale.x + scale.y) * 0.5f;
            foreach (var node in nodes) // Absolute positions, so applying parents first is fine.
                if (node.transform != null) node.transform.position = transform.TransformPoint(Vector3.Scale(node.localToHost, scale));
            foreach (var b in emitters)
            {
                var e = b.emitter;
                if (e == null) continue;
                var axis = AxisScale(Quaternion.Inverse(transform.rotation) * e.transform.rotation, scale);
                e.minSize = b.minSize * width * sizeMultiplier;
                e.maxSize = b.maxSize * width * sizeMultiplier;
                e.sizeGrow = b.sizeGrow * width;
                e.shape1D = b.shape1D * width * sizeMultiplier;
                e.shape2D = Vector2.Scale(b.shape2D, new Vector2(axis.x, axis.y)) * sizeMultiplier;
                e.shape3D = Vector3.Scale(b.shape3D, axis) * sizeMultiplier;
                e.localVelocity = Vector3.Scale(b.localVelocity, axis);
            }
        }

        /// <summary>The scale along each of an emitter's local axes, given the emitter's rotation relative to the frame the scale is expressed in.</summary>
        static Vector3 AxisScale(Quaternion emitterInFrame, Vector3 scale)
        {
            return new Vector3(
                Vector3.Scale(emitterInFrame * Vector3.right, scale).magnitude,
                Vector3.Scale(emitterInFrame * Vector3.up, scale).magnitude,
                Vector3.Scale(emitterInFrame * Vector3.forward, scale).magnitude);
        }
    }
}
