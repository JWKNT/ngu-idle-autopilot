/*
FILE PURPOSE

Purpose: This file is the build-pinned reflection boundary for NGU Idle native members which the
autopilot cannot call directly.  It turns an installed-build assumption into an explicit contract:
an irreversible call is unavailable unless the game assembly SHA-256, module MVID, declaring type,
member name, exact parameter and return types, visibility, instance/static shape, and metadata token
all match the audited NGU Idle 1.260 assembly.

Mechanism: NativeBindingRegistry owns immutable member descriptors and binds them once against a
supplied Assembly. Known builds enforce every descriptor including metadata tokens. Unknown builds
do not bind or invoke derived-state writes or irreversible mutations; exact signature-only read
bindings remain available so telemetry can continue without granting mutation authority.
NativeMutationAdapters provides semantic adapters for reset, challenge, inventory, Money Pit,
Daily Spin, Titan one-frame execution/version selection, Wandoos, Card, save-load, Yggdrasil, AP
purchases, and exact one-level QP quirk purchases. An adapter result says only
whether reflection was attempted; it deliberately does not claim the game mutation committed.

Inputs and outputs: Inputs are the loaded Assembly-CSharp Assembly, its externally measured SHA-256,
native controller instances, exact selector values, and typed method arguments. Outputs are bound
MemberInfo objects for read-only consumers or NativeInvocationResult values for mutation callers.
This file never discovers Character, reads a save, writes telemetry, or schedules work.

Invariants and safety: Unsupported hash/MVID means all irreversible adapters fail closed. A partial
catalog bind also disables the entire irreversible surface, so one stale critical member cannot
leave a deceptively half-compatible deployment. Invocation exceptions are reported as
ThrewAfterInvocation because native code may already have committed a prefix; callers must recapture
state and must never automatically retry. Selector fields are restored in finally blocks. A returned
Invoked status is not a postcondition and must be settled by the mutation coordinator.

Extension points and non-goals: Add a descriptor and a narrow semantic adapter when a new reflected
native mutation is introduced, then pin its token/signature in NativeBindingContractTests. Read-only
descriptors may opt into signature-only binding on unknown builds. This registry does not decide
policy, prove preconditions, verify postconditions, compensate, quarantine, or replace the root
transaction/epoch coordinator.
*/
using System;
using System.Collections.Generic;
using System.Reflection;

namespace NGUInjector.Autopilot
{
    internal enum NativeMemberKind
    {
        Method,
        Field
    }

    internal enum NativeBindingScope
    {
        ReadOnly,
        DerivedStateWrite,
        IrreversibleMutation
    }

    internal enum NativeVisibility
    {
        Public,
        Private
    }

    internal enum NativeInvocationStatus
    {
        HeldUnknownBuild,
        HeldRegistryIncomplete,
        BindingUnavailable,
        TargetMismatch,
        ArgumentMismatch,
        Invoked,
        ThrewAfterInvocation
    }

    internal sealed class NativeBindingDescriptor
    {
        internal readonly string Key;
        internal readonly string DeclaringTypeName;
        internal readonly NativeMemberKind Kind;
        internal readonly string MemberName;
        internal readonly string[] ParameterTypeNames;
        internal readonly string ValueTypeName;
        internal readonly bool IsStatic;
        internal readonly NativeVisibility Visibility;
        internal readonly int MetadataToken;
        internal readonly NativeBindingScope Scope;
        internal readonly string SemanticContract;

        internal NativeBindingDescriptor(
            string key,
            string declaringTypeName,
            NativeMemberKind kind,
            string memberName,
            string[] parameterTypeNames,
            string valueTypeName,
            bool isStatic,
            NativeVisibility visibility,
            int metadataToken,
            NativeBindingScope scope,
            string semanticContract)
        {
            Key = key ?? string.Empty;
            DeclaringTypeName = declaringTypeName ?? string.Empty;
            Kind = kind;
            MemberName = memberName ?? string.Empty;
            ParameterTypeNames = parameterTypeNames == null
                ? new string[0] : (string[])parameterTypeNames.Clone();
            ValueTypeName = valueTypeName ?? string.Empty;
            IsStatic = isStatic;
            Visibility = visibility;
            MetadataToken = metadataToken;
            Scope = scope;
            SemanticContract = semanticContract ?? string.Empty;
        }

        internal string ExactSignature
        {
            get
            {
                if (Kind == NativeMemberKind.Field)
                    return ValueTypeName + " " + DeclaringTypeName + "." + MemberName;
                return ValueTypeName + " " + DeclaringTypeName + "." + MemberName
                       + "(" + string.Join(",", ParameterTypeNames) + ")";
            }
        }

        internal NativeBindingDescriptor WithMetadataToken(int token)
        {
            return new NativeBindingDescriptor(Key, DeclaringTypeName, Kind, MemberName,
                ParameterTypeNames, ValueTypeName, IsStatic, Visibility, token, Scope,
                SemanticContract);
        }
    }

    internal sealed class NativeInvocationResult
    {
        internal readonly NativeInvocationStatus Status;
        internal readonly string BindingKey;
        internal readonly string Reason;
        internal readonly object ReturnValue;
        internal readonly Exception Exception;

        internal NativeInvocationResult(NativeInvocationStatus status, string bindingKey,
            string reason, object returnValue, Exception exception)
        {
            Status = status;
            BindingKey = bindingKey ?? string.Empty;
            Reason = reason ?? string.Empty;
            ReturnValue = returnValue;
            Exception = exception;
        }

        internal bool InvocationAttempted
        {
            get
            {
                return Status == NativeInvocationStatus.Invoked
                       || Status == NativeInvocationStatus.ThrewAfterInvocation;
            }
        }

        internal bool ReturnedNormally { get { return Status == NativeInvocationStatus.Invoked; } }
    }

    internal static class NativeBindingKeys
    {
        internal const string RebirthEngage = "rebirth.engage.ordinary";
        internal const string RebirthEngageHard = "rebirth.engage.hard";
        internal const string RebirthCalculateTimeMulti = "rebirth.calculate-time-multi";
        internal const string RebirthCalculateNextMultis = "rebirth.calculate-next-multis";
        internal const string ChallengeBasic = "challenge.basic.engage";
        internal const string ChallengeNoAugs = "challenge.no-augs.engage";
        internal const string ChallengeTwentyFourHour = "challenge.24-hour.engage";
        internal const string ChallengeOneHundredLevel = "challenge.100-level.engage";
        internal const string ChallengeNoEquipment = "challenge.no-equipment.engage";
        internal const string ChallengeTroll = "challenge.troll.engage";
        internal const string ChallengeNoRebirth = "challenge.no-rebirth.engage";
        internal const string ChallengeLaserSword = "challenge.laser-sword.engage";
        internal const string ChallengeBlind = "challenge.blind.engage";
        internal const string ChallengeNoNgu = "challenge.no-ngu.engage";
        internal const string ChallengeNoTimeMachine = "challenge.no-time-machine.engage";
        internal const string DifficultyNormal = "difficulty.normal.start";
        internal const string DifficultyEvil = "difficulty.evil.start";
        internal const string DifficultySadistic = "difficulty.sadistic.start";
        internal const string DifficultySelectEvil = "difficulty.evil.select";
        internal const string DifficultySelectSadistic = "difficulty.sadistic.select";
        internal const string ItemConsume = "inventory.item.consume";
        internal const string TitanManageOneFrame = "titan.manage-one-native-frame";
        internal const string TitanEnterZone = "titan.enter-zone";
        internal const string MoneyPitEngage = "money-pit.engage";
        internal const string DailySpinClaim = "daily-spin.claim";
        internal const string WandoosNextOs = "wandoos.next-os";
        internal const string WandoosSetOs = "wandoos.set-os";
        internal const string YggFruitToBuy = "yggdrasil.fruit-to-buy";
        internal const string YggBuyFruit = "yggdrasil.buy-fruit";
        internal const string CardConsume = "cards.consume";
        internal const string LoadIntoGame = "save.load-into-game";
        internal const string AdventureInventorySpaceCost = "adventure.inventory-space-cost";
        internal const string ApPurchaseId = "ap.purchase-id";
        internal const string ApPurchaseName = "ap.purchase-name";
        internal const string QuirkTryLevelUp = "quirk.try-level-up";

