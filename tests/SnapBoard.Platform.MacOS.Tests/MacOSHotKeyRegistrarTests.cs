using SnapBoard.Platform.Abstractions.Desktop;
using SnapBoard.Platform.MacOS.Desktop;
using SnapBoard.Platform.MacOS.Interop;

namespace SnapBoard.Platform.MacOS.Tests;

public sealed class MacOSHotKeyRegistrarTests
{
    [Fact]
    public void RegistersTwoCarbonIdsAndMapsTheirSources()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;

        registrar.Register(
            GlobalHotKeySlot.Primary,
            GlobalHotKeyGesture.MacOSDefault);
        registrar.Register(GlobalHotKeySlot.Double, CreateGesture(0x28, "K"));
        registrar.ProcessNativeEvent(
            5,
            MacOSHotKeyRegistrar.CreateIdentifier(GlobalHotKeySlot.Primary));
        registrar.ProcessNativeEvent(
            5,
            MacOSHotKeyRegistrar.CreateIdentifier(GlobalHotKeySlot.Double));

        Assert.Collection(
            native.Registrations,
            primary => Assert.Equal(
                MacOSHotKeyRegistrar.PrimaryHotKeyIdentifier,
                primary.Identifier.Id),
            doubleRegistration => Assert.Equal(
                MacOSHotKeyRegistrar.DoubleHotKeyIdentifier,
                doubleRegistration.Identifier.Id));
        Assert.Collection(
            events,
            primary => Assert.Equal(GlobalHotKeySlot.Primary, primary.Source),
            doubleTrigger => Assert.Equal(GlobalHotKeySlot.Double, doubleTrigger.Source));
    }

    [Fact]
    public void PressesBeforeReleaseAreMarkedRepeatAndCannotBecomeASecondPress()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(GlobalHotKeySlot.Double, CreateGesture(0x28, "K"));
        NativeEventHotKeyId identifier =
            MacOSHotKeyRegistrar.CreateIdentifier(GlobalHotKeySlot.Double);

        registrar.ProcessNativeEvent(5, identifier);
        registrar.ProcessNativeEvent(5, identifier);
        registrar.ProcessNativeEvent(5, identifier);
        registrar.ProcessNativeEvent(6, identifier);
        registrar.ProcessNativeEvent(5, identifier);

        Assert.Equal([false, true, true, false], events.Select(value => value.IsRepeat));
    }

    [Theory]
    [InlineData(
        GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
        0x3Au,
        0u)]
    [InlineData(
        GlobalHotKeyModifiers.Control |
            GlobalHotKeyModifiers.Shift |
            GlobalHotKeyModifiers.NoRepeat,
        0x38u,
        1u << 12)]
    [InlineData(
        GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
        0x07u,
        1u << 11)]
    public void ModifierMainKeyIsNotDuplicatedInCarbonModifierMask(
        GlobalHotKeyModifiers modifiers,
        uint virtualKey,
        uint expectedCarbonModifiers)
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture gesture = new(modifiers, virtualKey, "test");

        GlobalHotKeyRegistrationResult result = registrar.Register(
            GlobalHotKeySlot.Double,
            gesture);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, result.Status);
        Registration registration = Assert.Single(native.Registrations);
        Assert.Equal(virtualKey, registration.VirtualKey);
        Assert.Equal(expectedCarbonModifiers, registration.Modifiers);
    }

    [Fact]
    public void ModifierEventsEmitOnlyOnCompletePressTransitions()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(
            GlobalHotKeySlot.Double,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
                0x3A,
                "Option"));
        NativeEventHotKeyId identifier =
            MacOSHotKeyRegistrar.CreateIdentifier(GlobalHotKeySlot.Double);

        registrar.ProcessNativeEvent(5, identifier);
        native.PressedKeys.Add(0x3A);
        registrar.ProcessNativeEvent(4, default);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Remove(0x3A);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Add(0x3A);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Remove(0x3A);
        registrar.ProcessNativeEvent(4, default);

        Assert.Equal(2, events.Count);
        Assert.All(events, value =>
        {
            Assert.Equal(GlobalHotKeySlot.Double, value.Source);
            Assert.False(value.IsRepeat);
        });
    }

    [Fact]
    public void ModifierCombinationTriggersOnlyWhenOtherModifiersPrecedeMainKey()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(
            GlobalHotKeySlot.Double,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Control |
                    GlobalHotKeyModifiers.Shift |
                    GlobalHotKeyModifiers.NoRepeat,
                0x38,
                "Control+Shift"));

        native.PressedKeys.Add(0x38);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Add(0x3B);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Remove(0x38);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Add(0x38);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Remove(0x38);
        registrar.ProcessNativeEvent(4, default);

        MacOSHotKeyNativeEvent trigger = Assert.Single(events);
        Assert.Equal(GlobalHotKeySlot.Double, trigger.Source);
        Assert.False(trigger.IsRepeat);
    }

    [Fact]
    public void ModifierEventsRejectUnexpectedAdditionalModifiers()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(
            GlobalHotKeySlot.Double,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
                0x3A,
                "Option"));

        native.PressedKeys.UnionWith([0x3A, 0x3B]);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Clear();
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Add(0x3A);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Clear();
        registrar.ProcessNativeEvent(4, default);

        MacOSHotKeyNativeEvent trigger = Assert.Single(events);
        Assert.Equal(GlobalHotKeySlot.Double, trigger.Source);
    }

    [Fact]
    public void OverlappingModifierBindingsRemainSlotSpecific()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(
            GlobalHotKeySlot.Primary,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
                0x3A,
                "Option"));
        registrar.Register(
            GlobalHotKeySlot.Double,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Control |
                    GlobalHotKeyModifiers.Alt |
                    GlobalHotKeyModifiers.NoRepeat,
                0x3A,
                "Control+Option"));

        native.PressedKeys.Add(0x3A);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Clear();
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.UnionWith([0x3A, 0x3B]);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Clear();
        registrar.ProcessNativeEvent(4, default);

        Assert.Collection(
            events,
            primary => Assert.Equal(GlobalHotKeySlot.Primary, primary.Source),
            doubleTrigger => Assert.Equal(GlobalHotKeySlot.Double, doubleTrigger.Source));
    }

    [Fact]
    public void ClearingModifierSlotRemovesFallbackTrigger()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        List<MacOSHotKeyNativeEvent> events = [];
        registrar.Triggered += events.Add;
        registrar.Register(
            GlobalHotKeySlot.Double,
            new GlobalHotKeyGesture(
                GlobalHotKeyModifiers.Alt | GlobalHotKeyModifiers.NoRepeat,
                0x3A,
                "Option"));

        GlobalHotKeyRegistrationResult result = registrar.Clear(GlobalHotKeySlot.Double);
        native.PressedKeys.Add(0x3A);
        registrar.ProcessNativeEvent(4, default);
        native.PressedKeys.Clear();
        registrar.ProcessNativeEvent(4, default);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, result.Status);
        Assert.Empty(events);
    }

    [Fact]
    public void ConflictAndFailedOldReleasePreservePreviousSlot()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture original = CreateGesture(0x28, "K");
        GlobalHotKeyGesture replacement = CreateGesture(0x25, "L");
        registrar.Register(GlobalHotKeySlot.Double, original);
        native.RegisterResults.Enqueue(-9878);

        GlobalHotKeyRegistrationResult conflict = registrar.Register(
            GlobalHotKeySlot.Double,
            replacement);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Conflict, conflict.Status);
        Assert.Equal(original, registrar.GetCurrentGesture(GlobalHotKeySlot.Double));

        native.UnregisterResults.Enqueue(5);
        GlobalHotKeyRegistrationResult releaseFailure = registrar.Register(
            GlobalHotKeySlot.Double,
            replacement);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Failed, releaseFailure.Status);
        Assert.Equal(5, releaseFailure.NativeErrorCode);
        Assert.Equal(original, registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Equal(2, native.UnregisteredReferences.Count);
    }

    [Fact]
    public void DuplicateAndDoubleClearNeverDisturbPrimary()
    {
        FakeMacOSHotKeyNative native = new();
        using MacOSHotKeyRegistrar registrar = new(native);
        GlobalHotKeyGesture doubleGesture = CreateGesture(0x28, "K");
        registrar.Register(GlobalHotKeySlot.Primary, GlobalHotKeyGesture.MacOSDefault);

        GlobalHotKeyRegistrationResult duplicate = registrar.Register(
            GlobalHotKeySlot.Double,
            GlobalHotKeyGesture.MacOSDefault with { DisplayName = "duplicate" });
        registrar.Register(GlobalHotKeySlot.Double, doubleGesture);
        GlobalHotKeyRegistrationResult cleared = registrar.Clear(GlobalHotKeySlot.Double);
        registrar.Clear(GlobalHotKeySlot.Double);

        Assert.Equal(GlobalHotKeyRegistrationStatus.Duplicate, duplicate.Status);
        Assert.Equal(GlobalHotKeyRegistrationStatus.Registered, cleared.Status);
        Assert.Equal(
            GlobalHotKeyGesture.MacOSDefault,
            registrar.GetCurrentGesture(GlobalHotKeySlot.Primary));
        Assert.Null(registrar.GetCurrentGesture(GlobalHotKeySlot.Double));
        Assert.Single(native.UnregisteredReferences);
    }

    private static GlobalHotKeyGesture CreateGesture(uint virtualKey, string displayName) => new(
        GlobalHotKeyModifiers.NoRepeat,
        virtualKey,
        displayName);

    private sealed unsafe class FakeMacOSHotKeyNative : IMacOSHotKeyNative
    {
        private nint _nextReference = 100;

        public Queue<int> RegisterResults { get; } = new();

        public Queue<int> UnregisterResults { get; } = new();

        public List<Registration> Registrations { get; } = [];

        public List<nint> UnregisteredReferences { get; } = [];

        public HashSet<uint> PressedKeys { get; } = [];

        public int InstallEventHandler(
            delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
            delegate* unmanaged[Cdecl]<nint, void> modifierHandler,
            nint userData,
            out nint handlerReference)
        {
            _ = handler;
            _ = modifierHandler;
            _ = userData;
            handlerReference = 1;
            return 0;
        }

        public int RemoveEventHandler(nint handlerReference) => 0;

        public int Register(
            uint virtualKey,
            uint modifiers,
            NativeEventHotKeyId identifier,
            out nint hotKeyReference)
        {
            int result = RegisterResults.Count == 0 ? 0 : RegisterResults.Dequeue();
            hotKeyReference = result == 0 ? _nextReference++ : 0;
            if (result == 0)
            {
                Registrations.Add(new Registration(
                    hotKeyReference,
                    virtualKey,
                    modifiers,
                    identifier));
            }

            return result;
        }

        public int Unregister(nint hotKeyReference)
        {
            UnregisteredReferences.Add(hotKeyReference);
            return UnregisterResults.Count == 0 ? 0 : UnregisterResults.Dequeue();
        }

        public bool TryReadEvent(
            nint eventReference,
            out uint eventKind,
            out NativeEventHotKeyId identifier)
        {
            eventKind = 0;
            identifier = default;
            return false;
        }

        public bool IsKeyPressed(uint virtualKey) => PressedKeys.Contains(virtualKey);
    }

    private sealed record Registration(
        nint Reference,
        uint VirtualKey,
        uint Modifiers,
        NativeEventHotKeyId Identifier);
}
