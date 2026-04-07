using System;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Ffxiv2Mqtt.Topics.Interfaces;

namespace Ffxiv2Mqtt.Topics.Events;

/// <summary>
/// Publishes the GatherBuddyReborn AutoGather status to MQTT.
///
/// MQTT topic:  ffxiv/Plugin/GatherBuddy/AutoGather  (retained)
/// Payloads:
///   "Idle"    – AutoGather is disabled (stable state)
///   "Start"   – AutoGather was just enabled (one-frame transition)
///   "Farming" – AutoGather is actively running (stable state)
///   "End"     – AutoGather was just disabled (one-frame transition)
///
/// Gracefully handles GatherBuddyReborn not being installed: every IPC
/// call is individually guarded with try/catch. If registration fails the
/// class stays in Idle with no active subscriptions and no crash.
/// </summary>
internal sealed class GatherBuddyAutoGather : Topic, ICleanable, IDisposable
{
    // ── MQTT ─────────────────────────────────────────────────────────────────
    protected override string TopicPath => "Plugin/GatherBuddy/AutoGather";
    protected override bool   Retained  => true;

    // ── IPC labels ────────────────────────────────────────────────────────────
    private const string LabelIsEnabled      = "GatherBuddyReborn.IsAutoGatherEnabled";
    private const string LabelEnabledChanged = "GatherBuddyReborn.AutoGatherEnabledChanged";
    private const string LabelWaiting        = "GatherBuddyReborn.AutoGatherWaiting";

    // ── State machine ─────────────────────────────────────────────────────────
    private enum AutoGatherState { Idle, Start, Farming, End }
    private AutoGatherState _state = AutoGatherState.Idle;

    // ── IPC subscriber handles ────────────────────────────────────────────────
    // Stored so we can call Unsubscribe with the same delegate reference on Dispose.
    // Dalamud IPC uses reference equality — do NOT inline the lambda at call site.
    private ICallGateSubscriber<bool, object>? _enabledChangedSubscriber;
    private ICallGateSubscriber<object>?       _waitingSubscriber;

    private readonly Action<bool> _onEnabledChanged;
    private readonly Action       _onWaiting;

    private bool _frameworkUpdateRegistered;

    // ─────────────────────────────────────────────────────────────────────────

    public GatherBuddyAutoGather()
    {
        _onEnabledChanged = OnEnabledChanged;
        _onWaiting        = OnWaiting;

        // ── Read initial state ────────────────────────────────────────────────
        try {
            var isEnabled = Service.PluginInterface
                                   .GetIpcSubscriber<bool>(LabelIsEnabled)
                                   .InvokeFunc();
            _state = isEnabled ? AutoGatherState.Farming : AutoGatherState.Idle;
            Publish(_state.ToString());
        } catch (Exception ex) {
            Service.Log.Warning(
                $"[GatherBuddyAutoGather] Could not read initial AutoGather state " +
                $"(GatherBuddyReborn may not be installed): {ex.Message}");
        }

        // ── Subscribe to AutoGatherEnabledChanged ─────────────────────────────
        try {
            _enabledChangedSubscriber =
                Service.PluginInterface.GetIpcSubscriber<bool, object>(LabelEnabledChanged);
            _enabledChangedSubscriber.Subscribe(_onEnabledChanged);
        } catch (Exception ex) {
            Service.Log.Warning(
                $"[GatherBuddyAutoGather] Could not subscribe to {LabelEnabledChanged}: {ex.Message}");
        }

        // ── Subscribe to AutoGatherWaiting ────────────────────────────────────
        try {
            _waitingSubscriber =
                Service.PluginInterface.GetIpcSubscriber<object>(LabelWaiting);
            _waitingSubscriber.Subscribe(_onWaiting);
        } catch (Exception ex) {
            Service.Log.Warning(
                $"[GatherBuddyAutoGather] Could not subscribe to {LabelWaiting}: {ex.Message}");
        }

        // Register Framework.Update only when at least one IPC subscription succeeded
        // to avoid a useless tick running every frame when GatherBuddyReborn is absent.
        if (_enabledChangedSubscriber is not null || _waitingSubscriber is not null) {
            Service.Framework.Update += OnFrameworkUpdate;
            _frameworkUpdateRegistered = true;
        }
    }

    // ── IPC event handlers ────────────────────────────────────────────────────

    private void OnEnabledChanged(bool enabled)
    {
        if (enabled) {
            _state = AutoGatherState.Start;
            Publish("Start");
        } else {
            _state = AutoGatherState.End;
            Publish("End");
        }
    }

    /// <summary>
    /// Fired by GatherBuddyReborn while AutoGather is active and waiting
    /// between gather attempts. The state is already Farming at this point;
    /// extend here if a "Waiting" sub-state is ever required.
    /// </summary>
    private void OnWaiting() { }

    // ── Framework tick: advance one-frame transition states ───────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        switch (_state) {
            case AutoGatherState.Start:
                _state = AutoGatherState.Farming;
                Publish("Farming");
                break;

            case AutoGatherState.End:
                _state = AutoGatherState.Idle;
                Publish("Idle");
                break;
        }
    }

    // ── ICleanable ────────────────────────────────────────────────────────────

    /// <summary>Wipes the retained MQTT message when the plugin disconnects.</summary>
    public void Cleanup() { Publish(""); }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_frameworkUpdateRegistered) {
            Service.Framework.Update  -= OnFrameworkUpdate;
            _frameworkUpdateRegistered =  false;
        }

        try { _enabledChangedSubscriber?.Unsubscribe(_onEnabledChanged); }
        catch (Exception ex) {
            Service.Log.Warning(
                $"[GatherBuddyAutoGather] Error unsubscribing from {LabelEnabledChanged}: {ex.Message}");
        }

        try { _waitingSubscriber?.Unsubscribe(_onWaiting); }
        catch (Exception ex) {
            Service.Log.Warning(
                $"[GatherBuddyAutoGather] Error unsubscribing from {LabelWaiting}: {ex.Message}");
        }
    }
}