        internal static string TitanVersion(int titanId)
        {
            if (titanId < 6 || titanId > 12)
                throw new ArgumentOutOfRangeException("titanId");
            return "titan." + titanId + ".selected-version";
        }

        internal static string PurchaseMethod(string declaringTypeName, string methodName)
        {
            return "purchase." + declaringTypeName + "." + methodName;
        }

        internal static string PurchaseInput(string declaringTypeName, string fieldName)
        {
            return "purchase-input." + declaringTypeName + "." + fieldName;
        }

        internal static string PurchaseInputUpdate(string declaringTypeName, string methodName)
        {
            return "purchase-input-update." + declaringTypeName + "." + methodName;
        }
    }

    internal sealed class NativeBindingRegistry
    {
        internal const string AuditedGameSha256 =
            "f138c8555f3e3aa9b6661b45e569258125a798ff77555d42eeeaa61fb71eaf71";
        internal static readonly Guid AuditedGameMvid =
            new Guid("5ba2e26b-de64-4a2e-b83a-4a5324f3752e");
        internal const string AuditedGameContract = "NGU Idle 1.260";

        private readonly Assembly _assembly;
        private readonly Dictionary<string, NativeBindingDescriptor> _descriptors;
        private readonly Dictionary<string, MemberInfo> _bindings;
        private readonly Dictionary<string, string> _failures;

        internal readonly string SuppliedSha256;
        internal readonly Guid AssemblyMvid;
        internal readonly bool IsKnownBuild;
        internal readonly string BuildFailureReason;
        internal readonly bool IrreversibleActionsEnabled;

        private NativeBindingRegistry(Assembly assembly, string suppliedSha256,
            NativeBindingDescriptor[] descriptors)
        {
            _assembly = assembly;
            SuppliedSha256 = NormalizeHash(suppliedSha256);
            AssemblyMvid = assembly == null ? Guid.Empty : assembly.ManifestModule.ModuleVersionId;
            IsKnownBuild = assembly != null
                           && string.Equals(SuppliedSha256, AuditedGameSha256,
                               StringComparison.OrdinalIgnoreCase)
                           && AssemblyMvid == AuditedGameMvid;
            BuildFailureReason = assembly == null ? "game assembly is null"
                : !string.Equals(SuppliedSha256, AuditedGameSha256,
                    StringComparison.OrdinalIgnoreCase)
                    ? "game assembly SHA-256 is not the audited build"
                    : AssemblyMvid != AuditedGameMvid
                        ? "game assembly MVID is not the audited build" : string.Empty;

            _descriptors = new Dictionary<string, NativeBindingDescriptor>(StringComparer.Ordinal);
            _bindings = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
            _failures = new Dictionary<string, string>(StringComparer.Ordinal);
            var irreversibleComplete = IsKnownBuild;
            for (var i = 0; i < descriptors.Length; i++)
            {
                var descriptor = descriptors[i];
                if (_descriptors.ContainsKey(descriptor.Key))
                {
                    _failures[descriptor.Key] = "duplicate binding key";
                    if (descriptor.Scope != NativeBindingScope.ReadOnly)
                        irreversibleComplete = false;
                    continue;
                }
                _descriptors.Add(descriptor.Key, descriptor);

                if (!IsKnownBuild && descriptor.Scope != NativeBindingScope.ReadOnly)
                {
                    _failures[descriptor.Key] = BuildFailureReason;
                    if (descriptor.Scope != NativeBindingScope.ReadOnly)
                        irreversibleComplete = false;
                    continue;
                }

                MemberInfo member;
                string failure;
                // Read-only members deliberately retain exact type/signature/visibility checking
                // on unknown builds, but a changed token alone does not suppress telemetry.
                var enforceToken = IsKnownBuild;
                if (!TryValidateDescriptor(assembly, descriptor, enforceToken, out member, out failure))
                {
                    _failures[descriptor.Key] = failure;
                    if (descriptor.Scope != NativeBindingScope.ReadOnly)
                        irreversibleComplete = false;
                    continue;
                }
                _bindings.Add(descriptor.Key, member);
            }
            IrreversibleActionsEnabled = irreversibleComplete;
        }

        internal static NativeBindingRegistry Create(Assembly gameAssembly, string gameAssemblySha256)
        {
            return new NativeBindingRegistry(gameAssembly, gameAssemblySha256, BuildDescriptors());
        }

        internal NativeMutationAdapters CreateMutationAdapters()
        {
            return new NativeMutationAdapters(this);
        }

        internal NativeBindingDescriptor[] AllDescriptors()
        {
            var values = new NativeBindingDescriptor[_descriptors.Count];
            _descriptors.Values.CopyTo(values, 0);
            return values;
        }

        internal bool TryGetDescriptor(string key, out NativeBindingDescriptor descriptor)
        {
            return _descriptors.TryGetValue(key, out descriptor);
        }

        internal bool HasBinding(string key)
        {
            return _bindings.ContainsKey(key);
        }

        internal string FailureFor(string key)
        {
            string failure;
            return _failures.TryGetValue(key, out failure) ? failure : string.Empty;
        }

        internal bool TryGetReadOnlyMember(string key, out MemberInfo member, out string reason)
        {
            member = null;
            reason = string.Empty;
            NativeBindingDescriptor descriptor;
            if (!_descriptors.TryGetValue(key, out descriptor))
            {
                reason = "binding key is not registered";
                return false;
            }
            if (descriptor.Scope != NativeBindingScope.ReadOnly)
            {
                reason = "binding is not read-only";
                return false;
            }
            if (!_bindings.TryGetValue(key, out member))
            {
                reason = FailureFor(key);
                return false;
            }
            return true;
        }

        internal NativeInvocationResult InvokeMutation(string key, object target, params object[] arguments)
        {
            NativeBindingDescriptor descriptor;
            if (!_descriptors.TryGetValue(key, out descriptor))
                return Result(NativeInvocationStatus.BindingUnavailable, key,
                    "binding key is not registered", null, null);
            if (descriptor.Scope == NativeBindingScope.ReadOnly)
                return Result(NativeInvocationStatus.BindingUnavailable, key,
                    "read-only member cannot be invoked through the mutation surface", null, null);
            if (!IsKnownBuild)
                return Result(NativeInvocationStatus.HeldUnknownBuild, key, BuildFailureReason, null, null);
            if (descriptor.Scope == NativeBindingScope.IrreversibleMutation
                && !IrreversibleActionsEnabled)
                return Result(NativeInvocationStatus.HeldRegistryIncomplete, key,
                    "one or more irreversible native bindings failed validation", null, null);

            MemberInfo member;
            if (!_bindings.TryGetValue(key, out member))
                return Result(NativeInvocationStatus.BindingUnavailable, key, FailureFor(key), null, null);
            return InvokeBound(descriptor, member, target, arguments);
        }

