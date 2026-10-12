using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

using BDArmory.CounterMeasure;
using BDArmory.FX;
using BDArmory.Utils;
using BDArmory.Weapons.Missiles;

namespace BDArmory.Weapons
{
    /// <summary>
    /// Fire effects (muzzle flash, smoke, launch blast) for a gun (ModuleWeapon), a countermeasure/smoke launcher (CMDropper) or a MultiMissileLauncher.
    /// A part can have several of these (e.g., a gun's muzzle flash and a smoke launcher's puff), each linked to one of the part's modules.
    /// Nothing adds this module automatically: parts get it from config (see MMPatches/BDA_FireFX.cfg).
    ///
    /// Linking (which module triggers this one):
    ///   linkedFireTransformName    = fireTransformName of a ModuleWeapon
    ///   linkedEffectsTransformName = effectsTransformName of a CMDropper
    ///   neither: the part's first ModuleWeapon, else its MultiMissileLauncher, else its first CMDropper.
    ///
    /// Two ways to get FX:
    ///
    /// 1. Legacy (no fxModelPath). The old fields stay on the linked module for compatibility (ModuleWeapon.muzzleTransformName and
    ///    oneShotWorldParticles, CMDropper.effectsTransformName). This module reads them and takes over the emitters baked into the part model:
    ///    emit=false at start, rescaling, gapless world-space emitters and emitting when the module fires. The linked modules no longer touch emitters.
    ///    Emitters are found on or beneath the named transforms. adoptAllEmitters also takes every other emitter in the model, which is what
    ///    ModuleWeapon used to do; emitters named by another ModuleFireFX on the part are never taken, so a gun and a smoke launcher can share a part.
    ///
    /// 2. Explicit (fxModelPath = a .mu model, no extension). The FX hierarchy is instantiated (pooled) at the transforms in fxTransformName, as
    ///    missiles do with exhaust prefabs. Guns/droppers: one group per name (groups line up with barrels for ripple fire).
    ///    MultiMissileLauncher: FX fire once per tube launched. Transforms are matched to tubes by number suffix, like the tubes themselves
    ///    (rocket.001, rocket.002, ...): with fxTransformName = launchFX, "launchFX.001" / "launchFX1" is tube 1's, and so on. A transform with
    ///    no number is shared by every tube. A transform that is a child of a tube transform belongs to that tube. With fxTransformName empty,
    ///    each tube's own transform is used. Only the position and rotation of these transforms are used (the FX live on an unscaled anchor placed
    ///    when the tube fires), so tube scaling, and the tube being scaled to zero when fired, don't affect them.
    ///
    /// fxScale is in the FX's local axes: x/y = cross-section (these also drive particle size), z = along the fire direction (length).
    ///
    /// Examples:
    ///   MODULE { name = ModuleFireFX  adoptAllEmitters = true }                                    // a legacy gun
    ///   MODULE { name = ModuleFireFX  linkedEffectsTransformName = effectsTransform  oneShotWorldParticles = true }  // a smoke launcher
    ///   MODULE { name = ModuleFireFX  fxModelPath = Some/FX/model  fxTransformName = muzzleTransform  fxScale = 1, 1, 1.5 }
    ///   MODULE { name = ModuleFireFX  fxModelPath = Some/VLSBlast/model  fxTransformName = blastFX  fxScale = 2, 2, 2 }
    /// </summary>
    public class ModuleFireFX : PartModule
    {
        /// <summary>fireTransformName of the ModuleWeapon this belongs to.</summary>
        [KSPField] public string linkedFireTransformName = "";
        /// <summary>effectsTransformName of the CMDropper this belongs to.</summary>
        [KSPField] public string linkedEffectsTransformName = "";
        /// <summary>Model (GameDatabase url, no extension) to instantiate at each FX transform. Empty = adopt emitters already in the part model (legacy).</summary>
        [KSPField] public string fxModelPath = "";
        /// <summary>Comma separated transform names in the part model. Empty = the linked module's legacy field (muzzleTransformName / effectsTransformName).</summary>
        [KSPField] public string fxTransformName = "";
        /// <summary>XYZ scale of the FX, in the FX's local axes.</summary>
        [KSPField] public Vector3 fxScale = Vector3.one;
        /// <summary>Also multiply particle sizes/shapes by part.rescaleFactor (what ModuleWeapon always did for its emitters).</summary>
        [KSPField] public bool scaleWithPart = true;
        /// <summary>Read missing config from the linked module's legacy fields.</summary>
        [KSPField] public bool useLegacyConfig = true;
        /// <summary>Legacy: also take over every emitter in the part model that no other ModuleFireFX names (the old ModuleWeapon behaviour).</summary>
        [KSPField] public bool adoptAllEmitters = false;
        /// <summary>World-space emitters are emitted like local ones, instead of via gapless emitters. Also read from ModuleWeapon.oneShotWorldParticles.</summary>
        [KSPField] public bool oneShotWorldParticles = false;

