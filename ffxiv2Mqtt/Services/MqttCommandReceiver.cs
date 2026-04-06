using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Subscribes to the MQTT command topic and forwards received payloads
/// into the FFXIV in-game chat / command pipeline.
///
/// Topic:   ffxiv/Command/Chat  (or ffxiv/{ClientId}/Command/Chat)
///
/// Payload rules:
///   Starts with '/'  -> executed as slash command via ProcessCommand or ChatBox
///   Plain text       -> printed to local chat with [MQTT] prefix
/// </summary>
public sealed unsafe class MqttCommandReceiver : IDisposable
{
    private readonly IMqttClientWrapper mqttClient;
    private readonly ICommandManager    commandManager;
    private readonly IChatGui           chatGui;
    private readonly IClientState       clientState;
    private readonly IFramework         framework;
    private readonly IPluginLog         log;
    private readonly Configuration      config;

    private string subscribedTopic = string.Empty;

    public MqttCommandReceiver(
        IMqttClientWrapper mqttClient,
        ICommandManager    commandManager,
        IChatGui           chatGui,
        IClientState       clientState,
        IFramework         framework,
        IPluginLog         log,
        Configuration      config)
    {
        this.mqttClient     = mqttClient;
        this.commandManager = commandManager;
        this.chatGui        = chatGui;
        this.clientState    = clientState;
        this.framework      = framework;
        this.log            = log;
        this.config         = config;

        this.mqttClient.Connected       += OnConnected;
        this.mqttClient.Disconnected    += OnDisconnected;
        this.mqttClient.MessageReceived += OnMessageReceived;
    }

    private string BuildTopic()
    {
        var prefix = config.IncludeClientId && !string.IsNullOrWhiteSpace(config.ClientId)
            ? $"ffxiv/{config.ClientId}"
            : "ffxiv";
        return $"{prefix}/Command/Chat";
    }

    private async void OnConnected(object? sender, EventArgs e)
    {
        if (!config.EnableCommandReceiver)
        {
            log.Debug("[CommandReceiver] Disabled in config.");
            return;
        }
        subscribedTopic = BuildTopic();
        try
        {
            await mqttClient.SubscribeAsync(subscribedTopic, MqttQualityOfServiceLevel.AtLeastOnce);
            log.Information($"[CommandReceiver] Subscribed to '{subscribedTopic}'");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CommandReceiver] Failed to subscribe.");
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        subscribedTopic = string.Empty;
    }

    private void OnMessageReceived(object? sender, MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        if (!string.Equals(topic, subscribedTopic, StringComparison.OrdinalIgnoreCase))
            return;

        var raw = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();

        // Strip surrounding quotes that Home Assistant may add
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            log.Warning("[CommandReceiver] Empty payload – ignored.");
            return;
        }

        if (raw.Length > 500)
        {
            log.Warning($"[CommandReceiver] Payload too long ({raw.Length}) – ignored.");
            return;
        }

        log.Debug($"[CommandReceiver] Received: {raw}");
        framework.RunOnFrameworkThread(() => Execute(raw));
    }

    private void Execute(string payload)
    {
#pragma warning disable CS0618
        if (clientState.LocalPlayer == null)
#pragma warning restore CS0618
        {
            log.Warning("[CommandReceiver] No player logged in – ignored.");
            return;
        }

        if (payload.StartsWith('/'))
        {
            // 1. Try Dalamud-registered commands (plugin commands)
            if (commandManager.ProcessCommand(payload))
            {
                log.Debug($"[CommandReceiver] Dalamud command executed: {payload}");
                return;
            }

            // 2. Native game command via UIModule (same as typing in chat box)
            SendNativeCommand(payload);
        }
        else
        {
            // Plain text: print to local chat
            var msg = new SeStringBuilder()
                .AddUiForeground("[MQTT] ", 32)
                .AddText(payload)
                .Build();
            chatGui.Print(new XivChatEntry { Message = msg });
        }
    }

    private void SendNativeCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                log.Error("[CommandReceiver] UIModule unavailable.");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(command + "\0");
            fixed (byte* ptr = bytes)
            {
                var ptrInt = (nint)ptr;
                uiModule->ProcessChatBoxEntry((Utf8String*)&ptrInt);
            }
            log.Debug($"[CommandReceiver] Native command sent: {command}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[CommandReceiver] Failed to send native command: {command}");
            chatGui.PrintError($"[MQTT] Command failed: {command}");
        }
    }

    public void Dispose()
    {
        mqttClient.Connected       -= OnConnected;
        mqttClient.Disconnected    -= OnDisconnected;
        mqttClient.MessageReceived -= OnMessageReceived;
    }
}