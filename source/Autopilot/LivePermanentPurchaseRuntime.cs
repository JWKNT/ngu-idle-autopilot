/*
FILE PURPOSE

LivePermanentPurchaseRuntime is the audited Character adapter for the permanent-purchase
transaction model.  It captures exact EXP plus the complete effect vector for the small set of
live-enabled, source-proven early progression atoms, invokes only NativeMutationAdapters, and
re-captures the same vector for MutationCoordinator settlement.  The policy selector remains in
AutopilotManager; this file does not rank purchases or grant authority.

The adapter intentionally supports only state keys with an exact integral representation.  Energy
Power +0.1 is represented in tenths; Energy Bars and Cap are integral.  Unsupported descriptors
return no snapshot and therefore fail closed before a debit.  Custom-input purchases use the
build-pinned field/parser transaction in NativeMutationAdapters, which restores the user's prior
input synchronously.  A normal native return is never success: the manager still requires the
exact EXP debit and declared permanent-stat delta.
*/
using System;
using System.Collections.Generic;

namespace NGUInjector.Autopilot
{
    internal sealed class LivePermanentPurchaseRuntime : IPermanentPurchaseRuntime
    {
        private readonly Character _character;
        private readonly object _controller;
        private readonly PurchaseCostState _costState;
        private readonly NativeMutationAdapters _native;

        internal LivePermanentPurchaseRuntime(Character character, object controller,
            PurchaseCostState costState)
        {
            _character = character;
            _controller = controller;
            _costState = costState;
            _native = NativeBindingRegistry.Create(typeof(Character).Assembly,
                Main.GameAssemblySha256).CreateMutationAdapters();
        }

        public PurchaseBoundarySnapshot Capture(PurchaseDescriptor descriptor)
        {
            if (_character == null || descriptor == null || _controller == null
                || _costState == null || _character.purchases == null)
                return null;
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            var effects = descriptor.Effects();
            for (var i = 0; i < effects.Length; i++)
            {
                long value;
                if (!TryRead(effects[i].StateKey, out value)) return null;
                values.Add(effects[i].StateKey, value);
            }
            long liveCost;
            try { liveCost = descriptor.Cost.Evaluate(_costState); }
            catch { return null; }
            var balance = descriptor.Currency == PermanentCurrency.Experience
                ? _character.realExp : _character.arbitrary.curArbitraryPoints;
            return new PurchaseBoundarySnapshot(Main.GameAssemblySha256,
                typeof(Character).Assembly.ManifestModule.ModuleVersionId,
                descriptor.NativeId, descriptor.NativeMethodName, liveCost, _costState,
                new PurchaseStateVector(balance, values), _character.purchases.hasAutoMerge,
                null, false, false);
        }

        public PurchaseInvocation Invoke(RootTransactionToken token,
            PurchaseDescriptor descriptor, int temporaryHeartFilterExemptionItemId)
        {
            if (descriptor == null || _controller == null)
                return PurchaseInvocation.Held(string.Empty,
                    "live purchase descriptor/controller is unavailable");
            NativeInvocationResult result;
            string input;
            string update;
            if (TryCustomInput(descriptor.Cost.Kind, out input, out update))
            {
                result = _native.BuyPermanentUpgradeWithExactInput(_controller,
                    descriptor.NativeMethodName, input, update, _costState.Amount);
            }
            else
            {
                result = _native.BuyPermanentUpgrade(_controller,
                    descriptor.NativeMethodName);
            }
            if (result == null)
                return PurchaseInvocation.Held(descriptor.NativeBindingKey,
                    "native purchase adapter returned no result");
            if (result.Status == NativeInvocationStatus.ThrewAfterInvocation)
                return PurchaseInvocation.Threw(result.BindingKey,
                    result.Exception ?? new InvalidOperationException(result.Reason));
            return result.ReturnedNormally
                ? PurchaseInvocation.Invoked(result.BindingKey)
                : PurchaseInvocation.Held(result.BindingKey, result.Reason);
        }

