using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using HarmonyLib;

namespace InteractiveCameraTweak;

// Fixes InteractiveCamera performance degradation when linked to InteractiveCameraControl.
//
// Root causes in OnCommonUpdate (Control != null path):
//   1. World.GetGloballyRegisteredComponents<InteractiveCameraAnchor> runs every frame — a global
//      registry scan regardless of whether any anchors changed.
//   2. AvatarManager.CollectAllBadgeRoots runs every frame when HideBadge=true, calling
//      ForeachComponentInChildren on every user's avatar hierarchy each frame.
//
// Fix: throttle (1) to at most 4×/second; skip (2) unless HideBadge state or user count changed.

[HarmonyPatch(typeof(InteractiveCamera), "OnCommonUpdate")]
static class InteractiveCameraPatch
{
    sealed class PatchState
    {
        public double lastAnchorScanTime = double.MinValue;
        public int lastUserCount = -1;
    }

    // Compiled field accessor — near-native speed, no reflection overhead per call.
    static readonly AccessTools.FieldRef<InteractiveCamera, HashSet<Slot>> _excludedBadgesRef =
        AccessTools.FieldRefAccess<InteractiveCamera, HashSet<Slot>>("_excludedBadges");

    static readonly ConditionalWeakTable<InteractiveCamera, PatchState> _states = new();

    [ThreadStatic]
    static InteractiveCamera? _currentCamera;

    static PatchState GetState(InteractiveCamera cam) => _states.GetOrCreateValue(cam);

    // ── Prefix/Postfix set thread-local context so replacement methods can identify the instance ──

    static void Prefix(InteractiveCamera __instance) => _currentCamera = __instance;
    static void Postfix() => _currentCamera = null;

    // ── Drop-in replacement for World.GetGloballyRegisteredComponents<InteractiveCameraAnchor> ──
    // Stack on entry: world (instance), list, filter — identical to the original callvirt.
    public static void ThrottledGetAnchors(
        World world,
        List<InteractiveCameraAnchor> list,
        Predicate<InteractiveCameraAnchor>? filter)
    {
        var cam = _currentCamera;
        if (cam == null)
        {
            world.GetGloballyRegisteredComponents(list, filter!);
            return;
        }

        var state = GetState(cam);
        const double interval = 0.25; // max 4 scans/sec instead of 60+
        double now = world.Time.WorldTime;
        if (now - state.lastAnchorScanTime < interval)
            return; // leave list empty; existing exclusions from last scan remain intact

        state.lastAnchorScanTime = now;
        world.GetGloballyRegisteredComponents(list, filter!);
    }

    // ── Drop-in replacement for AvatarManager.CollectAllBadgeRoots(IEnumerable<User>, Action<Slot>) ──
    // Stack on entry: users, onRootFound — identical to the original call.
    public static void ThrottledCollectBadgeRoots(
        IEnumerable<User> users,
        Action<Slot> onRootFound)
    {
        var cam = _currentCamera;
        if (cam == null)
        {
            AvatarManager.CollectAllBadgeRoots(users, onRootFound);
            return;
        }

        var state = GetState(cam);
        int userCount = cam.World.UserCount;

        // _excludedBadges is cleared by the original else-branch when HideBadge turns false.
        // If it's empty here, badges are not yet applied and we must run the scan regardless.
        bool badgesApplied = _excludedBadgesRef(cam).Count > 0;

        // Re-scan only when badges haven't been applied yet, or when user count changed.
        if (badgesApplied && userCount == state.lastUserCount)
            return;

        state.lastUserCount = userCount;
        AvatarManager.CollectAllBadgeRoots(users, onRootFound);
    }

    // ── Transpiler: replace the two expensive calls with their throttled counterparts ──

    static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);

        MethodInfo? anchorTarget = FindGetAnchorsMethod();
        MethodInfo? badgeTarget = FindCollectBadgeRootsMethod();

        MethodInfo throttledAnchors = typeof(InteractiveCameraPatch)
            .GetMethod(nameof(ThrottledGetAnchors))!;
        MethodInfo throttledBadges = typeof(InteractiveCameraPatch)
            .GetMethod(nameof(ThrottledCollectBadgeRoots))!;

        int replacements = 0;
        for (int i = 0; i < codes.Count; i++)
        {
            if (anchorTarget != null && MethodMatches(codes[i], anchorTarget))
            {
                codes[i] = new CodeInstruction(OpCodes.Call, throttledAnchors);
                replacements++;
                Plugin.Log.LogDebug("Patched GetGloballyRegisteredComponents<InteractiveCameraAnchor> call.");
                continue;
            }

            if (badgeTarget != null && MethodMatches(codes[i], badgeTarget))
            {
                codes[i] = new CodeInstruction(OpCodes.Call, throttledBadges);
                replacements++;
                Plugin.Log.LogDebug("Patched CollectAllBadgeRoots call.");
            }
        }

        if (replacements < 2)
            Plugin.Log.LogWarning(
                $"InteractiveCameraPatch: expected 2 replacements, made {replacements}. " +
                "The game may have been updated; some optimizations may not be active.");

        return codes;
    }

    static bool MethodMatches(CodeInstruction instr, MethodInfo target) =>
        (instr.opcode == OpCodes.Call || instr.opcode == OpCodes.Callvirt)
        && instr.operand is MethodInfo mi
        && mi == target;

    static MethodInfo? FindGetAnchorsMethod()
    {
        foreach (var m in typeof(World).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "GetGloballyRegisteredComponents" || !m.IsGenericMethodDefinition)
                continue;
            var ps = m.GetParameters();
            if (ps.Length == 2
                && ps[0].ParameterType.IsGenericType
                && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return m.MakeGenericMethod(typeof(InteractiveCameraAnchor));
            }
        }
        Plugin.Log.LogWarning(
            "InteractiveCameraPatch: could not find World.GetGloballyRegisteredComponents<T>(List<T>, Predicate<T>). " +
            "Anchor scan throttle will not be applied.");
        return null;
    }

    static MethodInfo? FindCollectBadgeRootsMethod()
    {
        var method = typeof(AvatarManager).GetMethod(
            "CollectAllBadgeRoots",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(IEnumerable<User>), typeof(Action<Slot>) },
            null);
        if (method == null)
            Plugin.Log.LogWarning(
                "InteractiveCameraPatch: could not find AvatarManager.CollectAllBadgeRoots(IEnumerable<User>, Action<Slot>). " +
                "Badge collection throttle will not be applied.");
        return method;
    }
}