        internal NativeInvocationResult InvokeReadOnly(string key, object target, params object[] arguments)
        {
            NativeBindingDescriptor descriptor;
            if (!_descriptors.TryGetValue(key, out descriptor)
                || descriptor.Scope != NativeBindingScope.ReadOnly)
                return Result(NativeInvocationStatus.BindingUnavailable, key,
                    "read-only binding key is not registered", null, null);
            MemberInfo member;
            if (!_bindings.TryGetValue(key, out member))
                return Result(NativeInvocationStatus.BindingUnavailable, key, FailureFor(key), null, null);
            return InvokeBound(descriptor, member, target, arguments);
        }

        private NativeInvocationResult InvokeBound(NativeBindingDescriptor descriptor, MemberInfo member,
            object target, object[] arguments)
        {
            var declaringType = member.DeclaringType;
            if (!descriptor.IsStatic && (target == null || !declaringType.IsInstanceOfType(target)))
                return Result(NativeInvocationStatus.TargetMismatch, descriptor.Key,
                    "target is not an instance of " + descriptor.DeclaringTypeName, null, null);
            if (descriptor.Kind == NativeMemberKind.Field)
                return Result(NativeInvocationStatus.BindingUnavailable, descriptor.Key,
                    "field binding requires a semantic selector adapter", null, null);

            var method = (MethodInfo)member;
            var supplied = arguments ?? new object[0];
            if (supplied.Length != descriptor.ParameterTypeNames.Length)
                return Result(NativeInvocationStatus.ArgumentMismatch, descriptor.Key,
                    "argument count does not match exact native signature", null, null);
            try
            {
                var value = method.Invoke(target, supplied);
                return Result(NativeInvocationStatus.Invoked, descriptor.Key,
                    "native method returned; postcondition is not yet proven", value, null);
            }
            catch (Exception error)
            {
                var reported = error is TargetInvocationException && error.InnerException != null
                    ? error.InnerException : error;
                return Result(NativeInvocationStatus.ThrewAfterInvocation, descriptor.Key,
                    "native invocation threw after reflection dispatch; recapture and do not retry",
                    null, reported);
            }
        }

        internal bool TryGetBoundField(string key, out FieldInfo field, out string reason)
        {
            field = null;
            reason = string.Empty;
            NativeBindingDescriptor descriptor;
            MemberInfo member;
            if (!_descriptors.TryGetValue(key, out descriptor)
                || descriptor.Kind != NativeMemberKind.Field)
            {
                reason = "field binding key is not registered";
                return false;
            }
            if (!IsKnownBuild && descriptor.Scope != NativeBindingScope.ReadOnly)
            {
                reason = BuildFailureReason;
                return false;
            }
            if (!_bindings.TryGetValue(key, out member))
            {
                reason = FailureFor(key);
                return false;
            }
            field = (FieldInfo)member;
            return true;
        }

        internal static bool TryValidateDescriptor(Assembly assembly,
            NativeBindingDescriptor descriptor, bool enforceMetadataToken,
            out MemberInfo member, out string failure)
        {
            member = null;
            failure = string.Empty;
            if (assembly == null || descriptor == null)
            {
                failure = "assembly or descriptor is null";
                return false;
            }
            var declaringType = assembly.GetType(descriptor.DeclaringTypeName, false);
            if (declaringType == null)
            {
                failure = "declaring type not found: " + descriptor.DeclaringTypeName;
                return false;
            }

            var flags = BindingFlags.DeclaredOnly
                        | (descriptor.IsStatic ? BindingFlags.Static : BindingFlags.Instance)
                        | (descriptor.Visibility == NativeVisibility.Public
                            ? BindingFlags.Public : BindingFlags.NonPublic);
            if (descriptor.Kind == NativeMemberKind.Method)
            {
                var parameterTypes = new Type[descriptor.ParameterTypeNames.Length];
                for (var i = 0; i < parameterTypes.Length; i++)
                {
                    parameterTypes[i] = ResolveType(assembly, descriptor.ParameterTypeNames[i]);
                    if (parameterTypes[i] == null)
                    {
                        failure = "parameter type not found: " + descriptor.ParameterTypeNames[i];
                        return false;
                    }
                }
                var method = declaringType.GetMethod(descriptor.MemberName, flags, null,
                    parameterTypes, null);
                if (method == null)
                {
                    failure = "exact method not found: " + descriptor.ExactSignature;
                    return false;
                }
                if (method.ReturnType.FullName != descriptor.ValueTypeName)
                {
                    failure = "return type mismatch for " + descriptor.ExactSignature;
                    return false;
                }
                member = method;
            }
            else
            {
                var field = declaringType.GetField(descriptor.MemberName, flags);
                if (field == null)
                {
                    failure = "exact field not found: " + descriptor.ExactSignature;
                    return false;
                }
                if (field.FieldType.FullName != descriptor.ValueTypeName)
                {
                    failure = "field type mismatch for " + descriptor.ExactSignature;
                    return false;
                }
                member = field;
            }

            var isStatic = descriptor.Kind == NativeMemberKind.Method
                ? ((MethodInfo)member).IsStatic : ((FieldInfo)member).IsStatic;
            if (isStatic != descriptor.IsStatic)
            {
                failure = "static/instance mismatch for " + descriptor.ExactSignature;
                member = null;
                return false;
            }
            var isPublic = descriptor.Kind == NativeMemberKind.Method
                ? ((MethodInfo)member).IsPublic : ((FieldInfo)member).IsPublic;
            if (isPublic != (descriptor.Visibility == NativeVisibility.Public))
            {
                failure = "visibility mismatch for " + descriptor.ExactSignature;
                member = null;
                return false;
            }
            if (enforceMetadataToken && member.MetadataToken != descriptor.MetadataToken)
            {
                failure = "metadata token mismatch for " + descriptor.ExactSignature
                          + ": expected 0x" + descriptor.MetadataToken.ToString("x8")
                          + ", observed 0x" + member.MetadataToken.ToString("x8");
                member = null;
                return false;
            }
            return true;
        }