        class FXGroup
        {
            public readonly List<KSPParticleEmitter> emitters = new List<KSPParticleEmitter>();
            public readonly List<BDAGaplessParticleEmitter> gapless = new List<BDAGaplessParticleEmitter>();
            public Transform anchor; // Launcher FX only.
        }

        static readonly Dictionary<string, ObjectPool> fxPool = new Dictionary<string, ObjectPool>();

        readonly List<FXGroup> groups = new List<FXGroup>();
        readonly List<KSPParticleEmitter> owned = new List<KSPParticleEmitter>(); // Baked emitters this module controls.
        readonly List<BDAGaplessParticleEmitter> ambientGapless = new List<BDAGaplessParticleEmitter>(); // Gapless emitters of the owned world-space emitters.
        readonly List<GameObject> spawned = new List<GameObject>();
        readonly Dictionary<int, FXGroup> tubeGroups = new Dictionary<int, FXGroup>(); // Launcher FX: tube index, or -1 for the shared FX.
        readonly Dictionary<int, Transform> tubeSources = new Dictionary<int, Transform>();
        Transform sharedSource;
        bool tubeSourcesResolved = false;
        bool namedTubeSources = false;
        ModuleWeapon weapon;
        CMDropper dropper;
        MultiMissileLauncher mml;
        bool initialized = false;
        bool spawnMode = false;
        bool oneShot = false;
        bool diedHooked = false;

        #region Lookup
        static ModuleFireFX Find(Part part, System.Predicate<ModuleFireFX> match)
        {
            if (part == null) return null;
            foreach (var fx in part.FindModulesImplementing<ModuleFireFX>())
            {
                fx.ResolveLinks();
                if (match(fx)) return fx;
            }
            return null;
        }
        /// <summary>The fire FX module linked to a gun, if the part has one.</summary>
        public static ModuleFireFX FindFor(ModuleWeapon weapon) => weapon == null ? null : Find(weapon.part, fx => fx.weapon == weapon);
        /// <summary>The fire FX module linked to a countermeasure/smoke launcher, if the part has one.</summary>
        public static ModuleFireFX FindFor(CMDropper dropper) => dropper == null ? null : Find(dropper.part, fx => fx.dropper == dropper);
        /// <summary>The fire FX module linked to a multi-missile launcher, if the part has one.</summary>
        public static ModuleFireFX FindFor(MultiMissileLauncher launcher) => launcher == null ? null : Find(launcher.part, fx => fx.mml == launcher);

        /// <summary>True if the emitter belongs to an FX hierarchy spawned by a ModuleFireFX (other modules, e.g. MissileLauncher, should leave it alone).</summary>
        public static bool IsFireFXManaged(Component emitter)
        {
            if (emitter == null) return false;
            var state = emitter.GetComponentInParent<FXScaleState>();
            return state != null && state.fireFXOwned;
        }

        void ResolveLinks()
        {
            weapon = null; dropper = null; mml = null;
            var weapons = part.FindModulesImplementing<ModuleWeapon>();
            var droppers = part.FindModulesImplementing<CMDropper>();
            if (!string.IsNullOrEmpty(linkedFireTransformName)) weapon = weapons.Find(w => w.fireTransformName == linkedFireTransformName);
            else if (!string.IsNullOrEmpty(linkedEffectsTransformName)) dropper = droppers.Find(d => d.effectsTransformName == linkedEffectsTransformName);
            else if (weapons.Count > 0) weapon = weapons[0];
            else if ((mml = part.FindModuleImplementing<MultiMissileLauncher>()) != null) { }
            else if (droppers.Count > 0) dropper = droppers[0];
        }

