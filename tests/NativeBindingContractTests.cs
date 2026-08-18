/*
FILE PURPOSE

Purpose: This isolated executable proves the audited NGU Idle 1.260 native-binding catalog against
the installed Assembly-CSharp.dll without constructing Character, opening a save, or invoking any
game method. It specifically guards the irreversible reflection boundary against overload drift,
metadata-token drift, incomplete build recognition, and accidental mutation authority on an
unknown game build.

Mechanism: The test loads the installed assembly for metadata inspection, creates the production
NativeBindingRegistry with the measured SHA-256, and asserts every descriptor binds exactly. It
then validates deliberately corrupted token and overload descriptors, and creates an unknown-hash
registry to prove irreversible bindings are held while the exact read-only cost query remains
available. Adapter calls use null targets only on the unknown registry, so they stop at the build
gate and cannot dispatch native code.

Inputs and outputs: Input is work/Assembly-CSharp.dll plus its sibling Unity dependency assemblies.
Output is assertion diagnostics and a process exit code. No game process, save, runtime telemetry,
configuration, DLL injection, or source artifact is changed.

Invariants and safety: A test must never invoke a bound method on a real native controller. Known
build success requires the exact hash, MVID, type/name/signature/return/visibility/static contract,
and metadata token for every catalog entry. Unknown build tests must retain read metadata only and
must observe HeldUnknownBuild before reflection dispatch.

Extension points and non-goals: Add golden assertions when NativeBindingRegistry gains a new
irreversible family. Transaction settlement, postconditions, rollback, and live-save behavior are
tested by their owning coordinator suites, not here.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NGUInjector.Autopilot;

internal static class NativeBindingContractTests
{
    private static int _assertions;
    private static string _workDirectory;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static Assembly ResolveSibling(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name + ".dll";
        var path = Path.Combine(_workDirectory, name);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static Assembly LoadInstalledAssembly()
    {
        _workDirectory = Path.GetFullPath("work");
        AppDomain.CurrentDomain.AssemblyResolve += ResolveSibling;
        return Assembly.LoadFrom(Path.Combine(_workDirectory, "Assembly-CSharp.dll"));
    }

    private static void TestKnownBuildBindsEveryContract(Assembly gameAssembly,
        out NativeBindingRegistry registry)
    {
        registry = NativeBindingRegistry.Create(gameAssembly,
            NativeBindingRegistry.AuditedGameSha256.ToUpperInvariant());
        Assert(registry.IsKnownBuild, "audited SHA-256 and MVID identify the installed build");
        Assert(registry.AssemblyMvid == NativeBindingRegistry.AuditedGameMvid,
            "registry publishes the exact installed MVID");
        Assert(registry.IrreversibleActionsEnabled,
            "a complete exact catalog enables the irreversible adapter surface");

        var descriptors = registry.AllDescriptors();
        Assert(descriptors.Length >= 100,
            "catalog covers reset/challenge/inventory/pit/card/load and all reflected purchases");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var irreversible = 0;
        for (var i = 0; i < descriptors.Length; i++)
        {
            var descriptor = descriptors[i];
            Assert(keys.Add(descriptor.Key), "binding key is unique: " + descriptor.Key);
            Assert(!string.IsNullOrEmpty(descriptor.SemanticContract)
                   && descriptor.SemanticContract.Contains("1.260"),
                "binding carries semantic contract version: " + descriptor.Key);
            Assert(descriptor.MetadataToken != 0,
                "binding carries an exact metadata token: " + descriptor.Key);
            Assert(registry.HasBinding(descriptor.Key),
                "known build binds exact descriptor: " + descriptor.ExactSignature
                + " / " + registry.FailureFor(descriptor.Key));
            if (descriptor.Scope == NativeBindingScope.IrreversibleMutation) irreversible++;
        }
        Assert(irreversible >= 80, "catalog seals the complete current irreversible reflection surface");
    }

    private static void TestEngageOverloadsCannotAlias(Assembly gameAssembly,
        NativeBindingRegistry registry)
    {
        NativeBindingDescriptor ordinary;
        NativeBindingDescriptor hard;
        Assert(registry.TryGetDescriptor(NativeBindingKeys.RebirthEngage, out ordinary),
            "ordinary engage descriptor exists");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.RebirthEngageHard, out hard),
            "hard engage descriptor exists");
        Assert(ordinary.ParameterTypeNames.Length == 0
               && ordinary.MetadataToken == 0x06000a71,
            "ordinary reset is exact private void Rebirth.engage()");
        Assert(hard.ParameterTypeNames.Length == 1
               && hard.ParameterTypeNames[0] == "System.Boolean"
               && hard.MetadataToken == 0x06000a72,
            "hard reset primitive is distinct private void Rebirth.engage(bool)");

        MemberInfo member;
        string failure;
        var wrongToken = ordinary.WithMetadataToken(hard.MetadataToken);
        Assert(!NativeBindingRegistry.TryValidateDescriptor(gameAssembly, wrongToken, true,
                out member, out failure)
               && failure.Contains("metadata token mismatch"),
            "correct name/signature with wrong token fails closed");

        var wrongOverload = new NativeBindingDescriptor("test.wrong-overload", "Rebirth",
            NativeMemberKind.Method, "engage", new[] {"System.Boolean"}, "System.Void",
            false, NativeVisibility.Private, ordinary.MetadataToken,
            NativeBindingScope.IrreversibleMutation, "test only");
        Assert(!NativeBindingRegistry.TryValidateDescriptor(gameAssembly, wrongOverload, true,
                out member, out failure)
               && failure.Contains("metadata token mismatch"),
            "matching the bool overload with the ordinary token cannot alias the reset primitive");

        var corruptedCatalog = registry.AllDescriptors();
        for (var i = 0; i < corruptedCatalog.Length; i++)
            if (corruptedCatalog[i].Key == NativeBindingKeys.RebirthEngage)
                corruptedCatalog[i] = corruptedCatalog[i].WithMetadataToken(hard.MetadataToken);
        var constructor = typeof(NativeBindingRegistry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] {typeof(Assembly), typeof(string), typeof(NativeBindingDescriptor[])}, null);
        Assert(constructor != null, "contract test can exercise the private catalog constructor");
        var corruptedRegistry = (NativeBindingRegistry)constructor.Invoke(new object[]
        {
            gameAssembly, NativeBindingRegistry.AuditedGameSha256, corruptedCatalog
        });
        Assert(corruptedRegistry.IsKnownBuild && !corruptedRegistry.IrreversibleActionsEnabled,
            "one wrong critical token disables the complete irreversible surface");
        var held = corruptedRegistry.CreateMutationAdapters().InvokeOrdinaryRebirth(null);
        Assert(held.Status == NativeInvocationStatus.HeldRegistryIncomplete
               && !held.InvocationAttempted,
            "partial known-build binding holds before reflection dispatch");
    }

    private static void TestUnknownBuildIsReadOnly(Assembly gameAssembly)
    {
        var unknown = NativeBindingRegistry.Create(gameAssembly,
            new string('0', NativeBindingRegistry.AuditedGameSha256.Length));
        Assert(!unknown.IsKnownBuild, "wrong SHA-256 is an unsupported game build");
        Assert(!unknown.IrreversibleActionsEnabled,
            "unknown build disables the entire irreversible adapter surface");
        Assert(!unknown.HasBinding(NativeBindingKeys.RebirthEngage),
            "unknown build never exposes an irreversible reset MethodInfo");
        Assert(unknown.HasBinding(NativeBindingKeys.AdventureInventorySpaceCost),
            "unknown build retains exact signature-only read metadata");

        MemberInfo readMember;
        string reason;
        Assert(unknown.TryGetReadOnlyMember(NativeBindingKeys.AdventureInventorySpaceCost,
                out readMember, out reason)
               && readMember is MethodInfo,
            "read-only consumer can obtain its exact query member on an unknown hash");

        var result = unknown.CreateMutationAdapters().InvokeOrdinaryRebirth(null);
        Assert(result.Status == NativeInvocationStatus.HeldUnknownBuild,
            "unknown-build adapter holds before target checking or reflection dispatch");
        Assert(!result.InvocationAttempted,
            "unknown-build hold makes zero irreversible native calls");
    }

    private static void TestAdapterCatalogSurface(NativeBindingRegistry registry)
    {
        NativeBindingDescriptor descriptor;
        Assert(registry.TryGetDescriptor(NativeBindingKeys.RebirthCalculateTimeMulti,
                out descriptor)
               && descriptor.MetadataToken == 0x06000aa1
               && descriptor.ExactSignature == "System.Void Rebirth.calculateTimeMulti()",
            "rebirth preflight pins the discontinuous time-multiplier refresh");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.RebirthCalculateNextMultis,
                out descriptor)
               && descriptor.MetadataToken == 0x06000aa2
               && descriptor.ExactSignature == "System.Void Rebirth.calculateNextMultis()",
            "rebirth preflight pins the Blood-adjusted Number preview refresh");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.DifficultySelectEvil, out descriptor)
               && descriptor.MetadataToken == 0x06000a6e
               && registry.TryGetDescriptor(NativeBindingKeys.DifficultySelectSadistic,
                   out descriptor)
               && descriptor.MetadataToken == 0x06000a6f,
            "difficulty changes pin both gated selectors before exact reset entry");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.ItemConsume, out descriptor)
               && descriptor.ExactSignature == "System.Void ItemController.consumeItem()",
            "physical item consumption has an exact private adapter binding");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.CardConsume, out descriptor)
               && descriptor.ExactSignature
                   == "System.Void CardsController.tryConsumeCard(System.Int32)",
            "Card debit has an exact index-taking adapter binding");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.LoadIntoGame, out descriptor)
               && descriptor.ValueTypeName == "System.Boolean"
               && descriptor.ParameterTypeNames.Length == 1
               && descriptor.ParameterTypeNames[0] == "SaveData",
            "save replacement preserves the native Boolean result and exact SaveData argument");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.WandoosNextOs, out descriptor)
               && descriptor.Kind == NativeMemberKind.Field
               && descriptor.MetadataToken == 0x04000e36,
            "Wandoos composite adapter pins its ambient selector field");
        Assert(registry.TryGetDescriptor(NativeBindingKeys.YggFruitToBuy, out descriptor)
               && descriptor.Kind == NativeMemberKind.Field
               && descriptor.MetadataToken == 0x04000759,
            "Yggdrasil composite adapter pins its ambient selector field");
        Assert(registry.TryGetDescriptor(
                NativeBindingKeys.PurchaseMethod("ArbitraryController", "buyTagSlotAP"),
                out descriptor)
               && descriptor.MetadataToken == 0x060003da
               && descriptor.Visibility == NativeVisibility.Public,
            "AP purchases pin exact public method/token rather than a method name alone");

        var heartMethods = new[]
        {
            "buyHeartAP", "buyYellowHeartAP", "buyHeartBrown", "buyHeartGreenAP",
            "buyHeartBlueAP", "buyHeartPurpleAP", "buyHeartOrangeAP", "buyHeartGreyAP",
            "buyHeartPinkAP", "buyHeartRainbowAP"
        };
        for (var i = 0; i < heartMethods.Length; i++)
        {
            Assert(registry.HasBinding(NativeBindingKeys.PurchaseMethod(
                    "ArbitraryController", heartMethods[i])),
                "all ten Heart deliveries have exact native purchase bindings: "
                + heartMethods[i]);
        }
    }

    public static int Main()
    {
        try
        {
            var assembly = LoadInstalledAssembly();
            NativeBindingRegistry registry;
            TestKnownBuildBindsEveryContract(assembly, out registry);
            TestEngageOverloadsCannotAlias(assembly, registry);
            TestUnknownBuildIsReadOnly(assembly);
            TestAdapterCatalogSurface(registry);
            Console.WriteLine("Native binding contract tests passed: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
