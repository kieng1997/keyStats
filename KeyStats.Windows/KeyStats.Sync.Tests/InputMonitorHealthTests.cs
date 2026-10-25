using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using KeyStats.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KeyStats.Sync.Tests;

/// <summary>
/// Deterministic coverage for the hook-health watchdog. No global hooks are installed, no real
/// input is synthesized, and no timers are started: only the pure health judgment is exercised.
/// </summary>
[TestClass]
public sealed class InputMonitorHealthTests
{
    private const int ThresholdMs = 5000;
    private const int StaleElapsedMs = ThresholdMs + 1;
    private const int FreshElapsedMs = ThresholdMs - 1;

    [TestMethod]
    [DataRow(StaleElapsedMs, FreshElapsedMs, true, false, true, DisplayName = "Keyboard stale with keyboard evidence and a fresh mouse recovers")]
    [DataRow(FreshElapsedMs, StaleElapsedMs, false, true, true, DisplayName = "Mouse stale with mouse evidence and a fresh keyboard recovers")]
    [DataRow(FreshElapsedMs, FreshElapsedMs, true, true, false, DisplayName = "Healthy hooks do not recover")]
    [DataRow(StaleElapsedMs, FreshElapsedMs, false, true, false, DisplayName = "A stale idle keyboard is not revived by mouse activity")]
    [DataRow(FreshElapsedMs, StaleElapsedMs, true, false, false, DisplayName = "A stale idle mouse is not revived by keyboard activity")]
    [DataRow(StaleElapsedMs, StaleElapsedMs, false, false, false, DisplayName = "Idle devices without evidence never recover")]
    public void ShouldRecoverHooks_JudgesEachDeviceIndependently(
        int keyboardElapsedMs,
        int mouseElapsedMs,
        bool keyboardActivity,
        bool cursorMoved,
        bool expected)
    {
        var nowTick = 1_000_000;

        var actual = InvokeShouldRecoverHooks(
            nowTick,
            cursorMoved,
            keyboardActivity,
            nowTick - keyboardElapsedMs,
            nowTick - mouseElapsedMs);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow((long)ThresholdMs, false, DisplayName = "Exactly at the threshold is still healthy")]
    [DataRow((long)ThresholdMs + 1, true, DisplayName = "One millisecond past the threshold recovers")]
    [DataRow(0L, false, DisplayName = "A callback from this instant is healthy")]
    [DataRow(2147484647L, true, DisplayName = "A device idle longer than int.MaxValue still recovers")]
    public void ShouldRecoverHooks_UsesStrictThreshold(long elapsedMs, bool expected)
    {
        var nowTick = 1_000_000;
        var lastTick = unchecked((int)(nowTick - elapsedMs));

        var actual = InvokeShouldRecoverHooks(
            nowTick,
            cursorMoved: true,
            keyboardActivity: false,
            nowTick,
            lastTick);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow(6000, 1000, false, true, true, DisplayName = "Keyboard stale across the wrap recovers")]
    [DataRow(1000, 6000, true, false, true, DisplayName = "Mouse stale across the wrap recovers")]
    [DataRow(ThresholdMs, 1000, false, true, false, DisplayName = "Exactly at the threshold across the wrap stays healthy")]
    [DataRow(1000, 1000, true, true, false, DisplayName = "Both fresh across the wrap stays healthy")]
    public void ShouldRecoverHooks_HandlesSignedTickCountWrap(
        int keyboardElapsedMs,
        int mouseElapsedMs,
        bool cursorMoved,
        bool keyboardActivity,
        bool expected)
    {
        // Environment.TickCount wraps from int.MaxValue to int.MinValue every ~24.8 days.
        // Sampling just after the wrap makes both stale timestamps land before it.
        var nowTick = int.MinValue + 1000;
        var keyboardTick = unchecked(nowTick - keyboardElapsedMs);
        var mouseTick = unchecked(nowTick - mouseElapsedMs);

        // A stale timestamp must actually have been sampled before the wrap.
        if (keyboardElapsedMs > ThresholdMs) Assert.IsTrue(keyboardTick > nowTick);
        if (mouseElapsedMs > ThresholdMs) Assert.IsTrue(mouseTick > nowTick);

        var actual = InvokeShouldRecoverHooks(nowTick, cursorMoved, keyboardActivity, keyboardTick, mouseTick);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void ResetHookHealthTimestamps_RefreshesBothDevicesFromTheSameInstant()
    {
        // Both install paths (start and watchdog reinstall) must reset the two timestamps together.
        var service = CreateService();

        InvokeInstance(service, "ResetHookHealthTimestamps", 42_000);

        Assert.AreEqual(42_000, ReadTick(service, "_lastKeyboardHookCallbackTick"));
        Assert.AreEqual(42_000, ReadTick(service, "_lastMouseHookCallbackTick"));
    }

    [TestMethod]
    public void FreshlyInstalledHooks_DoNotRecoverWhileBothDevicesAreActive()
    {
        var service = CreateService();
        InvokeInstance(service, "ResetHookHealthTimestamps", 42_000);

        var keyboardTick = ReadTick(service, "_lastKeyboardHookCallbackTick");
        var mouseTick = ReadTick(service, "_lastMouseHookCallbackTick");

        Assert.IsFalse(InvokeShouldRecoverHooks(42_000 + FreshElapsedMs, true, true, keyboardTick, mouseTick));
        Assert.IsTrue(InvokeShouldRecoverHooks(42_000 + StaleElapsedMs, true, true, keyboardTick, mouseTick));
    }

    [TestMethod]
    [DataRow(0xA2, DisplayName = "Held left Ctrl is not evidence")]
    [DataRow(0xA3, DisplayName = "Held right Ctrl is not evidence")]
    [DataRow(0xA0, DisplayName = "Held left Shift is not evidence")]
    [DataRow(0xA1, DisplayName = "Held right Shift is not evidence")]
    [DataRow(0xA4, DisplayName = "Held left Alt is not evidence")]
    [DataRow(0xA5, DisplayName = "Held right Alt is not evidence")]
    [DataRow(0x5B, DisplayName = "Held left Win is not evidence")]
    [DataRow(0x5C, DisplayName = "Held right Win is not evidence")]
    public void HeldModifier_IsNotTreatedAsKeyboardEvidence(int vk)
    {
        // A long modifier hold keeps the high bit set even though nothing new happened.
        var service = CreateService();
        AddPressedKey(service, vk);

        Assert.IsFalse(IsEvidence(service, vk, HighBit));
    }

    [TestMethod]
    public void HeldModifier_DoesNotSuppressRecoveryWhenTheHookIsDead()
    {
        // Ignoring the hold must not make the keyboard look dead on its own: a stale keyboard with
        // no evidence stays quiet, while a genuinely dead hook still recovers through its own
        // device evidence (here: both hooks stale while the cursor keeps moving).
        var service = CreateService();
        AddPressedKey(service, 0xA2);
        var nowTick = 1_000_000;
        var keyboardActivity = IsEvidence(service, 0xA2, HighBit);

        Assert.IsFalse(InvokeShouldRecoverHooks(nowTick, false, keyboardActivity, nowTick - StaleElapsedMs, nowTick));
        Assert.IsTrue(InvokeShouldRecoverHooks(nowTick, true, keyboardActivity, nowTick - StaleElapsedMs, nowTick - StaleElapsedMs));
    }

    [TestMethod]
    [DataRow(0x11, DisplayName = "Generic VK_CONTROL is never modifier evidence")]
    [DataRow(0x10, DisplayName = "Generic VK_SHIFT is never modifier evidence")]
    [DataRow(0x12, DisplayName = "Generic VK_MENU is never modifier evidence")]
    public void GenericModifierHighBit_IsNotEvidence(int vk)
    {
        // The low-level hook reports left/right variants, so a generic key can never match _pressedKeys.
        var service = CreateService();
        AddPressedKey(service, 0xA2);

        Assert.IsFalse(IsEvidence(service, vk, HighBit));
    }

    [TestMethod]
    public void NonModifierKeysAndTransitions_RemainEvidence()
    {
        var service = CreateService();

        // A held non-modifier key is genuine evidence.
        Assert.IsTrue(IsEvidence(service, 0x41, HighBit));

        // The low bit means "changed since the last poll" and is evidence even for a modifier.
        Assert.IsTrue(IsEvidence(service, 0xA2, LowBit));

        // A modifier that the hook has not seen go down is not explained by the hook → evidence.
        Assert.IsTrue(IsEvidence(service, 0xA2, HighBit));

        // No state at all is never evidence.
        Assert.IsFalse(IsEvidence(service, 0x41, 0));
    }

    [TestMethod]
    public void ModifierRelease_MakesTheHighBitEvidenceAgain()
    {
        var service = CreateService();
        AddPressedKey(service, 0xA2);
        Assert.IsFalse(IsEvidence(service, 0xA2, HighBit));

        RemovePressedKey(service, 0xA2);

        Assert.IsTrue(IsEvidence(service, 0xA2, HighBit));
    }

    private const short HighBit = unchecked((short)0x8000);
    private const short LowBit = 0x0001;

    private static bool IsEvidence(InputMonitorService service, int vk, short state)
    {
        var method = typeof(InputMonitorService).GetMethod(
            "IsKeyboardActivityEvidence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing InputMonitorService.IsKeyboardActivityEvidence");
        return (bool)method!.Invoke(service, new object[] { vk, state })!;
    }

    private static bool InvokeShouldRecoverHooks(
        int nowTick,
        bool cursorMoved,
        bool keyboardActivity,
        int lastKeyboardTick,
        int lastMouseTick)
    {
        var method = typeof(InputMonitorService).GetMethod(
            "ShouldRecoverHooks",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing InputMonitorService.ShouldRecoverHooks");
        return (bool)method!.Invoke(
            null,
            new object[] { nowTick, cursorMoved, keyboardActivity, lastKeyboardTick, lastMouseTick })!;
    }

    private static void InvokeInstance(object target, string name, int argument)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "Missing InputMonitorService." + name);
        method!.Invoke(target, new object[] { argument });
    }