        /// <summary>The transform names this module uses: its own, else the linked module's legacy ones.</summary>
        List<string> ResolveNames()
        {
            if (!string.IsNullOrEmpty(fxTransformName)) return BDAcTools.ParseNames(fxTransformName);
            if (!useLegacyConfig) return new List<string>();
            if (weapon != null) return BDAcTools.ParseNames(weapon.muzzleTransformName);
            if (dropper != null) return BDAcTools.ParseNames(dropper.effectsTransformName);
            return new List<string>();
        }

        bool IsOrIsUnder(Transform t, List<string> names)
        {
            for (; t != null && t != part.transform; t = t.parent)
                if (names.Contains(t.name)) return true;
            return false;
        }

        /// <summary>Another ModuleFireFX on this part names this emitter (or something it's under).</summary>
        bool ClaimedByOtherFX(KSPParticleEmitter emitter)
        {
            foreach (var other in part.FindModulesImplementing<ModuleFireFX>())
            {
                if (other == this) continue;
                other.ResolveLinks();
                if (IsOrIsUnder(emitter.transform, other.ResolveNames())) return true;
            }
            return false;
        }
        #endregion

        #region Setup
        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            Initialize();
        }

        /// <summary>Set everything up. Safe to call more than once.</summary>
        public void Initialize()
        {
            if (initialized || part == null) return;
            initialized = true;
            ResolveLinks();
            bool flight = HighLogic.LoadedSceneIsFlight;
            spawnMode = !string.IsNullOrEmpty(fxModelPath);
            oneShot = oneShotWorldParticles || (useLegacyConfig && weapon != null && weapon.oneShotWorldParticles);
            if (weapon == null && dropper == null && mml == null)
            {
                if (flight) Debug.LogWarning($"[BDArmory.ModuleFireFX]: {part.partInfo?.name}: no module to link to (linkedFireTransformName='{linkedFireTransformName}', linkedEffectsTransformName='{linkedEffectsTransformName}'). It will never fire.");
                return;
            }
            if (mml != null)
            {
                if (!spawnMode && flight) Debug.LogWarning($"[BDArmory.ModuleFireFX]: {part.partInfo?.name}: launcher FX need an fxModelPath.");
                if (flight && spawnMode) HookPartDeath();
                return; // Launcher FX are created as tubes fire.
            }

            var names = ResolveNames();
            foreach (var name in names)
            {
                var group = new FXGroup();
                groups.Add(group);
                foreach (var attach in part.FindModelTransforms(name))
                {
                    if (attach == null) continue;
                    if (spawnMode)
                    {
                        if (flight) SpawnAt(group, attach);
                        continue;
                    }
                    var found = attach.GetComponentsInChildren<KSPParticleEmitter>(true);
                    if (found.Length == 0)
                    {
                        Debug.LogError($"[BDArmory.ModuleFireFX]: FX transform '{name}' on {part.partInfo?.name} has no KSPParticleEmitter component. Please fix your model.");
                        continue;
                    }
                    foreach (var emitter in found)
                    {
                        if (emitter == null || owned.Contains(emitter)) continue;
                        group.emitters.Add(emitter);
                        owned.Add(emitter);
                    }
                }
            }

            if (!spawnMode && adoptAllEmitters)
            {
                foreach (var emitter in part.FindModelComponents<KSPParticleEmitter>())
                {
                    if (emitter == null || owned.Contains(emitter) || IsFireFXManaged(emitter) || ClaimedByOtherFX(emitter)) continue;
                    owned.Add(emitter);
                }
            }

            float sizeMultiplier = scaleWithPart ? part.rescaleFactor : 1f;
            foreach (var emitter in owned)
            {
                emitter.emit = false; // Nothing emits until it fires.
                EffectBehaviour.AddParticleEmitter(emitter);
                if (!flight) continue;
                FXScaleState.Apply(emitter.gameObject, fxScale, sizeMultiplier, false);
                if (emitter.useWorldSpace && !oneShot) ambientGapless.Add(GetGapless(emitter));
            }

            if (flight && spawnMode) HookPartDeath();
        }