        private static Type ResolveType(Assembly gameAssembly, string fullName)
        {
            if (fullName == typeof(void).FullName) return typeof(void);
            if (fullName == typeof(bool).FullName) return typeof(bool);
            if (fullName == typeof(int).FullName) return typeof(int);
            if (fullName == typeof(long).FullName) return typeof(long);
            if (fullName == typeof(string).FullName) return typeof(string);
            var type = gameAssembly.GetType(fullName, false);
            if (type != null) return type;
            type = Type.GetType(fullName, false);
            if (type != null) return type;
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < loaded.Length; i++)
            {
                type = loaded[i].GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static NativeInvocationResult Result(NativeInvocationStatus status, string key,
            string reason, object value, Exception error)
        {
            return new NativeInvocationResult(status, key, reason, value, error);
        }

        private static string NormalizeHash(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty
                : value.Trim().Replace("-", string.Empty).ToLowerInvariant();
        }

        /*
        BUILD-PINNED NATIVE CATALOG

        Tokens below were read from the installed Assembly-CSharp.dll whose SHA-256/MVID are the
        constants above. Every reflected irreversible method used by the current bot is included,
        plus the direct Card/save/difficulty boundaries which audit 19 requires to migrate behind
        the same adapter. Purchase selector/input fields are catalogued because invoking a correctly
        named method with a stale ambient selector is still the wrong irreversible transaction.
        */
        private static NativeBindingDescriptor[] BuildDescriptors()
        {
            var descriptors = new List<NativeBindingDescriptor>();
            descriptors.Add(Method(NativeBindingKeys.RebirthEngage, "Rebirth", "engage",
                Empty(), VoidName, NativeVisibility.Private, 0x06000a71,
                NativeBindingScope.IrreversibleMutation, "ordinary rebirth"));
            descriptors.Add(Method(NativeBindingKeys.RebirthEngageHard, "Rebirth", "engage",
                Types(BoolName), VoidName, NativeVisibility.Private, 0x06000a72,
                NativeBindingScope.IrreversibleMutation, "hard-reset rebirth primitive"));
            descriptors.Add(Method(NativeBindingKeys.RebirthCalculateTimeMulti, "Rebirth",
                "calculateTimeMulti", Empty(), VoidName, NativeVisibility.Private, 0x06000aa1,
                NativeBindingScope.DerivedStateWrite, "refresh discontinuous rebirth time multiplier"));
            descriptors.Add(Method(NativeBindingKeys.RebirthCalculateNextMultis, "Rebirth",
                "calculateNextMultis", Empty(), VoidName, NativeVisibility.Private, 0x06000aa2,
                NativeBindingScope.DerivedStateWrite, "refresh Blood-adjusted Number preview"));

            AddChallenge(descriptors, NativeBindingKeys.ChallengeBasic,
                "engageBasicChallenge", 0x06000a7c, "Basic");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeNoAugs,
                "engageNoAugsChallenge", 0x06000a7e, "No Augments");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeTwentyFourHour,
                "engage24HourChallenge", 0x06000a80, "24 Hour");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeOneHundredLevel,
                "engagelevel100Challenge", 0x06000a82, "100 Level");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeNoEquipment,
                "engageNoEquipChallenge", 0x06000a84, "No Equipment");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeNoRebirth,
                "engageNoRebirthChallenge", 0x06000a86, "No Rebirth");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeTroll,
                "engageTrollChallenge", 0x06000a88, "Troll");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeLaserSword,
                "engageLaserSwordChallenge", 0x06000a8a, "Laser Sword");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeBlind,
                "engageBlindChallenge", 0x06000a8c, "Blind");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeNoNgu,
                "engageNGUChallenge", 0x06000a8e, "No NGU");
            AddChallenge(descriptors, NativeBindingKeys.ChallengeNoTimeMachine,
                "engageTimeMachineChallenge", 0x06000a90, "No Time Machine");

            descriptors.Add(Method(NativeBindingKeys.DifficultyNormal, "Rebirth",
                "startNormalRebirth", Empty(), VoidName, NativeVisibility.Public, 0x06000a92,
                NativeBindingScope.IrreversibleMutation, "Normal difficulty hard reset"));
            descriptors.Add(Method(NativeBindingKeys.DifficultyEvil, "Rebirth",
                "startHardRebirth", Empty(), VoidName, NativeVisibility.Public, 0x06000a93,
                NativeBindingScope.IrreversibleMutation, "Evil difficulty hard reset"));
            descriptors.Add(Method(NativeBindingKeys.DifficultySadistic, "Rebirth",
                "startSadisticRebirth", Empty(), VoidName, NativeVisibility.Public, 0x06000a94,
                NativeBindingScope.IrreversibleMutation, "Sadistic difficulty hard reset"));
            descriptors.Add(Method(NativeBindingKeys.DifficultySelectEvil, "Rebirth",
                "setEvilNextRebirth", Empty(), VoidName, NativeVisibility.Public, 0x06000a6e,
                NativeBindingScope.DerivedStateWrite, "gated Evil difficulty selector"));
            descriptors.Add(Method(NativeBindingKeys.DifficultySelectSadistic, "Rebirth",
                "setSadisticNextRebirth", Empty(), VoidName, NativeVisibility.Public, 0x06000a6f,
                NativeBindingScope.DerivedStateWrite, "gated Sadistic difficulty selector"));

            descriptors.Add(Method(NativeBindingKeys.ItemConsume, "ItemController", "consumeItem",
                Empty(), VoidName, NativeVisibility.Private, 0x06000ed2,
                NativeBindingScope.IrreversibleMutation, "consume exact selected physical item"));
            descriptors.Add(Method(NativeBindingKeys.TitanManageOneFrame,
                "AdventureController", "manageFight", Empty(), VoidName,
                NativeVisibility.Private, 0x0600008e,
                NativeBindingScope.IrreversibleMutation,
                "evaluate T1-T12 in native ascending order, kill at most one due Titan, reset its clock, and run its loot path"));
            descriptors.Add(Method(NativeBindingKeys.TitanEnterZone, "ZoneSelector",
                "changeZone", Types(IntName), VoidName, NativeVisibility.Public,
                0x060002cd, NativeBindingScope.IrreversibleMutation,
                "enter the exact zero-based Adventure zone for staged terminal-Titan combat"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(6), "Adventure",
                "titan6Version", IntName, NativeVisibility.Public, 0x0400005a,
                NativeBindingScope.DerivedStateWrite, "select T6 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(7), "Adventure",
                "titan7Version", IntName, NativeVisibility.Public, 0x04000067,
                NativeBindingScope.DerivedStateWrite, "select T7 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(8), "Adventure",
                "titan8Version", IntName, NativeVisibility.Public, 0x04000074,
                NativeBindingScope.DerivedStateWrite, "select T8 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(9), "Adventure",
                "titan9Version", IntName, NativeVisibility.Public, 0x04000086,
                NativeBindingScope.DerivedStateWrite, "select T9 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(10), "Adventure",
                "titan10Version", IntName, NativeVisibility.Public, 0x04000092,
                NativeBindingScope.DerivedStateWrite, "select T10 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(11), "Adventure",
                "titan11Version", IntName, NativeVisibility.Public, 0x0400009c,
                NativeBindingScope.DerivedStateWrite, "select T11 reward/enemy version 0-3"));
            descriptors.Add(Field(NativeBindingKeys.TitanVersion(12), "Adventure",
                "titan12Version", IntName, NativeVisibility.Public, 0x040000a6,
                NativeBindingScope.DerivedStateWrite, "select T12 reward/enemy version 0-3"));
            descriptors.Add(Method(NativeBindingKeys.MoneyPitEngage, "PitController", "engage",
                Empty(), VoidName, NativeVisibility.Private, 0x06000633,
                NativeBindingScope.IrreversibleMutation, "debit Gold and claim Money Pit reward"));
            descriptors.Add(Method(NativeBindingKeys.DailySpinClaim, "DailyRewardController",
                "startNoBullshitSpin", Empty(), VoidName, NativeVisibility.Public, 0x06000bb7,
                NativeBindingScope.IrreversibleMutation,
                "debit one free spin or exactly 86400 timer seconds, advance saved RNG, and grant one reward"));
            descriptors.Add(Field(NativeBindingKeys.WandoosNextOs, "Wandoos98Controller", "nextOS",
                IntName, NativeVisibility.Private, 0x04000e36,
                NativeBindingScope.IrreversibleMutation, "Wandoos target selector"));
            descriptors.Add(Method(NativeBindingKeys.WandoosSetOs, "Wandoos98Controller", "setOSType",
                Empty(), VoidName, NativeVisibility.Private, 0x060012b2,
                NativeBindingScope.IrreversibleMutation, "switch OS and clear Wandoos run levels"));
            descriptors.Add(Field(NativeBindingKeys.YggFruitToBuy, "YggdrasilEXPPurchases", "fruitToBuy",
                IntName, NativeVisibility.Private, 0x04000759,
                NativeBindingScope.IrreversibleMutation, "Yggdrasil permanent-purchase selector"));
            descriptors.Add(Method(NativeBindingKeys.YggBuyFruit, "YggdrasilEXPPurchases", "buyFruit",
                Empty(), VoidName, NativeVisibility.Private, 0x060009fc,
                NativeBindingScope.IrreversibleMutation, "debit EXP for selected permanent fruit"));
            descriptors.Add(Method(NativeBindingKeys.CardConsume, "CardsController", "tryConsumeCard",
                Types(IntName), VoidName, NativeVisibility.Public, 0x060006d3,
                NativeBindingScope.IrreversibleMutation, "debit exact Card index and Mayo"));
            descriptors.Add(Method(NativeBindingKeys.LoadIntoGame, "OpenFileDialog", "loadintoGame",
                Types("SaveData"), BoolName, NativeVisibility.Public, 0x06001255,
                NativeBindingScope.IrreversibleMutation, "replace live save/controller state"));
            descriptors.Add(Method(NativeBindingKeys.AdventureInventorySpaceCost, "AdventurePurchases",
                "invSpaceCost", Empty(), IntName, NativeVisibility.Private, 0x060008ec,
                NativeBindingScope.ReadOnly, "read current EXP inventory-space price"));

            descriptors.Add(Field(NativeBindingKeys.ApPurchaseId, "ArbitraryController", "id",
                IntName, NativeVisibility.Public, 0x0400030c,
                NativeBindingScope.IrreversibleMutation, "AP shop exact purchase ID selector"));
            descriptors.Add(Field(NativeBindingKeys.ApPurchaseName, "ArbitraryController", "itemName",
                StringName, NativeVisibility.Public, 0x0400030d,
                NativeBindingScope.IrreversibleMutation, "AP shop purchase-name guard selector"));
            descriptors.Add(Method(NativeBindingKeys.QuirkTryLevelUp,
                "BeastQuestPerkController", "tryLevelUp", Types(IntName), VoidName,
                NativeVisibility.Public, 0x0600051c, NativeBindingScope.IrreversibleMutation,
                "recheck quirk cap, QP, difficulty and feature gates, then buy exactly one level"));

            AddPurchaseCatalog(descriptors);
            return descriptors.ToArray();
        }

        private static void AddChallenge(List<NativeBindingDescriptor> descriptors, string key,
            string methodName, int token, string label)
        {
            descriptors.Add(Method(key, "Rebirth", methodName, Empty(), VoidName,
                NativeVisibility.Private, token, NativeBindingScope.IrreversibleMutation,
                "enter exact " + label + " challenge"));
        }

        private static void AddPurchaseCatalog(List<NativeBindingDescriptor> descriptors)
        {
            // Current dynamically reflected EXP purchases.
            AddPurchaseMethods(descriptors, "AdventurePurchases", new[]
            {
                P("buy1Attack", 0x060008ef), P("buy1Defense", 0x060008f9),
                P("buy1HPRegen", 0x0600090d), P("buyInventorySpace", 0x06000917),
                P("buyFilter", 0x06000919), P("buyAcc3", 0x0600091b),
                P("buyRecycleBoost", 0x0600091d), P("buyAutoMerge", 0x0600091f),
                P("buyAcc5", 0x06000923), P("buyDaycare", 0x06000929),
                P("buyDaycareSlot2", 0x0600092b), P("buyDaycareSlot3", 0x0600092d),
                P("buyInvMergeUnlock", 0x0600092f)
            });
            AddPurchaseMethods(descriptors, "StatBoostPurchases", new[]
            {
                P("buyAttack10", 0x060009e1), P("buyDefense10", 0x060009e7)
            });
            AddPurchaseMethods(descriptors, "MiscPurchases", new[]
            {
                P("buyAutoAdvance", 0x060009bf), P("buybeard1", 0x060009c9),
                P("buydigger1", 0x060009cb), P("buyMacguffin1", 0x060009cd)
            });
            AddPurchaseMethods(descriptors, "EnergyPurchases", new[]
            {
                P("buyEnergySpeed10", 0x06000955), P("buyEnergySpeed100", 0x06000957),
                P("buyEnergyBar1", 0x06000959), P("buyEnergySpeedSpecial1", 0x0600095b),
                P("buyEnergySpeedSpecial2", 0x0600095d), P("buyEnergySpeedSpecial3", 0x0600095f),
                P("buyEnergyPower01", 0x0600096b), P("buyCustomPower", 0x06000973),
                P("buyCustomBar", 0x06000975), P("buyCustomCap", 0x06000977),
                P("buyCustomAll", 0x06000979)
            });
            AddPurchaseMethods(descriptors, "MagicPurchases", new[]
            {
                P("buy10MagicSpeed", 0x06000992), P("buyCustomPower", 0x060009aa),
                P("buyCustomBar", 0x060009ac), P("buyCustomCap", 0x060009ae),
                P("buyCustomAll", 0x060009b7)
            });
            AddPurchaseMethods(descriptors, "Resource3Purchases", new[]
            {
                P("buyCustomPower", 0x06000aed), P("buyCustomBar", 0x06000aef),
                P("buyCustomCap", 0x06000af1), P("buyCustomAll", 0x06000afa)
            });

            AddPurchaseInputs(descriptors, "EnergyPurchases",
                0x040006de, 0x040006e0, 0x040006e2,
                0x0600097e, 0x0600097f, 0x06000980);
            AddPurchaseInputs(descriptors, "MagicPurchases",
                0x0400070a, 0x0400070c, 0x0400070e,
                0x060009b3, 0x060009b4, 0x060009b5);
            AddPurchaseInputs(descriptors, "Resource3Purchases",
                0x040007fd, 0x040007ff, 0x04000801,
                0x06000af6, 0x06000af7, 0x06000af8);

            AddPurchaseMethods(descriptors, "ArbitraryController", new[]
            {
                P("buyEnergyPotion1AP", 0x0600033b), P("buyEnergyPotion2AP", 0x0600033d),
                P("buyEnergyPotion3", 0x0600033f), P("buyMagicPotion1AP", 0x06000341),
                P("buyMagicPotion2AP", 0x06000343), P("buyMagicPotion3", 0x06000345),
                P("buyRes3Potion1", 0x06000347), P("buyRes3Potion2", 0x06000349),
                P("buyRes3Potion3", 0x0600034b), P("buyLootCharm1AP", 0x0600034d),
                P("buyEnergyBarBar1AP", 0x0600034f), P("buyMagicBarBar1AP", 0x06000351),
                P("buyLootFilterAP", 0x06000353), P("buyAutoBoostMergeAP", 0x06000355),
                P("buyInstaTrainAP", 0x06000357), P("buy500ExpAP", 0x06000359),
                P("buy200ExpAP", 0x0600035b), P("buy2KExpAP", 0x0600035d),
                P("buyHeartAP", 0x0600035f), P("buyCustomPercent1AP", 0x06000361),
                P("buyCustomPercent2AP", 0x06000363), P("buyCustomIdlePercent1AP", 0x06000365),
                P("buyRes3Percent1AP", 0x06000367), P("buyRes3Percent2AP", 0x06000369),
                P("buyRes3IdlePercent1AP", 0x0600036b), P("buyYellowHeartAP", 0x0600036d),
                P("buyInventoryAP", 0x0600036f), P("buyStarterPackAP", 0x06000373),
                P("buyAcc4AP", 0x06000375), P("buyAcc5AP", 0x06000377),
                P("buyAcc6AP", 0x06000379), P("buyAcc7AP", 0x0600037b),
                P("buyAcc8AP", 0x0600037d), P("buyAcc9AP", 0x0600037f),
                P("buyPoop1AP", 0x06000381), P("buyPoop10AP", 0x06000383),
                P("buyPoop100AP", 0x06000385),
                P("buyYggReminderAP", 0x06000387), P("buyExtendedSpinBankAP", 0x06000389),
                P("buyLoadoutSlotAP", 0x0600038b), P("buyBeardAP", 0x0600038d),
                P("buyCubeFilterAP", 0x0600038f), P("buyLootCharm2AP", 0x06000391),
                P("buyHeartBrown", 0x06000393), P("buyDaycareSpeedAP", 0x06000395),
                P("buyHeartGreenAP", 0x06000397), P("buyPill1AP", 0x0600039a),
                P("buyPill10AP", 0x0600039c), P("buyPill100AP", 0x0600039e),
                P("buyHeartBlueAP", 0x060003a0), P("buyLazyITOPODAP", 0x060003a2),
                P("buyDiggerSlotAP", 0x060003a4), P("buyMacguffinSlotAP", 0x060003a6),
                P("buyHeartPurpleAP", 0x060003a8), P("buyHeartGreyAP", 0x060003aa),
                P("buyMacguffinBooster1AP", 0x060003ac), P("buyBeastButter1AP", 0x060003ae),
                P("buyBeastButter10AP", 0x060003b0), P("buyBeastButter100AP", 0x060003b2),
                P("buyQuestLightAP", 0x060003b4),
                P("buyFasterQuests1AP", 0x060003b6), P("buyExtendedQuestBankAP", 0x060003b8),
                P("buyHeartOrangeAP", 0x060003ba), P("buy25ppAP", 0x060003bc),
                P("buy100ppAP", 0x060003be), P("buy500ppAP", 0x060003c0),
                P("buyAutoNukeAP", 0x060003c2), P("buyDaycareArtAP", 0x060003c4),
                P("buyNGUCapModifierAP", 0x060003c6), P("buyRes3NameGeneratorAP", 0x060003c8),
                P("buyFasterWishAP", 0x060003ca), P("buyInvMergeSlotAP", 0x060003cc),
                P("buyHeartPinkAP", 0x060003ce),
                P("buyAdvLightAP", 0x060003d0), P("buyAdvAdvancerAP", 0x060003d2),
                P("buyGoToQuestAP", 0x060003d4), P("buyDeckSlotAP", 0x060003d6),
                P("buyMayoGenAP", 0x060003d8), P("buyTagSlotAP", 0x060003da),
                P("buyMayoSpeedConsumableAP", 0x060003dc),
                P("buyCardTierConsumableAP", 0x060003de),
                P("buyHeartRainbowAP", 0x060003e0)
            }, NativeVisibility.Public);
        }

        private static void AddPurchaseMethods(List<NativeBindingDescriptor> descriptors,
            string declaringTypeName, PurchaseMethodToken[] methods)
        {
            AddPurchaseMethods(descriptors, declaringTypeName, methods, NativeVisibility.Private);
        }

        private static void AddPurchaseMethods(List<NativeBindingDescriptor> descriptors,
            string declaringTypeName, PurchaseMethodToken[] methods, NativeVisibility visibility)
        {
            for (var i = 0; i < methods.Length; i++)
            {
                descriptors.Add(Method(
                    NativeBindingKeys.PurchaseMethod(declaringTypeName, methods[i].Name),
                    declaringTypeName, methods[i].Name, Empty(), VoidName,
                    visibility, methods[i].Token,
                    NativeBindingScope.IrreversibleMutation,
                    "debit permanent currency for exact native purchase"));
            }
        }

        private static void AddPurchaseInputs(List<NativeBindingDescriptor> descriptors,
            string declaringTypeName, int powerField, int capField, int barField,
            int powerUpdate, int capUpdate, int barUpdate)
        {
            var inputType = "UnityEngine.UI.InputField";
            descriptors.Add(Field(NativeBindingKeys.PurchaseInput(declaringTypeName, "powerInput"),
                declaringTypeName, "powerInput", inputType, NativeVisibility.Public, powerField,
                NativeBindingScope.DerivedStateWrite, "exact custom purchase power selector"));
            descriptors.Add(Field(NativeBindingKeys.PurchaseInput(declaringTypeName, "capInput"),
                declaringTypeName, "capInput", inputType, NativeVisibility.Public, capField,
                NativeBindingScope.DerivedStateWrite, "exact custom purchase cap selector"));
            descriptors.Add(Field(NativeBindingKeys.PurchaseInput(declaringTypeName, "barInput"),
                declaringTypeName, "barInput", inputType, NativeVisibility.Public, barField,
                NativeBindingScope.DerivedStateWrite, "exact custom purchase bar selector"));
            descriptors.Add(Method(NativeBindingKeys.PurchaseInputUpdate(declaringTypeName,
                    "updateCustomPowerInput"), declaringTypeName, "updateCustomPowerInput",
                Empty(), VoidName, NativeVisibility.Public, powerUpdate,
                NativeBindingScope.DerivedStateWrite, "parse exact custom power selector"));
            descriptors.Add(Method(NativeBindingKeys.PurchaseInputUpdate(declaringTypeName,
                    "updateCustomCapInput"), declaringTypeName, "updateCustomCapInput",
                Empty(), VoidName, NativeVisibility.Public, capUpdate,
                NativeBindingScope.DerivedStateWrite, "parse exact custom cap selector"));
            descriptors.Add(Method(NativeBindingKeys.PurchaseInputUpdate(declaringTypeName,
                    "updateCustomBarInput"), declaringTypeName, "updateCustomBarInput",
                Empty(), VoidName, NativeVisibility.Public, barUpdate,
                NativeBindingScope.DerivedStateWrite, "parse exact custom bar selector"));
        }

        private sealed class PurchaseMethodToken
        {
            internal readonly string Name;
            internal readonly int Token;

            internal PurchaseMethodToken(string name, int token)
            {
                Name = name;
                Token = token;
            }
        }

        private static PurchaseMethodToken P(string name, int token)
        {
            return new PurchaseMethodToken(name, token);
        }

        private const string VoidName = "System.Void";
        private const string BoolName = "System.Boolean";
        private const string IntName = "System.Int32";
        private const string LongName = "System.Int64";
        private const string StringName = "System.String";

        private static string[] Empty() { return new string[0]; }
        private static string[] Types(params string[] names) { return names; }

        private static NativeBindingDescriptor Method(string key, string typeName,
            string methodName, string[] parameters, string returnType, NativeVisibility visibility,
            int token, NativeBindingScope scope, string contract)
        {
            return new NativeBindingDescriptor(key, typeName, NativeMemberKind.Method, methodName,
                parameters, returnType, false, visibility, token, scope,
                AuditedGameContract + ": " + contract);
        }

        private static NativeBindingDescriptor Field(string key, string typeName,
            string fieldName, string fieldType, NativeVisibility visibility, int token,
            NativeBindingScope scope, string contract)
        {
            return new NativeBindingDescriptor(key, typeName, NativeMemberKind.Field, fieldName,
                Empty(), fieldType, false, visibility, token, scope,
                AuditedGameContract + ": " + contract);
        }
    }

    internal enum NativeChallengeCall
    {
        Basic,
        NoAugs,
        TwentyFourHour,
        OneHundredLevel,
        NoEquipment,
        Troll,
        NoRebirth,
        LaserSword,
        Blind,
        NoNgu,
        NoTimeMachine
    }

    internal enum NativeDifficultyCall
    {
        Normal,
        Evil,
        Sadistic
    }

    /*
    SEMANTIC MUTATION ADAPTERS

    These methods remove string/name-only reflection from future callers. Composite selector calls
    resolve every member before changing the selector and restore ambient selector state in finally.
    They intentionally do not catch/translate a normal-return no-op into success: the coordinator
    must use an exact before/after proof before publishing Committed.
    */
    internal sealed class NativeMutationAdapters
    {
        private readonly NativeBindingRegistry _registry;

        internal NativeMutationAdapters(NativeBindingRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            _registry = registry;
        }

        internal NativeInvocationResult InvokeOrdinaryRebirth(object rebirth)
        {
            return _registry.InvokeMutation(NativeBindingKeys.RebirthEngage, rebirth);
        }

        internal NativeInvocationResult InvokeHardRebirthPrimitive(object rebirth)
        {
            return _registry.InvokeMutation(NativeBindingKeys.RebirthEngageHard, rebirth, true);
        }

        internal NativeInvocationResult RefreshRebirthPreview(object rebirth)
        {
            return _registry.InvokeMutation(NativeBindingKeys.RebirthCalculateNextMultis, rebirth);
        }

        internal NativeInvocationResult RefreshRebirthTimeMultiplier(object rebirth)
        {
            return _registry.InvokeMutation(NativeBindingKeys.RebirthCalculateTimeMulti, rebirth);
        }

        internal NativeInvocationResult EnterChallenge(object rebirth, NativeChallengeCall challenge)
        {
            string key;
            switch (challenge)
            {
                case NativeChallengeCall.Basic: key = NativeBindingKeys.ChallengeBasic; break;
                case NativeChallengeCall.NoAugs: key = NativeBindingKeys.ChallengeNoAugs; break;
                case NativeChallengeCall.TwentyFourHour:
                    key = NativeBindingKeys.ChallengeTwentyFourHour; break;
                case NativeChallengeCall.OneHundredLevel:
                    key = NativeBindingKeys.ChallengeOneHundredLevel; break;
                case NativeChallengeCall.NoEquipment:
                    key = NativeBindingKeys.ChallengeNoEquipment; break;
                case NativeChallengeCall.Troll: key = NativeBindingKeys.ChallengeTroll; break;
                case NativeChallengeCall.NoRebirth:
                    key = NativeBindingKeys.ChallengeNoRebirth; break;
                case NativeChallengeCall.LaserSword:
                    key = NativeBindingKeys.ChallengeLaserSword; break;
                case NativeChallengeCall.Blind: key = NativeBindingKeys.ChallengeBlind; break;
                case NativeChallengeCall.NoNgu: key = NativeBindingKeys.ChallengeNoNgu; break;
                case NativeChallengeCall.NoTimeMachine:
                    key = NativeBindingKeys.ChallengeNoTimeMachine; break;
                default:
                    return Invalid("challenge call is outside the exact catalog");
            }
            return _registry.InvokeMutation(key, rebirth);
        }

        internal NativeInvocationResult StartDifficulty(object rebirth, NativeDifficultyCall difficulty)
        {
            var key = difficulty == NativeDifficultyCall.Normal ? NativeBindingKeys.DifficultyNormal
                : difficulty == NativeDifficultyCall.Evil ? NativeBindingKeys.DifficultyEvil
                : difficulty == NativeDifficultyCall.Sadistic ? NativeBindingKeys.DifficultySadistic
                : string.Empty;
            return string.IsNullOrEmpty(key)
                ? Invalid("difficulty call is outside the exact catalog")
                : _registry.InvokeMutation(key, rebirth);
        }

        internal NativeInvocationResult SelectDifficulty(object rebirth, NativeDifficultyCall difficulty)
        {
            var key = difficulty == NativeDifficultyCall.Evil ? NativeBindingKeys.DifficultySelectEvil
                : difficulty == NativeDifficultyCall.Sadistic
                    ? NativeBindingKeys.DifficultySelectSadistic
                    : string.Empty;
            return string.IsNullOrEmpty(key)
                ? Invalid("only gated Evil/Sadistic selectors are catalogued")
                : _registry.InvokeMutation(key, rebirth);
        }

        internal NativeInvocationResult ConsumeItem(object itemController)
        {
            return _registry.InvokeMutation(NativeBindingKeys.ItemConsume, itemController);
        }

        /*
        TYPED TITAN NATIVE BOUNDARY

        AdventureController.manageFight is the audited native update primitive, not a generic
        combat helper. Its T1-T12 branches are ordered and each successful branch returns after
        the selected-version Bestiary increment, exact clock reset, and native loot call. The
        execution coordinator temporarily raises autoKillTitans only around this synchronous call,
        then proves the one-target counter/clock delta; the adapter itself never claims a kill.

        Version selection intentionally writes the exact Adventure field instead of calling
        changeTitanDifficulty(int): that native UI method switches on the ambient adventure zone,
        so using it from a background executor could mutate the wrong Titan. T1-T5 and T13-T14 do
        not have version selectors and are rejected here.
        */
        internal NativeInvocationResult InvokeOneTitanFrame(object adventureController)
        {
            return _registry.InvokeMutation(NativeBindingKeys.TitanManageOneFrame,
                adventureController);
        }

        internal NativeInvocationResult EnterTitanZone(object zoneSelector,
            int zeroBasedZone)
        {
            if (zeroBasedZone < 0)
                return Invalid("terminal Titan zone must be nonnegative");
            return _registry.InvokeMutation(NativeBindingKeys.TitanEnterZone,
                zoneSelector, zeroBasedZone);
        }

        internal NativeInvocationResult SelectTitanVersion(object adventure,
            int titanId, int zeroBasedVersion)
        {
            if (titanId < 6 || titanId > 12 || zeroBasedVersion < 0
                || zeroBasedVersion > 3)
                return Invalid("Titan selector requires T6-T12 and a zero-based version 0-3");
            var key = NativeBindingKeys.TitanVersion(titanId);
            if (!_registry.IsKnownBuild || !_registry.IrreversibleActionsEnabled)
                return new NativeInvocationResult(_registry.IsKnownBuild
                        ? NativeInvocationStatus.HeldRegistryIncomplete
                        : NativeInvocationStatus.HeldUnknownBuild,
                    key, _registry.IsKnownBuild
                        ? "one or more native bindings failed validation"
                        : _registry.BuildFailureReason, null, null);
            FieldInfo selector;
            string reason;
            if (!_registry.TryGetBoundField(key, out selector, out reason))
                return Unavailable(key, reason);
            if (adventure == null || !selector.DeclaringType.IsInstanceOfType(adventure))
                return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    key, "Titan version target is not an Adventure instance", null, null);
            try
            {
                selector.SetValue(adventure, zeroBasedVersion);
                return new NativeInvocationResult(NativeInvocationStatus.Invoked, key,
                    "exact Titan version field written; caller must verify the selected version",
                    null, null);
            }
            catch (Exception error)
            {
                return new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    key, "Titan version write threw; recapture the selector before retry",
                    null, error);
            }
        }

        internal NativeInvocationResult TossMoneyPit(object pitController)
        {
            return _registry.InvokeMutation(NativeBindingKeys.MoneyPitEngage, pitController);
        }

        internal NativeInvocationResult ClaimDailySpin(object dailyRewardController)
        {
            return _registry.InvokeMutation(NativeBindingKeys.DailySpinClaim,
                dailyRewardController);
        }

        internal NativeInvocationResult ConsumeCard(object cardsController, int exactIndex)
        {
            return _registry.InvokeMutation(NativeBindingKeys.CardConsume, cardsController, exactIndex);
        }

        internal NativeInvocationResult LoadSave(object openFileDialog, object saveData)
        {
            return _registry.InvokeMutation(NativeBindingKeys.LoadIntoGame, openFileDialog, saveData);
        }

        internal NativeInvocationResult SwitchWandoosOperatingSystem(object controller, int osId)
        {
            return SelectAndInvoke(controller, NativeBindingKeys.WandoosNextOs, osId,
                NativeBindingKeys.WandoosSetOs);
        }

        internal NativeInvocationResult BuyYggdrasilFruit(object controller, int fruitId)
        {
            return SelectAndInvoke(controller, NativeBindingKeys.YggFruitToBuy, fruitId,
                NativeBindingKeys.YggBuyFruit);
        }

        internal NativeInvocationResult BuyPermanentUpgrade(object controller, string exactMethodName)
        {
            if (controller == null || string.IsNullOrEmpty(exactMethodName))
                return Invalid("purchase target and exact method name are required");
            var type = controller.GetType();
            var key = NativeBindingKeys.PurchaseMethod(type.FullName, exactMethodName);
            NativeBindingDescriptor descriptor;
            if (!_registry.TryGetDescriptor(key, out descriptor)
                || descriptor.Scope != NativeBindingScope.IrreversibleMutation)
                return Invalid("purchase is outside the exact build-pinned catalog: " + key);
            return _registry.InvokeMutation(key, controller);
        }

        /*
        EXACT CUSTOM-PURCHASE SELECTOR TRANSACTION

        The Energy/Magic/R3 custom shop methods debit whatever amount is currently parsed from
        their InputField.  A build-pinned method call is therefore not sufficient by itself: the
        field and its parser are part of the irreversible purchase boundary.  Resolve both through
        the audited registry, install one exact positive integer, parse it, invoke the purchase,
        and restore the user's prior text/parsed value in finally.  The caller still has to prove
        the currency and permanent-stat deltas through MutationCoordinator.
        */
        internal NativeInvocationResult BuyPermanentUpgradeWithExactInput(object controller,
            string exactMethodName, string exactInputFieldName,
            string exactInputUpdateMethodName, long exactAmount)
        {
            if (controller == null || string.IsNullOrEmpty(exactMethodName)
                || string.IsNullOrEmpty(exactInputFieldName)
                || string.IsNullOrEmpty(exactInputUpdateMethodName) || exactAmount <= 0L)
                return Invalid("custom purchase target, binding names, and positive amount are required");

            var typeName = controller.GetType().FullName;
            FieldInfo inputField;
            string reason;
            var fieldKey = NativeBindingKeys.PurchaseInput(typeName, exactInputFieldName);
            if (!_registry.TryGetBoundField(fieldKey, out inputField, out reason))
                return Unavailable(fieldKey, reason);
            if (!inputField.DeclaringType.IsInstanceOfType(controller))
                return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    fieldKey, "custom purchase selector target type does not match", null, null);
            var input = inputField.GetValue(controller);
            var textProperty = input == null ? null : input.GetType().GetProperty("text",
                BindingFlags.Instance | BindingFlags.Public);
            if (input == null || textProperty == null || !textProperty.CanRead
                || !textProperty.CanWrite || textProperty.PropertyType != typeof(string))
                return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    fieldKey, "custom purchase selector is not a Unity InputField", null, null);

            var updateKey = NativeBindingKeys.PurchaseInputUpdate(typeName,
                exactInputUpdateMethodName);
            var previousText = textProperty.GetValue(input, null) as string;
            NativeInvocationResult result;
            try
            {
                textProperty.SetValue(input, exactAmount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), null);
                var parsed = _registry.InvokeMutation(updateKey, controller);
                if (!parsed.ReturnedNormally) return parsed;
                result = BuyPermanentUpgrade(controller, exactMethodName);
            }
            catch (Exception error)
            {
                result = new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    NativeBindingKeys.PurchaseMethod(typeName, exactMethodName),
                    "custom purchase selector/invocation threw; recapture before retry",
                    null, error);
            }
            try
            {
                textProperty.SetValue(input, previousText ?? string.Empty, null);
                var restored = _registry.InvokeMutation(updateKey, controller);
                if (!restored.ReturnedNormally)
                    return new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                        updateKey, "custom purchase selector restoration did not complete",
                        result.ReturnValue, restored.Exception);
            }
            catch (Exception restoreError)
            {
                return new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    updateKey, "custom purchase selector restoration threw",
                    result.ReturnValue, restoreError);
            }
            return result;
        }

        internal NativeInvocationResult BuyApUpgrade(object controller, int exactId,
            string exactNativeName, string exactMethodName)
        {
            if (!_registry.IsKnownBuild || !_registry.IrreversibleActionsEnabled)
                return _registry.InvokeMutation(
                    NativeBindingKeys.PurchaseMethod("ArbitraryController", exactMethodName), controller);
            FieldInfo idField;
            FieldInfo nameField;
            string reason;
            if (!_registry.TryGetBoundField(NativeBindingKeys.ApPurchaseId, out idField, out reason))
                return Unavailable(NativeBindingKeys.ApPurchaseId, reason);
            if (!_registry.TryGetBoundField(NativeBindingKeys.ApPurchaseName, out nameField, out reason))
                return Unavailable(NativeBindingKeys.ApPurchaseName, reason);
            if (controller == null || !idField.DeclaringType.IsInstanceOfType(controller)
                || !nameField.DeclaringType.IsInstanceOfType(controller))
                return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    NativeBindingKeys.ApPurchaseId, "target is not an ArbitraryController", null, null);

            var previousId = idField.GetValue(controller);
            var previousName = nameField.GetValue(controller);
            NativeInvocationResult result;
            try
            {
                idField.SetValue(controller, exactId);
                nameField.SetValue(controller, exactNativeName ?? string.Empty);
                result = BuyPermanentUpgrade(controller, exactMethodName);
            }
            catch (Exception error)
            {
                result = new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    NativeBindingKeys.PurchaseMethod("ArbitraryController", exactMethodName),
                    "AP selector/invocation threw; recapture currency and ownership before retry",
                    null, error);
            }
            try
            {
                idField.SetValue(controller, previousId);
                nameField.SetValue(controller, previousName);
            }
            catch (Exception restoreError)
            {
                return new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    NativeBindingKeys.PurchaseMethod("ArbitraryController", exactMethodName),
                    "AP selector restoration threw; selector state is indeterminate",
                    result.ReturnValue, restoreError);
            }
            return result;
        }

        internal NativeInvocationResult BuyOneQuirkLevel(object controller, int exactId)
        {
            if (exactId < 0)
                return Invalid("quirk ID must be nonnegative");
            return _registry.InvokeMutation(NativeBindingKeys.QuirkTryLevelUp,
                controller, exactId);
        }

        private NativeInvocationResult SelectAndInvoke(object controller, string selectorKey,
            object selectorValue, string methodKey)
        {
            if (!_registry.IsKnownBuild || !_registry.IrreversibleActionsEnabled)
                return _registry.InvokeMutation(methodKey, controller);
            FieldInfo selector;
            string reason;
            if (!_registry.TryGetBoundField(selectorKey, out selector, out reason))
                return Unavailable(selectorKey, reason);
            if (controller == null || !selector.DeclaringType.IsInstanceOfType(controller))
                return new NativeInvocationResult(NativeInvocationStatus.TargetMismatch,
                    selectorKey, "selector target type does not match", null, null);
            var previous = selector.GetValue(controller);
            NativeInvocationResult result;
            try
            {
                selector.SetValue(controller, selectorValue);
                result = _registry.InvokeMutation(methodKey, controller);
            }
            catch (Exception error)
            {
                result = new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    methodKey, "selector/native invocation threw; recapture before retry",
                    null, error);
            }
            try
            {
                selector.SetValue(controller, previous);
            }
            catch (Exception restoreError)
            {
                return new NativeInvocationResult(NativeInvocationStatus.ThrewAfterInvocation,
                    methodKey, "selector restoration threw; selector state is indeterminate",
                    result.ReturnValue, restoreError);
            }
            return result;
        }

        private static NativeInvocationResult Invalid(string reason)
        {
            return new NativeInvocationResult(NativeInvocationStatus.BindingUnavailable,
                string.Empty, reason, null, null);
        }

        private static NativeInvocationResult Unavailable(string key, string reason)
        {
            return new NativeInvocationResult(NativeInvocationStatus.BindingUnavailable,
                key, reason, null, null);
        }
    }
}