        internal static PurchaseStateVector ExpectedAfter(PurchaseDescriptor descriptor,
            PurchaseBoundarySnapshot before)
        {
            if (descriptor == null || before == null || before.State == null) return null;
            var values = before.State.ValuesCopy();
            var effects = descriptor.Effects();
            for (var i = 0; i < effects.Length; i++)
            {
                long value;
                if (!values.TryGetValue(effects[i].StateKey, out value)) return null;
                switch (effects[i].Kind)
                {
                    case PurchaseEffectKind.ExactDelta:
                        values[effects[i].StateKey] = checked(value + effects[i].Amount);
                        break;
                    case PurchaseEffectKind.SetOne:
                        if (value != 0L) return null;
                        values[effects[i].StateKey] = 1L;
                        break;
                    case PurchaseEffectKind.CappedDelta:
                        values[effects[i].StateKey] = Math.Min(effects[i].Maximum,
                            checked(value + effects[i].Amount));
                        break;
                    case PurchaseEffectKind.CostStateAmountDelta:
                        if (before.CostState == null || before.CostState.Amount <= 0L)
                            return null;
                        values[effects[i].StateKey] = checked(value
                            + before.CostState.Amount);
                        break;
                    default:
                        return null;
                }
            }
            return new PurchaseStateVector(before.State.CurrencyBalance - before.LiveCost,
                values);
        }

        private bool TryRead(string key, out long value)
        {
            value = 0L;
            switch (key)
            {
                case "permanent.energyPowerTenths":
                    value = (long)Math.Round(_character.energyPower * 10.0,
                        MidpointRounding.AwayFromZero);
                    return true;
                case "permanent.energyBars": value = _character.energyBars; return true;
                case "permanent.energyCap": value = _character.capEnergy; return true;
                case "permanent.energySpeed":
                    value = (long)Math.Round(_character.energySpeed * 100.0,
                        MidpointRounding.AwayFromZero);
                    return true;
                case "exp.hasBasicFilter": value = _character.purchases.hasFilter ? 1L : 0L; return true;
                case "exp.hasRecycle": value = _character.purchases.boost >= .5f ? 1L : 0L; return true;
                case "exp.hasAccessory3": value = _character.purchases.hasAcc3 ? 1L : 0L; return true;
                case "exp.hasAutoMerge": value = _character.purchases.hasAutoMerge ? 1L : 0L; return true;
                case "exp.hasAccessory5": value = _character.purchases.hasAcc5 ? 1L : 0L; return true;
                case "exp.hasDaycare": value = _character.purchases.hasDaycare ? 1L : 0L; return true;
                case "exp.hasDaycareSlot2": value = _character.purchases.hasDaycareSlot2 ? 1L : 0L; return true;
                case "exp.hasDaycareSlot3": value = _character.purchases.hasDaycareSlot3 ? 1L : 0L; return true;
                case "exp.hasInventoryMerge": value = _character.purchases.hasInvMerge ? 1L : 0L; return true;
                default: return false;
            }
        }

        private static bool TryCustomInput(PurchaseCostKind kind, out string field,
            out string update)
        {
            if (kind == PurchaseCostKind.EnergyPower || kind == PurchaseCostKind.MagicPower
                || kind == PurchaseCostKind.Resource3Power)
            {
                field = "powerInput"; update = "updateCustomPowerInput"; return true;
            }
            if (kind == PurchaseCostKind.EnergyCap || kind == PurchaseCostKind.MagicCap
                || kind == PurchaseCostKind.Resource3Cap)
            {
                field = "capInput"; update = "updateCustomCapInput"; return true;
            }
            if (kind == PurchaseCostKind.EnergyBar || kind == PurchaseCostKind.MagicBar
                || kind == PurchaseCostKind.Resource3Bar)
            {
                field = "barInput"; update = "updateCustomBarInput"; return true;
            }
            field = string.Empty; update = string.Empty; return false;
        }
    }
}