    private static int ReadTick(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing InputMonitorService." + name);
        return (int)field!.GetValue(target)!;
    }

    private static void AddPressedKey(object target, int vk) => MutatePressedKeys(target, keys => keys.Add(vk));

    private static void RemovePressedKey(object target, int vk) => MutatePressedKeys(target, keys => keys.Remove(vk));

    private static void MutatePressedKeys(object target, Func<HashSet<int>, bool> mutate)
    {
        var field = target.GetType().GetField("_pressedKeys", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing InputMonitorService._pressedKeys");
        var keys = field!.GetValue(target) as HashSet<int>;
        Assert.IsNotNull(keys, "InputMonitorService._pressedKeys is not initialized");
        mutate(keys!);
    }

    /// <summary>
    /// Creates an instance without running the private constructor or installing any hooks.
    /// Field initializers are skipped by GetUninitializedObject, so _pressedKeys is restored here.
    /// </summary>
    private static InputMonitorService CreateService()
    {
        var service = (InputMonitorService)FormatterServices.GetUninitializedObject(typeof(InputMonitorService));
        var field = typeof(InputMonitorService).GetField(
            "_pressedKeys",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "Missing InputMonitorService._pressedKeys");
        field!.SetValue(service, new HashSet<int>());
        return service;
    }
}