        void HookPartDeath()
        {
            if (diedHooked) return;
            diedHooked = true;
            part.OnJustAboutToDie += Release;
            part.OnJustAboutToBeDestroyed += Release;
        }

        BDAGaplessParticleEmitter GetGapless(KSPParticleEmitter emitter)
        {
            var gpe = emitter.GetComponent<BDAGaplessParticleEmitter>();
            if (gpe == null) gpe = emitter.gameObject.AddComponent<BDAGaplessParticleEmitter>();
            gpe.part = part;
            gpe.pEmitter = emitter; // Don't wait for Start().
            return gpe;
        }

        static bool CreatePool(string modelPath)
        {
            if (!fxPool.ContainsKey(modelPath) || fxPool[modelPath] == null || fxPool[modelPath].poolObject == null)
            {
                var template = GameDatabase.Instance.GetModel(modelPath);
                if (template == null) return false;
                template.SetActive(false);
                fxPool[modelPath] = ObjectPool.CreateObjectPool(template, 1, true, true);
            }
            return true;
        }

        /// <summary>Instantiate the FX model at a transform and register its emitters with a group.</summary>
        void SpawnAt(FXGroup group, Transform attach)
        {
            if (!CreatePool(fxModelPath))
            {
                Debug.LogError($"[BDArmory.ModuleFireFX]: Failed to get model {fxModelPath} for {part.partInfo?.name}. Check that the file exists!");
                return;
            }
            var fx = fxPool[fxModelPath].GetPooledObject();
            fx.SetActive(true);
            fx.transform.SetParent(attach, false);
            fx.transform.localPosition = Vector3.zero;
            fx.transform.localRotation = Quaternion.identity;
            var state = FXScaleState.Apply(fx, fxScale, scaleWithPart ? part.rescaleFactor : 1f, true);
            state.fireFXOwned = true;
            foreach (var emitter in fx.GetComponentsInChildren<KSPParticleEmitter>(true))
            {
                if (emitter == null) continue;
                emitter.emit = false;
                if (emitter.useWorldSpace && !oneShot) group.gapless.Add(GetGapless(emitter));
                else
                {
                    EffectBehaviour.AddParticleEmitter(emitter);
                    group.emitters.Add(emitter);
                }
            }
            spawned.Add(fx);
        }

        /// <summary>Return spawned FX to the pool.</summary>
        void Release()
        {
            if (part != null)
            {
                part.OnJustAboutToDie -= Release;
                part.OnJustAboutToBeDestroyed -= Release;
            }
            diedHooked = false;
            foreach (var fx in spawned)
            {
                if (fx == null) continue;
                foreach (var emitter in fx.GetComponentsInChildren<KSPParticleEmitter>(true))
                    if (emitter != null) EffectBehaviour.RemoveParticleEmitter(emitter);
                fx.transform.SetParent(null, false);
                fx.SetActive(false);
            }
            spawned.Clear();
            foreach (var group in tubeGroups.Values) if (group.anchor != null) Destroy(group.anchor.gameObject);
            tubeGroups.Clear();
            foreach (var group in groups) { group.emitters.Clear(); group.gapless.Clear(); }
        }

        void OnDestroy()
        {
            foreach (var emitter in owned)
                if (emitter != null) EffectBehaviour.RemoveParticleEmitter(emitter);
            foreach (var gpe in ambientGapless)
                if (gpe != null && gpe.pEmitter != null) EffectBehaviour.RemoveParticleEmitter(gpe.pEmitter);
            Release();
        }
        #endregion

        #region Firing
        /// <summary>
        /// Fire the FX (guns and countermeasure launchers). group &lt; 0: every group; otherwise just that group (e.g., the barrel index for ripple fire).
        /// </summary>
        public void Fire(int group = -1)
        {
            if (!initialized) Initialize();
            for (int i = 0; i < groups.Count; ++i)
            {
                if (group < 0 || i == group) EmitGroup(groups[i]);
                if (!spawnMode) EmitAmbientGapless(); // Legacy behaviour: a gun emitted these once per muzzle group, per shot.
            }
        }

        /// <summary>Fire the FX for one tube of a MultiMissileLauncher. Call before the tube is scaled to zero.</summary>
        public void FireTube(int tube, Transform tubeTransform)
        {
            if (!initialized) Initialize();
            if (!HighLogic.LoadedSceneIsFlight || mml == null || !spawnMode) return;
            if (!tubeSourcesResolved) ResolveTubeSources();
            Transform source;
            int key = tube;
            if (tubeSources.TryGetValue(tube, out source)) { }
            else if (sharedSource != null) { source = sharedSource; key = -1; }
            else if (!namedTubeSources) source = tubeTransform; // No names given: FX at the tube itself.
            else return; // Names given but this tube has no FX.
            var group = GetTubeGroup(key, source);
            if (group != null) EmitGroup(group);
        }

        /// <summary>Find the FX transforms of each tube: by number suffix, as a child of a tube, or one shared by all tubes.</summary>
        void ResolveTubeSources()
        {
            tubeSourcesResolved = true;
            tubeSources.Clear();
            sharedSource = null;
            var names = ResolveNames();
            namedTubeSources = names.Count > 0;
            if (!namedTubeSources) return;
            var model = part.FindModelTransform("model") ?? part.transform;
            var launchRoot = string.IsNullOrEmpty(mml.launchTransformName) ? null : part.FindModelTransform(mml.launchTransformName);
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t.GetComponentInParent<MissileDummy>() != null) continue; // Part of a missile model.
                foreach (var name in names)
                {
                    if (t.name == name)
                    {
                        int tube = TubeOf(t, launchRoot);
                        if (tube >= 0) tubeSources[tube] = t; // A child of a tube: that tube's.
                        else if (sharedSource == null) sharedSource = t; // No number: shared by every tube.
                        break;
                    }
                    if (NumberSuffix.TryParse(t.name, name, out int number)) // Numbered like the tubes, starting at 1.
                    {
                        tubeSources[number - 1] = t;
                        break;
                    }
                }
            }
        }

        /// <summary>The (0-based) tube whose transform this is a child of, or -1.</summary>
        static int TubeOf(Transform t, Transform launchRoot)
        {
            if (launchRoot == null) return -1;
            for (var a = t; a != null && a != launchRoot; a = a.parent)
                if (a.parent == launchRoot) return NumberSuffix.TrailingNumber(a.name) - 1; // -1 if unnumbered.
            return -1;
        }

        FXGroup GetTubeGroup(int key, Transform source)
        {
            if (source == null) return null;
            if (!tubeGroups.TryGetValue(key, out var group) || group.anchor == null)
            {
                // Tubes are scaled (and hidden when fired), so FX can't be children of them or of anything under them: use an unscaled anchor on the model instead.
                var parent = part.FindModelTransform("model") ?? part.transform;
                var anchor = new GameObject($"fireFXAnchor{key}").transform;
                anchor.SetParent(parent, false);
                float r = scaleWithPart ? part.rescaleFactor : 1f;
                var ls = parent.lossyScale;
                anchor.localScale = new Vector3(r / Mathf.Max(Mathf.Abs(ls.x), 1e-4f), r / Mathf.Max(Mathf.Abs(ls.y), 1e-4f), r / Mathf.Max(Mathf.Abs(ls.z), 1e-4f));
                group = new FXGroup { anchor = anchor };
                SpawnAt(group, anchor);
                tubeGroups[key] = group;
            }
            group.anchor.position = source.position; // Pose only: the source's scale is ignored.
            group.anchor.rotation = source.rotation;
            return group;
        }

        void EmitGroup(FXGroup group)
        {
            foreach (var emitter in group.emitters)
            {
                if (emitter == null) continue;
                if (emitter.useWorldSpace && !oneShot) continue;
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

        void EmitAmbientGapless()
        {
            foreach (var gpe in ambientGapless)
                if (gpe != null) gpe.EmitParticles();
        }
        #endregion
    }

    /// <summary>
    /// The number-suffix convention for per-tube things (tube transforms, FX transforms, animations): a base name, an optional separator
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

        /// <summary>Set for FX hierarchies spawned by ModuleFireFX, so that other modules don't adopt their emitters.</summary>
        public bool fireFXOwned = false;
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
